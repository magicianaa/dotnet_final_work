using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartStudy.Core.Configuration;

namespace SmartStudy.Core.Rag;

public interface IEmbeddingClient
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default);
}

/// <summary>
/// 完全离线的本地 embedding：把中英文 token 哈希到固定维度向量，再做 L2 归一化。
/// 这不是神经网络语义向量，但对课程资料这类小型知识库足够演示本地 RAG 检索。
/// </summary>
public sealed class LocalHashEmbeddingClient : IEmbeddingClient
{
    private readonly int _dimensions;

    public LocalHashEmbeddingClient(IOptions<AgentOptions> opts)
    {
        _dimensions = Math.Max(64, opts.Value.Embedding.LocalDimensions);
    }

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) =>
        Task.FromResult(Embed(text));

    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<float[]>>(texts.Select(Embed).ToList());

    private float[] Embed(string text)
    {
        var vector = new float[_dimensions];
        foreach (var token in Tokenize(text))
        {
            var hash = StableHash(token);
            var index = (int)(hash % (uint)_dimensions);
            var sign = (hash & 0x80000000u) == 0 ? 1f : -1f;
            vector[index] += sign;
        }

        double norm = 0;
        for (int i = 0; i < vector.Length; i++) norm += vector[i] * vector[i];
        if (norm == 0) return vector;

        var scale = (float)(1.0 / Math.Sqrt(norm));
        for (int i = 0; i < vector.Length; i++) vector[i] *= scale;
        return vector;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var lower = text.ToLowerInvariant();
        var word = new System.Text.StringBuilder();
        var cjkBuffer = new Queue<char>();

        foreach (var ch in lower)
        {
            if (IsAsciiLetterOrDigit(ch))
            {
                word.Append(ch);
                cjkBuffer.Clear();
                continue;
            }

            if (word.Length > 0)
            {
                yield return word.ToString();
                word.Clear();
            }

            if (IsCjk(ch))
            {
                cjkBuffer.Enqueue(ch);
                if (cjkBuffer.Count == 2)
                {
                    yield return new string(cjkBuffer.ToArray());
                    cjkBuffer.Dequeue();
                }
            }
            else
            {
                cjkBuffer.Clear();
            }
        }

        if (word.Length > 0) yield return word.ToString();
    }

    private static bool IsAsciiLetterOrDigit(char ch) =>
        ch is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool IsCjk(char ch) =>
        ch is >= '\u4e00' and <= '\u9fff';

    private static uint StableHash(string token)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(token);
        var hash = SHA256.HashData(bytes);
        return BitConverter.ToUInt32(hash, 0);
    }
}

/// <summary>智谱 embedding-3（OpenAI 兼容）。POST /embeddings { input, model }。</summary>
public sealed class ZhipuEmbeddingClient : IEmbeddingClient
{
    private readonly HttpClient _http;
    private readonly EmbeddingOptions _opts;
    private readonly ILogger<ZhipuEmbeddingClient> _logger;
    private readonly int _dimensions;

    public ZhipuEmbeddingClient(HttpClient http, IOptions<AgentOptions> opts, ILogger<ZhipuEmbeddingClient> logger)
    {
        _http = http;
        _opts = opts.Value.Embedding;
        _logger = logger;
        _dimensions = NormalizeDimensions(_opts.LocalDimensions);
        if (_http.BaseAddress is null) _http.BaseAddress = new Uri(_opts.BaseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _opts.ApiKey);
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var list = await EmbedBatchAsync(new[] { text }, ct);
        return list[0];
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var input = texts.Select(SanitizeInput).ToList();
        var results = new List<float[]>(input.Count);
        // 智谱 embedding-3 单次最大 64 条
        const int batch = 32;
        for (int i = 0; i < input.Count; i += batch)
        {
            var slice = input.Skip(i).Take(batch).ToList();
            results.AddRange(await EmbedSliceWithRetryAsync(slice, ct));
        }
        return results;
    }

    private async Task<IReadOnlyList<float[]>> EmbedSliceWithRetryAsync(IReadOnlyList<string> input, CancellationToken ct)
    {
        try
        {
            return await EmbedSliceAsync(input, ct);
        }
        catch (HttpRequestException ex) when (input.Count > 1 && IsRetryableParameterError(ex))
        {
            var mid = input.Count / 2;
            _logger.LogWarning(ex, "智谱 embedding 批量请求失败，拆分为 {Left}+{Right} 条后重试", mid, input.Count - mid);
            var left = await EmbedSliceWithRetryAsync(input.Take(mid).ToList(), ct);
            var right = await EmbedSliceWithRetryAsync(input.Skip(mid).ToList(), ct);
            return left.Concat(right).ToList();
        }
    }

    private async Task<IReadOnlyList<float[]>> EmbedSliceAsync(IReadOnlyList<string> input, CancellationToken ct)
    {
        if (input.Count == 0)
            return Array.Empty<float[]>();

        var body = new
        {
            model = _opts.Model,
            input = input,
            dimensions = _dimensions
        };
        using var resp = await _http.PostAsJsonAsync("embeddings", body, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var maxChars = input.Max(t => t.Length);
            throw new HttpRequestException(
                $"Embedding 错误 {(int)resp.StatusCode}: {raw}。本批 {input.Count} 条，最长 {maxChars} 字符；请检查文本是否过长或模型参数是否支持。");
        }

        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.GetProperty("data")
            .EnumerateArray()
            .OrderBy(d => d.TryGetProperty("index", out var index) ? index.GetInt32() : 0)
            .Select(d => d.GetProperty("embedding").EnumerateArray().Select(x => (float)x.GetDouble()).ToArray())
            .ToList();
    }

    private static string SanitizeInput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return ".";

        return text.Replace('\0', ' ').Trim();
    }

    private static bool IsRetryableParameterError(HttpRequestException ex) =>
        ex.Message.Contains("400", StringComparison.Ordinal) ||
        ex.Message.Contains("\"1210\"", StringComparison.Ordinal) ||
        ex.Message.Contains("参数", StringComparison.Ordinal);

    private static int NormalizeDimensions(int dimensions) =>
        dimensions switch
        {
            256 or 512 or 1024 or 2048 => dimensions,
            <= 256 => 256,
            <= 512 => 512,
            <= 1024 => 1024,
            _ => 2048
        };
}
