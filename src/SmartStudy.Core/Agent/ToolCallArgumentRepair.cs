using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmartStudy.Core.Agent;

internal static class ToolCallArgumentRepair
{
    public static string Repair(string toolName, string arguments, string userInput)
    {
        if (!string.Equals(toolName, "calculate", StringComparison.Ordinal))
            return arguments;

        var userExpression = TryExtractArithmeticExpression(userInput);
        if (userExpression is null)
            return arguments;

        if (!TryReadExpression(arguments, out var modelExpression))
            return arguments;

        if (!ShouldPreferUserExpression(userExpression, modelExpression))
            return arguments;

        try
        {
            if (JsonNode.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments) is not JsonObject obj)
                return arguments;

            obj["expression"] = userExpression;
            return obj.ToJsonString();
        }
        catch (JsonException)
        {
            return arguments;
        }
    }

    private static bool TryReadExpression(string arguments, out string expression)
    {
        expression = "";
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            if (!doc.RootElement.TryGetProperty("expression", out var exprEl) ||
                exprEl.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            expression = exprEl.GetString() ?? "";
            return !string.IsNullOrWhiteSpace(expression);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ShouldPreferUserExpression(string userExpression, string modelExpression)
    {
        var normalizedUser = NormalizeMathText(userExpression);
        var normalizedModel = NormalizeMathText(modelExpression);

        if (string.Equals(normalizedUser, normalizedModel, StringComparison.Ordinal))
            return false;

        if (!ContainsGrouping(normalizedUser))
            return false;

        return string.Equals(
            StripGrouping(normalizedUser),
            StripGrouping(normalizedModel),
            StringComparison.Ordinal);
    }

    private static string? TryExtractArithmeticExpression(string input)
    {
        var normalized = NormalizeMathText(input);
        string? best = null;
        var current = new StringBuilder();

        foreach (var ch in normalized)
        {
            if (IsExpressionChar(ch))
            {
                current.Append(ch);
            }
            else
            {
                FlushCandidate();
            }
        }

        FlushCandidate();
        return best;

        void FlushCandidate()
        {
            if (current.Length == 0)
                return;

            var candidate = Compact(current.ToString());
            current.Clear();

            if (!LooksLikeArithmeticExpression(candidate))
                return;

            if (best is null || candidate.Length > best.Length)
                best = candidate;
        }
    }

    private static string NormalizeMathText(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            sb.Append(ch switch
            {
                'x' or 'X' or '*' => '*',
                '/' => '/',
                '+' => '+',
                '-' => '-',
                _ => ch
            });
        }

        return Compact(sb.ToString());
    }

    private static string Compact(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (!char.IsWhiteSpace(ch))
                sb.Append(ch);
        }

        return sb.ToString();
    }

    private static bool IsExpressionChar(char ch) =>
        char.IsDigit(ch) || ch is '.' or '+' or '-' or '*' or '/' or '(' or ')' || char.IsWhiteSpace(ch);

    private static bool LooksLikeArithmeticExpression(string candidate)
    {
        if (candidate.Length == 0)
            return false;

        if (!candidate.Any(char.IsDigit))
            return false;

        if (!candidate.Any(ch => ch is '+' or '-' or '*' or '/'))
            return false;

        if (CountNumberTokens(candidate) < 2)
            return false;

        var depth = 0;
        foreach (var ch in candidate)
        {
            if (ch == '(') depth++;
            if (ch == ')') depth--;
            if (depth < 0)
                return false;
        }

        return depth == 0;
    }

    private static int CountNumberTokens(string expression)
    {
        var count = 0;
        var inNumber = false;

        foreach (var ch in expression)
        {
            if (char.IsDigit(ch) || ch == '.')
            {
                if (!inNumber)
                    count++;
                inNumber = true;
            }
            else
            {
                inNumber = false;
            }
        }

        return count;
    }

    private static bool ContainsGrouping(string expression) =>
        expression.Contains('(') || expression.Contains(')');

    private static string StripGrouping(string expression)
    {
        var sb = new StringBuilder(expression.Length);
        foreach (var ch in expression)
        {
            if (ch is not ('(' or ')'))
                sb.Append(ch);
        }

        return sb.ToString();
    }
}
