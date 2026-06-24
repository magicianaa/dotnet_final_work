using System.Text.Json;
using SmartStudy.Core.Rag;

namespace SmartStudy.Core.Tools.Builtin;

/// <summary>读取本机课程资料文件或目录，抽取文本并导入本地 RAG 知识库。</summary>
public sealed class ImportCourseMaterialsTool : ITool
{
    private readonly CourseMaterialImporter _importer;

    public ImportCourseMaterialsTool(CourseMaterialImporter importer) => _importer = importer;

    public string Name => "import_course_materials";

    public string Description =>
        "读取用户提供的本机课程资料路径，支持单个文件或目录（.pdf/.pptx/.docx/.xlsx/.csv/.tsv/.html/.htm/.md/.txt），抽取文本，写入 knowledge/imported，并重建本地 RAG 索引。用户说“阅读这个文件/文件夹、导入课程资料、我的资料在某路径”时应调用。";

    public JsonElement ParametersSchema { get; } = JsonSchema.Build("""
    {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "本机课程资料文件或目录路径，例如 C:\\Users\\name\\Desktop\\course\\lesson.pdf 或 C:\\Users\\name\\Desktop\\course\\ppts" },
        "directory": { "type": "string", "description": "兼容旧参数：本机课程资料目录路径；如果用户给的是单个文件，请优先使用 path" },
        "glob": { "type": "string", "description": "可选，按文件名包含关系过滤，例如 Lesson01 或 Risk；单文件导入通常不需要" }
      },
      "anyOf": [
        { "required": ["path"] },
        { "required": ["directory"] }
      ]
    }
    """);

    public async Task<string> InvokeAsync(JsonElement args, CancellationToken ct = default)
    {
        var pathEl = args.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
            ? p
            : args.TryGetProperty("directory", out var d) && d.ValueKind == JsonValueKind.String
                ? d
                : default;

        if (pathEl.ValueKind != JsonValueKind.String)
            return "错误：必须提供 path 字段（文件或目录路径）。";

        var path = pathEl.GetString() ?? "";
        var glob = args.TryGetProperty("glob", out var globEl) && globEl.ValueKind == JsonValueKind.String
            ? globEl.GetString()
            : null;

        try
        {
            var result = await _importer.ImportAsync(path, glob, ct);
            return $"已读取课程资料路径：{path}\n" +
                   $"成功导入 {result.FilesRead} 个文件，跳过 {result.FilesSkipped} 个文件。\n" +
                   $"已重建本地 RAG 索引，当前索引片段数：{result.ChunksIndexed}。\n" +
                   $"导入输出目录：{result.OutputDirectory}\n" +
                   $"导入文件：{string.Join(", ", result.ImportedSources.Take(20))}";
        }
        catch (Exception ex)
        {
            return $"导入课程资料失败：{ex.Message}";
        }
    }
}
