using System.Text;
using System.Text.RegularExpressions;

namespace SmartStudy.Core.Agent;

internal static class QuizAnswerObservationSummary
{
    private static readonly Regex ResultRegex = new(
        @"练习\s*#(?<quiz>\S+)\s*第\s*(?<number>\d+)\s*题：(?<status>答对|答错)",
        RegexOptions.Compiled);

    public static bool TryBuild(IReadOnlyList<string> observations, out string summary)
    {
        summary = "";
        var results = observations
            .Select(Parse)
            .Where(result => result is not null)
            .Select(result => result!)
            .OrderBy(result => result.Number)
            .ToList();

        if (results.Count == 0)
            return false;

        var correct = results.Count(result => result.IsCorrect);
        var wrong = results.Count - correct;
        var sb = new StringBuilder();

        sb.AppendLine($"本次已按工具判分：共提交 {results.Count} 题，答对 {correct} 题，答错 {wrong} 题。");
        foreach (var result in results)
        {
            sb.AppendLine();
            sb.AppendLine($"第 {result.Number} 题：{(result.IsCorrect ? "答对" : "答错")}");
            sb.AppendLine($"题目：{result.Question}");
            sb.AppendLine($"你的答案：{result.UserAnswer}");
            sb.AppendLine($"标准答案：{result.CorrectAnswer}");
            if (!string.IsNullOrWhiteSpace(result.Explanation))
                sb.AppendLine($"解析：{result.Explanation}");
        }

        if (wrong > 0)
            sb.AppendLine("\n答错的题目已经写入错题记录；如果提供了主题，也会同步到学习画像的薄弱知识点。");

        summary = sb.ToString().TrimEnd();
        return true;
    }

    public static string GuardFinalAnswer(string answer, IReadOnlyList<string> observations)
    {
        if (!TryBuild(observations, out var summary))
            return answer;

        return HasWrongAnswer(observations) && ClaimsAllCorrect(answer)
            ? summary
            : answer;
    }

    private static QuizAnswerObservation? Parse(string observation)
    {
        var match = ResultRegex.Match(observation);
        if (!match.Success)
            return null;

        var number = int.Parse(match.Groups["number"].Value);
        var isCorrect = match.Groups["status"].Value == "答对";
        return new QuizAnswerObservation(
            number,
            isCorrect,
            ReadLineValue(observation, "题目"),
            ReadLineValue(observation, "你的答案"),
            ReadLineValue(observation, "标准答案"),
            ReadLineValue(observation, "解析"));
    }

    private static bool HasWrongAnswer(IReadOnlyList<string> observations) =>
        observations.Any(observation => observation.Contains("答错", StringComparison.Ordinal));

    private static bool ClaimsAllCorrect(string answer)
    {
        var compact = Regex.Replace(answer, @"\s+", "");
        return compact.Contains("全部答对", StringComparison.Ordinal) ||
               compact.Contains("全都答对", StringComparison.Ordinal) ||
               compact.Contains("全部正确", StringComparison.Ordinal) ||
               compact.Contains("全对", StringComparison.Ordinal) ||
               compact.Contains("已经全部答对", StringComparison.Ordinal);
    }

    private static string ReadLineValue(string text, string label)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            var prefix = label + "：";
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
                return trimmed[prefix.Length..].Trim();
        }

        return "";
    }

    private sealed record QuizAnswerObservation(
        int Number,
        bool IsCorrect,
        string Question,
        string UserAnswer,
        string CorrectAnswer,
        string Explanation);
}
