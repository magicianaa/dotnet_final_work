using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SmartStudy.Core.Llm;

namespace SmartStudy.Core.Tools.Builtin;

/// <summary>
/// 让 Agent 主动调用 LLM 把任意内容转换成结构化练习题。
/// 注意：此工具会再发起一次 LLM 请求（不带工具），等价于一个子任务。
/// </summary>
public sealed class MakeQuizTool : ITool
{
    private static readonly Regex FencedJsonRegex = new(@"```(?:json)?\s*(?<json>[\s\S]*?)```", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly ILlmClient _llm;
    private readonly IQuizSessionStore _sessions;

    public MakeQuizTool(ILlmClient llm, IQuizSessionStore sessions)
    {
        _llm = llm;
        _sessions = sessions;
    }

    public string Name => "make_quiz";
    public string Description => "根据给定的学习材料原文生成练习题并隐藏答案。调用前必须先用 knowledge_search 或 read_course_material 取得真实课程材料，不能凭常识或用户一句话直接出题。工具会保存标准答案，用户回答后应调用 submit_quiz_answer 判分并给解析。";

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
        if (!args.TryGetProperty("material", out var materialEl) || materialEl.ValueKind != JsonValueKind.String)
            return "出题失败：必须提供 material 字段。";

        var material = materialEl.GetString() ?? "";
        var count = args.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 3;
        count = Math.Clamp(count, 1, 10);

        var resp = await _llm.ChatAsync(BuildGenerateRequest(material, count), ct);
        var raw = resp.Content ?? "";
        if (TryNormalizeQuizJson(raw, count, out var normalized, out var error))
            return await SaveAndFormatQuizAsync(normalized, ct);

        var repair = await _llm.ChatAsync(BuildRepairRequest(raw, error, count), ct);
        var repairedRaw = repair.Content ?? "";
        if (TryNormalizeQuizJson(repairedRaw, count, out normalized, out error))
            return await SaveAndFormatQuizAsync(normalized, ct);

        return $"出题失败：LLM 未返回合法的练习题 JSON。最后错误：{error}\n原始输出片段：{Truncate(repairedRaw, 800)}";
    }

    private async Task<string> SaveAndFormatQuizAsync(string normalizedJson, CancellationToken ct)
    {
        var items = JsonSerializer.Deserialize<List<QuizItem>>(normalizedJson, JsonOptions) ?? new List<QuizItem>();
        var session = new QuizSession
        {
            Questions = items.Select((item, index) => new QuizQuestion
            {
                Number = index + 1,
                Question = item.Question,
                Options = item.Options.ToList(),
                Answer = item.Answer,
                Explanation = item.Explanation
            }).ToList()
        };
        await _sessions.SaveAsync(session, ct);

        var lines = new List<string>
        {
            $"已生成练习 #{session.Id}，共 {session.Questions.Count} 题。请先作答，答案和解析已隐藏。",
            "提交答案格式：`submit_quiz_answer` 工具，或聊天快捷指令 `:answer <quizId> | <题号> | <你的答案> | <主题>`。",
            ""
        };

        lines.Insert(2, "也可以直接回复，例如：`第1题选A，第2题选B`。系统会自动提交并判分。");
        lines.Insert(3, $"标准指令示例：`:answer {session.Id} | 1 | A | 主题`，或 `:answer latest | 1 | A | 主题`。");

        foreach (var question in session.Questions)
        {
            lines.Add($"{question.Number}. {question.Question}");
            if (question.Options.Count > 0)
            {
                for (var i = 0; i < question.Options.Count; i++)
                    lines.Add($"   {OptionLabel(i)}. {question.Options[i]}");
            }
            lines.Add("");
        }

        return string.Join("\n", lines).TrimEnd();
    }

    private static ChatRequest BuildGenerateRequest(string material, int count)
    {
        var prompt =
            $"基于下面材料生成 {count} 道练习题。\n" +
            "严格输出 JSON 数组，数组长度必须等于题目数量。\n" +
            "每一项必须包含 question、options、answer、explanation 四个字段。\n" +
            "options 必须是字符串数组；如果是填空题可以为空数组。\n" +
            "禁止输出 Markdown、解释文字或代码块。\n" +
            "---\n" +
            material +
            "\n---";

        return new ChatRequest
        {
            Temperature = 0.4,
            MaxTokens = 2048,
            Messages = new()
            {
                new ChatMessage { Role = ChatRoles.System, Content = "你是出题助手。严格只输出 JSON。" },
                new ChatMessage { Role = ChatRoles.User, Content = prompt }
            }
        };
    }

