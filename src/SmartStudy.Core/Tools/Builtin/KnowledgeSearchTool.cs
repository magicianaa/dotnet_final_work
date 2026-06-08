using System.Text.Json;
using SmartStudy.Core.Rag;

namespace SmartStudy.Core.Tools.Builtin;

/// <summary>RAG 工具：先向量检索，再把命中文段返回给 Agent。</summary>
public sealed class KnowledgeSearchTool : ITool
{
    private readonly KnowledgeSearchService _search;

    public KnowledgeSearchTool(KnowledgeSearchService search)
    {
        _search = search;
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
        int? topK = args.TryGetProperty("topK", out var k) && k.ValueKind == JsonValueKind.Number ? k.GetInt32() : null;
        return await _search.SearchAsync(query, topK, ct);
    }
}
