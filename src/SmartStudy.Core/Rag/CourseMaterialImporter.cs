using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartStudy.Core.Configuration;

namespace SmartStudy.Core.Rag;

public sealed record CourseImportResult(int FilesRead, int FilesSkipped, int ChunksIndexed, string OutputDirectory, IReadOnlyList<string> ImportedSources);

public sealed class CourseMaterialImporter
{
    public static readonly IReadOnlySet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".pdf", ".pptx", ".docx", ".csv", ".tsv", ".html", ".htm", ".xlsx"
    };

    private readonly KnowledgeIndexer _indexer;
    private readonly IRagRuntimeContext _rag;
    private readonly ILogger<CourseMaterialImporter> _logger;

    [ActivatorUtilitiesConstructor]
    public CourseMaterialImporter(KnowledgeIndexer indexer, IRagRuntimeContext rag, ILogger<CourseMaterialImporter> logger)
    {
        _indexer = indexer;
        _rag = rag;
        _logger = logger;
    }

    public CourseMaterialImporter(KnowledgeIndexer indexer, IOptions<AgentOptions> options, ILogger<CourseMaterialImporter> logger)
        : this(indexer, new DefaultRagRuntimeContext(options), logger)
    {
    }

    public async Task<CourseImportResult> ImportAsync(string path, string? glob = null, CancellationToken ct = default)
    {
        var sourcePath = NormalizePath(path);
        var files = ResolveSourceFiles(sourcePath, glob);

        var opts = _rag.Current;
        Directory.CreateDirectory(opts.KnowledgeDirectory);
        var importedDir = Path.Combine(opts.KnowledgeDirectory, "imported");
        Directory.CreateDirectory(importedDir);

        var imported = new List<string>();
        var skipped = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            string text;
            try
            {
                text = await ExtractTextAsync(file, ct);
            }
            catch (Exception ex)
            {
                skipped++;
                _logger.LogWarning(ex, "导入课程资料失败：{File}", file);
                continue;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                skipped++;
                continue;
            }

            var outName = MakeSafeFileName(Path.GetFileNameWithoutExtension(file)) + ".md";
            var outPath = Path.Combine(importedDir, outName);
            var markdown = new StringBuilder()
                .AppendLine($"# {Path.GetFileNameWithoutExtension(file)}")
                .AppendLine()
                .AppendLine($"Source: {file}")
                .AppendLine()
                .AppendLine(text.Trim())
                .ToString();
            await File.WriteAllTextAsync(outPath, markdown, Encoding.UTF8, ct);
            imported.Add(Path.GetFileName(outPath));
        }

        await _indexer.BuildAsync(ct);
        var count = await _indexer.LoadIfExistsAsync(ct) ? _indexer.Count : 0;
        return new CourseImportResult(imported.Count, skipped, count, importedDir, imported);
    }

    private static string NormalizePath(string path)
    {
        var trimmed = path.Trim().Trim('"');
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(trimmed));
    }

    private static List<string> ResolveSourceFiles(string sourcePath, string? glob)
    {
        if (File.Exists(sourcePath))
        {
            if (!SupportedExtensions.Contains(Path.GetExtension(sourcePath)))
                throw new InvalidOperationException($"不支持的资料文件类型：{Path.GetExtension(sourcePath)}");

            return string.IsNullOrWhiteSpace(glob) ||
                   Path.GetFileName(sourcePath).Contains(glob, StringComparison.OrdinalIgnoreCase)
                ? new List<string> { sourcePath }
                : new List<string>();
        }

        if (!Directory.Exists(sourcePath))
            throw new FileNotFoundException($"路径不存在：{sourcePath}");

        return Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
            .Where(f => string.IsNullOrWhiteSpace(glob) || Path.GetFileName(f).Contains(glob, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<string> ExtractTextAsync(string file, CancellationToken ct)
    {
        var ext = Path.GetExtension(file);
        if (ext.Equals(".md", StringComparison.OrdinalIgnoreCase) || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            return await File.ReadAllTextAsync(file, ct);
        if (ext.Equals(".csv", StringComparison.OrdinalIgnoreCase) || ext.Equals(".tsv", StringComparison.OrdinalIgnoreCase))
            return await ExtractDelimitedTextAsync(file, ct);
        if (ext.Equals(".html", StringComparison.OrdinalIgnoreCase) || ext.Equals(".htm", StringComparison.OrdinalIgnoreCase))
            return ExtractHtmlText(await File.ReadAllTextAsync(file, ct));
        if (ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            return ExtractXlsxText(file);
        if (ext.Equals(".docx", StringComparison.OrdinalIgnoreCase))
            return ExtractOpenXmlText(file, "word/document.xml");
        if (ext.Equals(".pptx", StringComparison.OrdinalIgnoreCase))
            return ExtractOpenXmlText(file, "ppt/slides/");
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return await ExtractPdfTextAsync(file, ct);
        return "";
    }

    private static async Task<string> ExtractDelimitedTextAsync(string file, CancellationToken ct)
    {
        var delimiter = Path.GetExtension(file).Equals(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : ',';
        var lines = await File.ReadAllLinesAsync(file, ct);
        var sb = new StringBuilder();
        sb.AppendLine($"## Table: {Path.GetFileName(file)}");
        foreach (var line in lines)
        {
            var cells = ParseDelimitedLine(line, delimiter)
                .Select(c => c.Trim())
                .Where(c => !string.IsNullOrWhiteSpace(c));
            sb.AppendLine(string.Join(" | ", cells));
        }
        return sb.ToString();
    }

    private static IReadOnlyList<string> ParseDelimitedLine(string line, char delimiter)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
                continue;
            }

            if (ch == delimiter && !inQuotes)
            {
                cells.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        cells.Add(current.ToString());
        return cells;
    }

    private static string ExtractHtmlText(string html)
    {
        var withoutScripts = System.Text.RegularExpressions.Regex.Replace(
            html,
            @"<\s*(script|style)[^>]*>[\s\S]*?<\s*/\s*\1\s*>",
            " ",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var withBreaks = System.Text.RegularExpressions.Regex.Replace(
            withoutScripts,
            @"</?(p|div|br|li|tr|h[1-6]|table|section|article)[^>]*>",
            "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var noTags = System.Text.RegularExpressions.Regex.Replace(withBreaks, "<[^>]+>", " ");
        var decoded = System.Net.WebUtility.HtmlDecode(noTags);
        var lines = decoded
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => System.Text.RegularExpressions.Regex.Replace(line, @"\s+", " ").Trim())
            .Where(line => line.Length > 0);
        return string.Join(Environment.NewLine, lines);
    }

    private static string ExtractXlsxText(string file)
    {
        using var archive = ZipFile.OpenRead(file);
        var sharedStrings = ReadSharedStrings(archive);
        var workbookRels = ReadWorkbookRelationships(archive);
        var sheetNames = ReadSheetNames(archive, workbookRels);
        var sb = new StringBuilder();

        var sheets = archive.Entries
            .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var sheet in sheets)
        {
            var sheetName = sheetNames.TryGetValue(sheet.FullName, out var name)
                ? name
                : Path.GetFileNameWithoutExtension(sheet.FullName);
            sb.AppendLine($"## Sheet: {sheetName}");
            using var stream = sheet.Open();
            var doc = System.Xml.Linq.XDocument.Load(stream);
            var rows = doc.Descendants().Where(e => e.Name.LocalName == "row");
            foreach (var row in rows)
            {
                var cells = row.Descendants()
                    .Where(e => e.Name.LocalName == "c")
                    .Select(cell => ReadCellValue(cell, sharedStrings))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToList();
                if (cells.Count > 0)
                    sb.AppendLine(string.Join(" | ", cells));
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return new List<string>();

        using var stream = entry.Open();
        var doc = System.Xml.Linq.XDocument.Load(stream);
        return doc.Descendants()
            .Where(e => e.Name.LocalName == "si")
            .Select(si => string.Concat(si.Descendants().Where(t => t.Name.LocalName == "t").Select(t => t.Value)))
            .ToList();
    }

    private static Dictionary<string, string> ReadWorkbookRelationships(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/_rels/workbook.xml.rels");
        if (entry is null) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var stream = entry.Open();
        var doc = System.Xml.Linq.XDocument.Load(stream);
        return doc.Descendants()
            .Where(e => e.Name.LocalName == "Relationship")
            .Where(e => e.Attribute("Id") is not null && e.Attribute("Target") is not null)
            .ToDictionary(
                e => e.Attribute("Id")!.Value,
                e => NormalizeXlsxTarget(e.Attribute("Target")!.Value),
                StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ReadSheetNames(ZipArchive archive, Dictionary<string, string> rels)
    {
        var entry = archive.GetEntry("xl/workbook.xml");
        if (entry is null) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        XNamespace relNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        using var stream = entry.Open();
        var doc = System.Xml.Linq.XDocument.Load(stream);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in doc.Descendants().Where(e => e.Name.LocalName == "sheet"))
        {
            var name = sheet.Attribute("name")?.Value;
            var rid = sheet.Attribute(relNs + "id")?.Value;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(rid)) continue;
            if (rels.TryGetValue(rid, out var target))
                result[target] = name;
        }
        return result;
    }

    private static string NormalizeXlsxTarget(string target)
    {
        var normalized = target.Replace('\\', '/').TrimStart('/');
        return normalized.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : "xl/" + normalized;
    }

    private static string ReadCellValue(System.Xml.Linq.XElement cell, IReadOnlyList<string> sharedStrings)
    {
        var type = cell.Attribute("t")?.Value;
        var value = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? "";
        if (type == "s" && int.TryParse(value, out var sharedIndex) && sharedIndex >= 0 && sharedIndex < sharedStrings.Count)
            return sharedStrings[sharedIndex];
        if (type == "inlineStr")
            return string.Concat(cell.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value));
        return value;
    }

    private static string ExtractOpenXmlText(string file, string prefix)
    {
        using var archive = ZipFile.OpenRead(file);
        var sb = new StringBuilder();
        var entries = archive.Entries
            .Where(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            using var stream = entry.Open();
            var doc = System.Xml.Linq.XDocument.Load(stream);
            var texts = doc.Descendants()
                .Where(e => e.Name.LocalName is "t")
                .Select(e => e.Value)
                .Where(s => !string.IsNullOrWhiteSpace(s));
            foreach (var text in texts) sb.AppendLine(text);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static async Task<string> ExtractPdfTextAsync(string file, CancellationToken ct)
    {
        var script = """
import fitz, sys
doc = fitz.open(sys.argv[1])
for i, page in enumerate(doc):
    text = page.get_text()
    if text.strip():
        print(f"\n\n## Page {i + 1}\n")
        print(text)
""";
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(file);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 python 提取 PDF 文本。");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"PDF 文本提取失败：{stderr}");
        return stdout;
    }

    private static string MakeSafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }
}
