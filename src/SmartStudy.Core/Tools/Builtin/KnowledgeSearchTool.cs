using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartStudy.Core.Configuration;
using SmartStudy.Core.Rag;

namespace SmartStudy.Core.Tools.Builtin;

/// <summary>RAG 工具：先向量检索，再把命中文段返回给 Agent。</summary>
public sealed class KnowledgeSearchTool : ITool
{
    private readonly IEmbeddingClient _embed;
    private readonly IVectorStore _store;
    private readonly RagOptions _opts;

    public KnowledgeSearchTool(IEmbeddingClient embed, IVectorStore store, IOptions<AgentOptions> opts)
    {
        _embed = embed; _store = store; _opts = opts.Value.Rag;
    }

    public string Name => "knowledge_search";
    public string Description => "在课程知识库（Semantic Kernel / Agent Framework / ReAct 等资料）中按语义检索相关片段。回答事实问题前应先调用。";

    public JsonElement ParametersSchema { get; } = JsonSchema.Build("""
    {
      "type": "object",
      "properties": {
        "query": { "type": "string", "description": "检索问题或关键词" },
        "topK":  { "type": "integer", "description": "返回片段数，默认 4", "minimum": 1, "maximum": 8 }
      },
      "required": ["query"]
    }
    """);

    public async Task<string> InvokeAsync(JsonElement args, CancellationToken ct = default)
    {
        var query = args.GetProperty("query").GetString() ?? "";
        var topK = args.TryGetProperty("topK", out var k) && k.ValueKind == JsonValueKind.Number ? k.GetInt32() : _opts.TopK;

        if (_store.Count == 0)
            return "知识库尚未建立索引。请先执行 `index` 命令。";

        var qv = await _embed.EmbedAsync(query, ct);
        var hits = _store.Search(qv, topK);
        if (hits.Count == 0) return "未检索到相关内容。";

        var sb = new StringBuilder();
        sb.AppendLine($"检索到 {hits.Count} 段相关内容（按相似度降序）：");
        int i = 1;
        foreach (var h in hits)
        {
            sb.AppendLine($"\n[{i++}] 来源={h.Chunk.Source}  相似度={h.Score:F3}");
            sb.AppendLine(h.Chunk.Text.Trim());
        }
        return sb.ToString();
    }
}
