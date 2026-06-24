using System.Text.RegularExpressions;

namespace SmartStudy.Core.Tools.Builtin;

public sealed record ParsedQuizAnswer(string QuizId, int QuestionNumber, string Answer, string Topic);

public static class QuizAnswerParser
{
    private static readonly Regex ExplicitQuestionRegex = new(
        @"(?:第\s*)?(?<number>\d+)\s*(?:题|問|问)?\s*(?:我)?\s*(?:选|选择|答案是|答案为|答|=|：|:)\s*(?<answer>[A-Za-z]|[\p{IsCJKUnifiedIdeographs}][\p{IsCJKUnifiedIdeographs}\w-]{0,30})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ChineseQuestionRegex = new(
        @"第\s*(?<number>[一二三四五六七八九十]+)\s*(?:题|問|问)\s*(?:我)?\s*(?:选|选择|答案是|答案为|答|=|：|:)?\s*(?<answer>[A-Za-z])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CompactQuestionRegex = new(
        @"第\s*(?<number>\d+)\s*(?:题|問|问)\s*(?<answer>[A-Za-z])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ListedAnswerRegex = new(
        @"(?<number>\d+)\s*(?:\.|、|:|：)\s*(?<answer>[A-Za-z])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex OptionOnlyRegex = new(
        @"(?<![A-Za-z])(?<answer>[A-Za-z])(?![A-Za-z])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuizIdRegex = new(
        @"#(?<id>[A-Za-z0-9_-]+)|(?:quiz|练习|测验)\s*(?:id|编号)?\s*[:：#]?\s*(?<id2>[A-Za-z0-9_-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool TryParse(string input, out IReadOnlyList<ParsedQuizAnswer> answers)
    {
        answers = Array.Empty<ParsedQuizAnswer>();
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var normalized = Normalize(input);
        if (!LooksLikeQuizAnswer(normalized))
            return false;

        var quizId = ReadQuizId(normalized);
        var topic = ReadTopic(normalized);
        var matches = new List<ParsedQuizAnswer>();

        foreach (var regex in new[] { ExplicitQuestionRegex, ChineseQuestionRegex, CompactQuestionRegex, ListedAnswerRegex })
        {
            foreach (Match match in regex.Matches(normalized))
            {
                if (!match.Success || !TryReadQuestionNumber(match.Groups["number"].Value, out var number) || number <= 0)
                    continue;

                var answer = CleanAnswer(match.Groups["answer"].Value);
                if (string.IsNullOrWhiteSpace(answer) || IsNoise(answer))
                    continue;

                matches.Add(new ParsedQuizAnswer(quizId, number, answer, topic));
            }
        }

        if (matches.Count == 0 && LooksLikeSingleLatestAnswer(normalized))
        {
            var option = OptionOnlyRegex.Match(normalized);
            if (option.Success)
                matches.Add(new ParsedQuizAnswer(quizId, 1, option.Groups["answer"].Value.ToUpperInvariant(), topic));
        }

        answers = matches
            .GroupBy(answer => answer.QuestionNumber)
            .Select(group => group.Last())
            .OrderBy(answer => answer.QuestionNumber)
            .ToList();
        return answers.Count > 0;
    }

    private static string Normalize(string input) =>
        input.Replace('，', ',')
            .Replace('；', ';')
            .Replace('。', '.')
            .Replace('（', '(')
            .Replace('）', ')')
            .Trim();

    private static bool LooksLikeQuizAnswer(string input) =>
        input.Contains('题') ||
        input.Contains("选", StringComparison.OrdinalIgnoreCase) ||
        input.Contains("answer", StringComparison.OrdinalIgnoreCase) ||
        input.Contains("答案", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeSingleLatestAnswer(string input) =>
        input.Contains("选", StringComparison.OrdinalIgnoreCase) ||
        input.Contains("答案", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("answer", StringComparison.OrdinalIgnoreCase);

    private static string ReadQuizId(string input)
    {
        var match = QuizIdRegex.Match(input);
        if (!match.Success)
            return "latest";

        var id = match.Groups["id"].Success ? match.Groups["id"].Value : match.Groups["id2"].Value;
        return string.IsNullOrWhiteSpace(id) ? "latest" : id.Trim();
    }

    private static string ReadTopic(string input)
    {
        var markers = new[] { "主题", "知识点", "topic" };
        foreach (var marker in markers)
        {
            var index = input.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                continue;

            var value = input[(index + marker.Length)..].Trim(' ', ':', '：', '=', '-', ',', ';', '.');
            return value.Length > 40 ? value[..40] : value;
        }

        return "";
    }

    private static string CleanAnswer(string answer) =>
        answer.Trim().Trim('。', '.', ',', '，', ';', '；', ':', '：', '、', ' ', '\t').ToUpperInvariant();

    private static bool TryReadQuestionNumber(string value, out int number)
    {
        if (int.TryParse(value, out number))
            return true;

        number = value.Trim() switch
        {
            "一" => 1,
            "二" => 2,
            "三" => 3,
            "四" => 4,
            "五" => 5,
            "六" => 6,
            "七" => 7,
            "八" => 8,
            "九" => 9,
            "十" => 10,
            _ => 0
        };
        return number > 0;
    }

    private static bool IsNoise(string answer)
    {
        var lower = answer.ToLowerInvariant();
        return lower is "第" or "题" or "选" or "答案" or "answer" or "topic";
    }
}
