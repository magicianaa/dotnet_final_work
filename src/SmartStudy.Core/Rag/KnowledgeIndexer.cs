using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmartStudy.Core.Configuration;

namespace SmartStudy.Core.Rag;

/// <summary>把 knowledge/*.md 切片、向量化、写入向量库。</summary>
public sealed class KnowledgeIndexer
{
    private readonly IEmbeddingClient _embed;
    private readonly IVectorStore _store;
    private readonly IRagRuntimeContext _rag;
    private readonly ILogger<KnowledgeIndexer> _logger;

    [ActivatorUtilitiesConstructor]
    public KnowledgeIndexer(IEmbeddingClient embed, IVectorStore store,
        IRagRuntimeContext rag, ILogger<KnowledgeIndexer> logger)
    {
        _embed = embed; _store = store; _rag = rag; _logger = logger;
    }

    public KnowledgeIndexer(IEmbeddingClient embed, IVectorStore store,
        IOptions<AgentOptions> opts, ILogger<KnowledgeIndexer> logger)
        : this(embed, store, new DefaultRagRuntimeContext(opts), logger)
    {
    }

    public int Count => _store.Count;

    public async Task BuildAsync(CancellationToken ct = default)
    {
        var opts = _rag.Current;
        if (!Directory.Exists(opts.KnowledgeDirectory))
        {
            _logger.LogWarning("知识目录不存在：{Dir}", opts.KnowledgeDirectory);
            return;
        }
        var files = Directory.GetFiles(opts.KnowledgeDirectory, "*.md", SearchOption.AllDirectories);
        var chunks = new List<KnowledgeChunk>();
        foreach (var f in files)
        {
            var text = await File.ReadAllTextAsync(f, ct);
            var name = Path.GetFileName(f);
            foreach (var (chunkText, idx) in Chunk(text, opts.ChunkSize, opts.ChunkOverlap).Select((c, i) => (c, i)))
            {
                chunks.Add(new KnowledgeChunk { Id = $"{name}#{idx}", Source = name, Text = chunkText });
            }
        }
        _logger.LogInformation("准备 embed {N} 个片段（来自 {F} 个文件）", chunks.Count, files.Length);
        var vectors = await _embed.EmbedBatchAsync(chunks.Select(c => c.Text), ct);
        for (int i = 0; i < chunks.Count; i++) chunks[i].Vector = vectors[i];
        _store.Replace(chunks);
        await _store.SaveAsync(opts.IndexFile, ct);
        _logger.LogInformation("索引已写入 {Path}", opts.IndexFile);
    }

    public async Task<bool> LoadIfExistsAsync(CancellationToken ct = default)
    {
        var opts = _rag.Current;
        if (!File.Exists(opts.IndexFile)) return false;
        await _store.LoadAsync(opts.IndexFile, ct);
        return _store.Count > 0;
    }

    /// <summary>按段落优先切分，达到 chunkSize 后输出一片；overlap 保留尾部字符防止断句。</summary>
    public static IEnumerable<string> Chunk(string text, int chunkSize, int overlap)
    {
        text = text.Replace("\r\n", "\n");
        chunkSize = Math.Max(128, chunkSize);
        overlap = Math.Clamp(overlap, 0, Math.Max(0, chunkSize / 2));

        var paragraphs = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var buf = new System.Text.StringBuilder();
        foreach (var p in paragraphs)
        {
            if (p.Length > chunkSize)
            {
                if (buf.Length > 0)
                {
                    yield return buf.ToString();
                    buf.Clear();
                }

                foreach (var part in SplitLongText(p, chunkSize, overlap))
                    yield return part;

                continue;
            }

            if (buf.Length + p.Length + 1 > chunkSize && buf.Length > 0)
            {
                yield return buf.ToString();
                var tail = buf.ToString();
                buf.Clear();
                if (overlap > 0 && tail.Length > overlap) buf.Append(tail[^overlap..]).Append('\n');
            }
            buf.Append(p).Append("\n\n");
        }
        if (buf.Length > 0) yield return buf.ToString();
    }

    private static IEnumerable<string> SplitLongText(string text, int chunkSize, int overlap)
    {
        var step = Math.Max(1, chunkSize - overlap);
        for (var start = 0; start < text.Length; start += step)
        {
            var length = Math.Min(chunkSize, text.Length - start);
            var chunk = text.Substring(start, length);
            if (!string.IsNullOrWhiteSpace(chunk))
                yield return chunk;

            if (start + length >= text.Length)
                yield break;
        }
    }
}
