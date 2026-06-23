using System.Text;

namespace SmartStudy.Core.Agent;

public sealed record AnswerQualityIssue(string Check, string Detail);

public sealed record AnswerQualityReview(
    bool Passed,
    IReadOnlyList<string> PassedChecks,
    IReadOnlyList<AnswerQualityIssue> Issues,
    string Summary);

/// <summary>Deterministic reviewer used before showing an answer in demos and plan-execute mode.</summary>
public sealed class AnswerQualityReviewer
{
    private static readonly string[] SourceMarkers =
    {
        "来源", "证据编号", "ChunkId", "资料依据", "Source:"
    };

    private static readonly string[] NextStepMarkers =
    {
        "下一步", "建议", "验收", "运行", "命令", "复习", "继续"
    };

    public AnswerQualityReview Review(string userGoal, string answer, bool evidenceExpected = true)
    {
        var passed = new List<string>();
        var issues = new List<AnswerQualityIssue>();

        if (string.IsNullOrWhiteSpace(answer))
        {
            issues.Add(new AnswerQualityIssue("非空回答", "最终回答为空。"));
        }
        else
        {
            passed.Add("非空回答");
        }

        if (answer.Length >= 80)
            passed.Add("回答充分");
        else
            issues.Add(new AnswerQualityIssue("回答充分", "回答过短，可能没有覆盖完整任务。"));

        if (CoversGoal(userGoal, answer))
            passed.Add("覆盖用户目标");
        else
            issues.Add(new AnswerQualityIssue("覆盖用户目标", "回答中没有明显覆盖用户目标中的关键主题。"));

        if (!evidenceExpected || ContainsAny(answer, SourceMarkers))
            passed.Add("包含资料依据");
        else
            issues.Add(new AnswerQualityIssue("包含资料依据", "需要引用 RAG 检索来源、证据编号或资料依据。"));

        if (ContainsAny(answer, NextStepMarkers))
            passed.Add("包含下一步");
        else
            issues.Add(new AnswerQualityIssue("包含下一步", "需要给出后续操作、验收命令或学习建议。"));

        if (!ContainsPlaceholders(answer))
            passed.Add("无明显占位文本");
        else
            issues.Add(new AnswerQualityIssue("无明显占位文本", "回答包含“此处省略”等占位式文本。"));

        var summary = BuildSummary(passed, issues);
        return new AnswerQualityReview(issues.Count == 0, passed, issues, summary);
    }

    private static bool CoversGoal(string goal, string answer)
    {
        var keywords = ExtractKeywords(goal);
        if (keywords.Count == 0) return true;
        var matched = keywords.Count(k => answer.Contains(k, StringComparison.OrdinalIgnoreCase));
        return matched >= Math.Min(2, keywords.Count);
    }

    private static IReadOnlyList<string> ExtractKeywords(string text)
    {
        var separators = new[] { ' ', ',', '，', '。', ';', '；', '/', '\\', ':', '：', '?', '？', '!', '！', '"', '\'' };
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "请", "帮我", "解释", "说明", "一下", "如何", "怎么", "准备", "完成", "基于", "进行", "生成", "计划",
            "the", "a", "an", "and", "or", "to", "of", "for"
        };

        return text
            .Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 2 && !stopWords.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }

    private static bool ContainsAny(string text, IEnumerable<string> markers) =>
        markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsPlaceholders(string answer) =>
        ContainsAny(answer, new[] { "此处省略", "略", "TODO", "待补充", "placeholder" });

    private static string BuildSummary(IReadOnlyList<string> passed, IReadOnlyList<AnswerQualityIssue> issues)
    {
        var sb = new StringBuilder();
        sb.AppendLine(issues.Count == 0 ? "质量检查通过。" : "质量检查发现需要改进的项目。");
        foreach (var item in passed)
            sb.AppendLine($"- OK {item}");
        foreach (var issue in issues)
            sb.AppendLine($"- TODO {issue.Check}: {issue.Detail}");
        return sb.ToString().TrimEnd();
    }
}
