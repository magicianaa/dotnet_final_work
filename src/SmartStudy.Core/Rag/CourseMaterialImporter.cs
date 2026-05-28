using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartStudy.Core.Configuration;

namespace SmartStudy.Core.Rag;

public sealed record CourseImportResult(int FilesRead, int FilesSkipped, int ChunksIndexed, string OutputDirectory, IReadOnlyList<string> ImportedSources);

public sealed class CourseMaterialImporter
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".pdf", ".pptx", ".docx"
    };

    private readonly KnowledgeIndexer _indexer;
    private readonly RagOptions _opts;
    private readonly ILogger<CourseMaterialImporter> _logger;

    public CourseMaterialImporter(KnowledgeIndexer indexer, IOptions<AgentOptions> options, ILogger<CourseMaterialImporter> logger)
    {
        _indexer = indexer;
        _opts = options.Value.Rag;
        _logger = logger;
    }

    public async Task<CourseImportResult> ImportAsync(string directory, string? glob = null, CancellationToken ct = default)
    {
        var sourceDir = NormalizePath(directory);
        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"目录不存在：{sourceDir}");

        Directory.CreateDirectory(_opts.KnowledgeDirectory);
        var importedDir = Path.Combine(_opts.KnowledgeDirectory, "imported");
        Directory.CreateDirectory(importedDir);

        var files = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
            .Where(f => Supported.Contains(Path.GetExtension(f)))
            .Where(f => string.IsNullOrWhiteSpace(glob) || Path.GetFileName(f).Contains(glob, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

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

    private static async Task<string> ExtractTextAsync(string file, CancellationToken ct)
    {
        var ext = Path.GetExtension(file);
        if (ext.Equals(".md", StringComparison.OrdinalIgnoreCase) || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            return await File.ReadAllTextAsync(file, ct);
        if (ext.Equals(".docx", StringComparison.OrdinalIgnoreCase))
            return ExtractOpenXmlText(file, "word/document.xml");
        if (ext.Equals(".pptx", StringComparison.OrdinalIgnoreCase))
            return ExtractOpenXmlText(file, "ppt/slides/");
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return await ExtractPdfTextAsync(file, ct);
        return "";
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
