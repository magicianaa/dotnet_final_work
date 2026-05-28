using System.Text.Json;
using SmartStudy.Core.Llm;

namespace SmartStudy.Core.Tools.Builtin;

/// <summary>
/// 让 Agent 主动调用 LLM 把任意内容转换成结构化练习题。
/// 注意：此工具会再发起一次 LLM 请求（不带工具），等价于一个子任务。
/// </summary>
public sealed class MakeQuizTool : ITool
{
    private readonly ILlmClient _llm;
    public MakeQuizTool(ILlmClient llm) => _llm = llm;

    public string Name => "make_quiz";
    public string Description => "根据给定的学习材料文本，生成若干道带答案的练习题（单选/填空）。用于复习。";

    public JsonElement ParametersSchema { get; } = JsonSchema.Build("""
    {
      "type": "object",
      "properties": {
        "material": { "type": "string", "description": "用于出题的学习材料原文" },
        "count":    { "type": "integer", "description": "题目数量，默认 3", "minimum": 1, "maximum": 10 }
      },
      "required": ["material"]
    }
    """);

    public async Task<string> InvokeAsync(JsonElement args, CancellationToken ct = default)
    {
        var material = args.GetProperty("material").GetString() ?? "";
        var count = args.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 3;

        var prompt = $"基于下面材料生成 {count} 道练习题，输出 JSON 数组，每项含 question/options/answer/explanation，禁止额外文字：\n---\n{material}\n---";
        var req = new ChatRequest
        {
            Temperature = 0.4,
            Messages = new()
            {
                new ChatMessage { Role = ChatRoles.System, Content = "你是出题助手。严格只输出 JSON。" },
                new ChatMessage { Role = ChatRoles.User, Content = prompt }
            }
        };
        var resp = await _llm.ChatAsync(req, ct);
        return resp.Content ?? "（出题失败）";
    }
}
