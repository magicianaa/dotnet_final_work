using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmartStudy.Core.Configuration;

namespace SmartStudy.Core.Rag;

public sealed class KnowledgeSearchService
{
    private readonly IEmbeddingClient _embed;
    private readonly IVectorStore _store;
    private readonly IRagRuntimeContext _rag;

    [ActivatorUtilitiesConstructor]
    public KnowledgeSearchService(IEmbeddingClient embed, IVectorStore store, IRagRuntimeContext rag)
    {
        _embed = embed;
        _store = store;
        _rag = rag;
    }

    public KnowledgeSearchService(IEmbeddingClient embed, IVectorStore store, IOptions<AgentOptions> opts)
        : this(embed, store, new DefaultRagRuntimeContext(opts))
    {
    }

    public async Task<string> SearchAsync(string query, int? topK = null, CancellationToken ct = default)
    {
        if (_store.Count == 0)
            return "知识库尚未建立索引。请先执行 `index` 命令。";

        var effectiveTopK = Math.Clamp(topK ?? _rag.Current.TopK, 1, 8);
        var qv = await _embed.EmbedAsync(query, ct);
        var hits = _store.Search(qv, effectiveTopK);
        if (hits.Count == 0) return "未检索到相关内容。";

        var sb = new StringBuilder();
        sb.AppendLine($"检索到 {hits.Count} 段相关内容（按相似度降序，可追溯来源）：");
        int i = 1;
        var sources = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in hits)
        {
            sources.Add(h.Chunk.Source);
            sb.AppendLine($"\n[{i}] 来源：{h.Chunk.Source}");
            sb.AppendLine($"证据编号：S{i}  ChunkId：{h.Chunk.Id}  相似度：{h.Score:F3}");
            sb.AppendLine(h.Chunk.Text.Trim());
            i++;
        }
        sb.AppendLine();
        sb.AppendLine($"来源汇总：{string.Join("；", sources)}");
        return sb.ToString();
    }
}

public sealed record CourseMaterialSummary(string FileName, string RelativePath, long Bytes, DateTime LastWriteTime);

public sealed class CourseMaterialCatalog
{
    private readonly IRagRuntimeContext _rag;

    [ActivatorUtilitiesConstructor]
    public CourseMaterialCatalog(IRagRuntimeContext rag)
    {
        _rag = rag;
    }

    public CourseMaterialCatalog(IOptions<AgentOptions> opts)
        : this(new DefaultRagRuntimeContext(opts))
    {
    }

    public IReadOnlyList<CourseMaterialSummary> ListImportedMaterials(int limit = 50)
    {
        var opts = _rag.Current;
        var effectiveLimit = Math.Clamp(limit, 1, 200);
        var dirs = CandidateImportedDirectories()
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (dirs.Count == 0) return Array.Empty<CourseMaterialSummary>();

        return dirs
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.md", SearchOption.TopDirectoryOnly))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Take(effectiveLimit)
            .Select(f =>
            {
                var info = new FileInfo(f);
                var knowledgeDir = Directory.GetParent(info.DirectoryName ?? "")?.FullName ?? opts.KnowledgeDirectory;
                return new CourseMaterialSummary(
                    info.Name,
                    Path.GetRelativePath(knowledgeDir, info.FullName),
                    info.Length,
                    info.LastWriteTime);
            })
            .ToList();
    }

    private IEnumerable<string> CandidateImportedDirectories()
    {
        var opts = _rag.Current;
        yield return Path.Combine(Path.GetFullPath(opts.KnowledgeDirectory), "imported");

        // When SmartStudy.Mcp is launched from its own output directory, imported course
        // materials usually live in the CLI output directory created during prior imports.
        yield return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "SmartStudy.Cli", "bin", "Debug", "net8.0",
            opts.KnowledgeDirectory,
            "imported"));
    }
}
