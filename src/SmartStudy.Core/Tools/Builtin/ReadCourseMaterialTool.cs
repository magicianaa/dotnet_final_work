using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SmartStudy.Core.Configuration;
using SmartStudy.Core.Rag;

namespace SmartStudy.Core.Tools.Builtin;

/// <summary>读取已经导入知识库的指定课程资料，适合按文件名讲解整份课件或连续页面。</summary>
public sealed class ReadCourseMaterialTool : ITool
{
    private const int DefaultMaxChars = 6000;
    private const int HardMaxChars = 12000;
    private static readonly Regex PageHeading = new(@"^## Page\s+(\d+)\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    private readonly IVectorStore _store;
    private readonly RagOptions _opts;

    public ReadCourseMaterialTool(IVectorStore store, IOptions<AgentOptions> opts)
    {
        _store = store;
        _opts = opts.Value.Rag;
    }

    public string Name => "read_course_material";

    public string Description =>
        "读取已导入知识库中的某个课程资料文件的连续内容。用户要求“讲讲某个文件/课件/PDF的具体内容/第一页到第几页/整份介绍”时优先调用它，而不是只用 knowledge_search。";

    public JsonElement ParametersSchema { get; } = JsonSchema.Build("""
    {
      "type": "object",
      "properties": {
        "fileName": { "type": "string", "description": "课程资料文件名或部分文件名，例如 2026_Slides Lesson00_Introduction to SEME.pdf 或 Lesson00" },
        "startPage": { "type": "integer", "description": "可选，起始页码。PDF 导入文本含 Page 标记时生效。", "minimum": 1 },
        "endPage": { "type": "integer", "description": "可选，结束页码。", "minimum": 1 },
        "maxChars": { "type": "integer", "description": "可选，最多返回字符数，默认 6000，最大 12000。", "minimum": 500, "maximum": 12000 }
      },
      "required": ["fileName"]
    }
    """);

    public async Task<string> InvokeAsync(JsonElement args, CancellationToken ct = default)
    {
        if (!args.TryGetProperty("fileName", out var fileEl) || fileEl.ValueKind != JsonValueKind.String)
            return "错误：必须提供 fileName 字段。";

        var fileName = fileEl.GetString() ?? "";
        var startPage = ReadOptionalInt(args, "startPage");
        var endPage = ReadOptionalInt(args, "endPage");
        var maxChars = Math.Clamp(ReadOptionalInt(args, "maxChars") ?? DefaultMaxChars, 500, HardMaxChars);

        var materialPath = ResolveMaterialPath(fileName);
        if (materialPath is null)
        {
            var candidates = ListCandidateSources(fileName);
            return candidates.Count == 0
                ? $"未找到匹配的课程资料：{fileName}。请先导入资料，或提供更完整的文件名。"
                : $"未找到完全匹配的课程资料：{fileName}。相近文件包括：{string.Join(", ", candidates)}";
        }

        var text = await File.ReadAllTextAsync(materialPath, ct);
        var pageCount = CountPages(text);
        var selected = SelectPages(text, startPage, endPage);
        var truncated = selected.Length > maxChars;
        if (truncated) selected = selected[..maxChars].TrimEnd();

        var pageHint = startPage.HasValue || endPage.HasValue
            ? $"页码范围：{startPage?.ToString() ?? "开头"}-{endPage?.ToString() ?? "末尾"}\n"
            : "";
        var completeness = truncated
            ? "内容状态：已截断，原文更长。\n"
            : "内容状态：已读取完整匹配内容，没有后续被省略的页面。\n";
        var pageCountHint = pageCount > 0 ? $"识别页数：{pageCount}\n" : "";

        var sb = new StringBuilder()
            .AppendLine($"课程资料：{Path.GetFileName(materialPath)}")
            .Append(pageHint)
            .Append(pageCountHint)
            .Append(completeness)
            .AppendLine(truncated
                ? $"以下为截取内容（约 {selected.Length} 字，原文更长）："
                : $"以下为可用内容（约 {selected.Length} 字）：")
            .AppendLine()
            .AppendLine(selected.Trim());

        if (truncated)
            sb.AppendLine().AppendLine("提示：如需继续，请再次调用本工具并指定后续页码，或提高 maxChars。");

        return sb.ToString();
    }

    private static int CountPages(string text)
    {
        return PageHeading.Matches(text).Count;
    }

    private string? ResolveMaterialPath(string fileName)
    {
        var normalized = NormalizeName(fileName);
        var files = Directory.Exists(_opts.KnowledgeDirectory)
            ? Directory.EnumerateFiles(_opts.KnowledgeDirectory, "*.md", SearchOption.AllDirectories).ToList()
            : new List<string>();

        var exact = files.FirstOrDefault(f => SourceNames(f).Any(n => NormalizeName(n).Equals(normalized, StringComparison.OrdinalIgnoreCase)));
        if (exact is not null) return exact;

        var contains = files
            .Select(f => new { File = f, Score = SourceNames(f).Max(n => ContainsScore(NormalizeName(n), normalized)) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.File, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (contains is not null) return contains.File;

        var indexed = _store.Chunks
            .Select(c => c.Source)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(s => new { Source = s, Score = ContainsScore(NormalizeName(s), normalized) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();

        return indexed is null ? null : files.FirstOrDefault(f => Path.GetFileName(f).Equals(indexed.Source, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<string> ListCandidateSources(string fileName)
    {
        var normalized = NormalizeName(fileName);
        return _store.Chunks
            .Select(c => c.Source)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(s => new { Source = s, Score = ContainsScore(NormalizeName(s), normalized) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(8)
            .Select(x => x.Source)
            .ToList();
    }

    private static IEnumerable<string> SourceNames(string markdownFile)
    {
        yield return Path.GetFileName(markdownFile);
        yield return Path.GetFileNameWithoutExtension(markdownFile);

        foreach (var line in File.ReadLines(markdownFile).Take(8))
        {
            if (line.StartsWith("Source:", StringComparison.OrdinalIgnoreCase))
            {
                var source = line["Source:".Length..].Trim();
                yield return source;
                yield return Path.GetFileName(source);
                yield return Path.GetFileNameWithoutExtension(source);
            }
        }
    }

    private static string SelectPages(string text, int? startPage, int? endPage)
    {
        if (!startPage.HasValue && !endPage.HasValue) return text;

        var matches = PageHeading.Matches(text);
        if (matches.Count == 0) return text;

        var start = startPage ?? 1;
        var end = Math.Max(endPage ?? int.MaxValue, start);
        var sections = new List<string>();

        for (var i = 0; i < matches.Count; i++)
        {
            var page = int.Parse(matches[i].Groups[1].Value);
            if (page < start || page > end) continue;

            var sectionStart = matches[i].Index;
            var sectionEnd = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            sections.Add(text[sectionStart..sectionEnd].Trim());
        }

        return sections.Count == 0
            ? $"未找到指定页码范围 {start}-{end} 的内容。"
            : string.Join("\n\n", sections);
    }

    private static int? ReadOptionalInt(JsonElement args, string name)
    {
        return args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetInt32()
            : null;
    }

    private static int ContainsScore(string candidate, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0;
        if (candidate.Equals(query, StringComparison.OrdinalIgnoreCase)) return 1000;
        if (candidate.Contains(query, StringComparison.OrdinalIgnoreCase)) return 500 + query.Length;
        if (query.Contains(candidate, StringComparison.OrdinalIgnoreCase)) return 250 + candidate.Length;

        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Count(t => candidate.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeName(string value)
    {
        var name = value.Trim().Trim('"', '\'');
        name = Path.GetFileName(name);
        if (name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            name = Path.GetFileNameWithoutExtension(name);
        }
        if (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            name = Path.GetFileNameWithoutExtension(name);

        return name.Replace('_', ' ').Replace('-', ' ').Trim();
    }
}
