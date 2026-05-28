using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartStudy.Core.Configuration;

namespace SmartStudy.Core.Rag;

/// <summary>把 knowledge/*.md 切片、向量化、写入向量库。</summary>
public sealed class KnowledgeIndexer
{
    private readonly IEmbeddingClient _embed;
    private readonly IVectorStore _store;
    private readonly RagOptions _opts;
    private readonly ILogger<KnowledgeIndexer> _logger;

    public KnowledgeIndexer(IEmbeddingClient embed, IVectorStore store,
        IOptions<AgentOptions> opts, ILogger<KnowledgeIndexer> logger)
    {
        _embed = embed; _store = store; _opts = opts.Value.Rag; _logger = logger;
    }

    public int Count => _store.Count;

    public async Task BuildAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_opts.KnowledgeDirectory))
        {
            _logger.LogWarning("知识目录不存在：{Dir}", _opts.KnowledgeDirectory);
            return;
        }
        var files = Directory.GetFiles(_opts.KnowledgeDirectory, "*.md", SearchOption.AllDirectories);
        var chunks = new List<KnowledgeChunk>();
        foreach (var f in files)
        {
            var text = await File.ReadAllTextAsync(f, ct);
            var name = Path.GetFileName(f);
            foreach (var (chunkText, idx) in Chunk(text, _opts.ChunkSize, _opts.ChunkOverlap).Select((c, i) => (c, i)))
            {
                chunks.Add(new KnowledgeChunk { Id = $"{name}#{idx}", Source = name, Text = chunkText });
            }
        }
        _logger.LogInformation("准备 embed {N} 个片段（来自 {F} 个文件）", chunks.Count, files.Length);
        var vectors = await _embed.EmbedBatchAsync(chunks.Select(c => c.Text), ct);
        for (int i = 0; i < chunks.Count; i++) chunks[i].Vector = vectors[i];
        _store.Replace(chunks);
        await _store.SaveAsync(_opts.IndexFile, ct);
        _logger.LogInformation("索引已写入 {Path}", _opts.IndexFile);
    }

    public async Task<bool> LoadIfExistsAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_opts.IndexFile)) return false;
        await _store.LoadAsync(_opts.IndexFile, ct);
        return _store.Count > 0;
    }

    /// <summary>按段落优先切分，达到 chunkSize 后输出一片；overlap 保留尾部字符防止断句。</summary>
    public static IEnumerable<string> Chunk(string text, int chunkSize, int overlap)
    {
        text = text.Replace("\r\n", "\n");
        var paragraphs = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var buf = new System.Text.StringBuilder();
        foreach (var p in paragraphs)
        {
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
}