    private static ChatRequest BuildRepairRequest(string raw, string error, int count)
    {
        var prompt =
            "请修复下面的练习题输出，使其成为合法 JSON 数组。\n" +
            $"数组长度必须等于 {count}。\n" +
            "每项必须包含 question、options、answer、explanation。\n" +
            "options 必须是字符串数组。只输出 JSON，禁止输出 Markdown。\n" +
            $"校验错误：{error}\n" +
            "--- 原始输出 ---\n" +
            raw;

        return new ChatRequest
        {
            Temperature = 0,
            MaxTokens = 2048,
            Messages = new()
            {
                new ChatMessage { Role = ChatRoles.System, Content = "你是 JSON 修复器。只输出合法 JSON。" },
                new ChatMessage { Role = ChatRoles.User, Content = prompt }
            }
        };
    }

    internal static bool TryNormalizeQuizJson(string raw, int expectedCount, out string normalized, out string error)
    {
        normalized = "";
        error = "";

        foreach (var candidate in ExtractJsonCandidates(raw))
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    error = "根节点必须是 JSON 数组。";
                    continue;
                }

                var items = new List<QuizItem>();
                var index = 0;
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    index++;
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        error = $"第 {index} 项必须是 JSON 对象。";
                        continue;
                    }

                    if (!TryReadString(item, "question", out var question))
                    {
                        error = $"第 {index} 项缺少非空 question 字段。";
                        continue;
                    }
                    if (!TryReadStringArray(item, "options", out var options))
                    {
                        error = $"第 {index} 项缺少 options 字符串数组字段。";
                        continue;
                    }
                    if (!TryReadString(item, "answer", out var answer))
                    {
                        error = $"第 {index} 项缺少非空 answer 字段。";
                        continue;
                    }
                    if (!TryReadString(item, "explanation", out var explanation))
                    {
                        error = $"第 {index} 项缺少非空 explanation 字段。";
                        continue;
                    }

                    items.Add(new QuizItem(question, options, answer, explanation));
                }

                if (items.Count != expectedCount)
                {
                    error = $"题目数量应为 {expectedCount}，实际为 {items.Count}。";
                    continue;
                }

                normalized = JsonSerializer.Serialize(items, JsonOptions);
                return true;
            }
            catch (JsonException ex)
            {
                error = $"JSON 解析失败：{ex.Message}";
            }
        }

        if (string.IsNullOrWhiteSpace(error))
            error = "未找到 JSON 数组。";

        return false;
    }

    private static IEnumerable<string> ExtractJsonCandidates(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            yield break;

        yield return raw.Trim();

        foreach (Match match in FencedJsonRegex.Matches(raw))
        {
            var fenced = match.Groups["json"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(fenced))
                yield return fenced;
        }

        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start >= 0 && end > start)
            yield return raw[start..(end + 1)].Trim();
    }

    private static bool TryReadString(JsonElement item, string name, out string value)
    {
        value = "";
        if (!item.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return false;

        value = prop.GetString()?.Trim() ?? "";
        return value.Length > 0;
    }

    private static bool TryReadStringArray(JsonElement item, string name, out List<string> values)
    {
        values = new List<string>();
        if (!item.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var option in prop.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.String)
                return false;
            values.Add(option.GetString()?.Trim() ?? "");
        }

        return true;
    }

    private static string Truncate(string value, int maxChars)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= maxChars ? value : value[..maxChars] + "...";
    }

    internal sealed record QuizItem(
        [property: JsonPropertyName("question")] string Question,
        [property: JsonPropertyName("options")] IReadOnlyList<string> Options,
        [property: JsonPropertyName("answer")] string Answer,
        [property: JsonPropertyName("explanation")] string Explanation);

    private static string OptionLabel(int index) => ((char)('A' + index)).ToString();
}
