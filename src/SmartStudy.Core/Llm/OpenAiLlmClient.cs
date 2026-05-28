using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartStudy.Core.Configuration;

namespace SmartStudy.Core.Llm;

/// <summary>
/// 调用任何 OpenAI Chat Completions 兼容端点（智谱 GLM、DeepSeek、OpenAI、Ollama 等）。
/// 支持 function calling 与 SSE 流式输出。
/// </summary>
public sealed class OpenAiLlmClient : ILlmClient
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly LlmProfileManager _profiles;
    private readonly ILogger<OpenAiLlmClient> _logger;

    public OpenAiLlmClient(HttpClient http, LlmProfileManager profiles, ILogger<OpenAiLlmClient> logger)
    {
        _http = http;
        _profiles = profiles;
        _logger = logger;
        _http.Timeout = TimeSpan.FromSeconds(120);
    }

    public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
    {
        var opts = _profiles.Current;
        request.Model = string.IsNullOrEmpty(request.Model) ? opts.Model : request.Model;
        request.Stream = false;
        _logger.LogDebug("LLM 非流式请求 model={Model} msgs={N}", request.Model, request.Messages.Count);

        using var req = BuildRequest(opts, request);
        using var resp = await _http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"LLM 返回错误 {(int)resp.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var choice = doc.RootElement.GetProperty("choices")[0];
        var msg = choice.GetProperty("message");
        var result = new ChatResponse
        {
            Content = msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null,
            FinishReason = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() ?? "" : ""
        };
        if (msg.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
        {
            foreach (var tc in tcs.EnumerateArray())
            {
                result.ToolCalls.Add(new ToolCall
                {
                    Id = tc.GetProperty("id").GetString() ?? "",
                    Type = tc.TryGetProperty("type", out var t) ? t.GetString() ?? "function" : "function",
                    Function = new ToolCallFunction
                    {
                        Name = tc.GetProperty("function").GetProperty("name").GetString() ?? "",
                        Arguments = tc.GetProperty("function").GetProperty("arguments").GetString() ?? "{}"
                    }
                });
            }
        }
        return result;
    }

    public async IAsyncEnumerable<ChatStreamChunk> ChatStreamAsync(ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var opts = _profiles.Current;
        request.Model = string.IsNullOrEmpty(request.Model) ? opts.Model : request.Model;
        request.Stream = true;

        using var req = BuildRequest(opts, request);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"LLM 流式返回错误 {(int)resp.StatusCode}: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // 工具调用在流中按 index 分片聚合
        var toolBuffers = new Dictionary<int, ToolCall>();

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line[5..].Trim();
            if (payload == "[DONE]") yield break;

            ChatStreamChunk? chunk = null;
            try { chunk = ParseStreamChunk(payload, toolBuffers); }
            catch (Exception ex) { _logger.LogWarning(ex, "解析流式分片失败：{Line}", payload); }
            if (chunk != null) yield return chunk;
        }
    }

    private static HttpRequestMessage BuildRequest(LlmOptions opts, ChatRequest request)
    {
        var endpoint = new Uri(opts.BaseUrl.TrimEnd('/') + "/chat/completions");
        var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(request, options: JsonOpts)
        };
        if (!string.IsNullOrWhiteSpace(opts.ApiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.ApiKey);
        return message;
    }

    private static ChatStreamChunk? ParseStreamChunk(string payload, Dictionary<int, ToolCall> toolBuffers)
    {
        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return null;
        var choice = choices[0];
        var chunk = new ChatStreamChunk();
        if (choice.TryGetProperty("delta", out var delta))
        {
            if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                chunk.ContentDelta = c.GetString();
            if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcs.EnumerateArray())
                {
                    var idx = tc.TryGetProperty("index", out var i) ? i.GetInt32() : 0;
                    if (!toolBuffers.TryGetValue(idx, out var buf))
                    {
                        buf = new ToolCall
                        {
                            Id = "",
                            Function = new ToolCallFunction { Name = "", Arguments = "" }
                        };
                        toolBuffers[idx] = buf;
                    }
                    if (tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        buf.Id = id.GetString() ?? buf.Id;
                    if (tc.TryGetProperty("function", out var fn))
                    {
                        if (fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                            buf.Function.Name += n.GetString();
                        if (fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String)
                            buf.Function.Arguments += a.GetString();
                    }
                }
                chunk.ToolCallsAccumulated = toolBuffers.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
            }
        }
        if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
            chunk.FinishReason = fr.GetString();
        return chunk;
    }
}
