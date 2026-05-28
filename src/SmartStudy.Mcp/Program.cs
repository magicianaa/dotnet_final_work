using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SmartStudy.Core.Tools.Builtin;

// 把日志写到 stderr，stdout 留给 JSON-RPC
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// 与 CLI 共用持久化文件
var noteFile = Environment.GetEnvironmentVariable("SMARTSTUDY_NOTES")
               ?? Path.Combine(AppContext.BaseDirectory, "data", "notes.json");
builder.Services.AddSingleton<INoteStore>(_ => new JsonNoteStore(noteFile));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();


/// <summary>把笔记能力暴露为 MCP Server，证明同一套领域逻辑可以被任何 MCP 客户端复用。</summary>
[McpServerToolType]
public sealed class NoteMcpTools
{
    private readonly INoteStore _store;
    public NoteMcpTools(INoteStore store) => _store = store;

    [McpServerTool, Description("保存一条学习笔记并返回新笔记 Id。")]
    public async Task<string> AddNote(
        [Description("笔记标题")] string title,
        [Description("笔记正文")] string content,
        [Description("逗号分隔的标签，可选")] string? tags = null,
        CancellationToken ct = default)
    {
        var tagList = string.IsNullOrWhiteSpace(tags)
            ? new List<string>()
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var note = await _store.AddAsync(new Note { Title = title, Content = content, Tags = tagList }, ct);
        return $"saved id={note.Id}";
    }

    [McpServerTool, Description("按标签或关键字检索笔记。两个参数均可省略。")]
    public async Task<string> ListNotes(
        [Description("按标签过滤，可选")] string? tag = null,
        [Description("按关键字过滤，可选")] string? keyword = null,
        CancellationToken ct = default)
    {
        var list = await _store.ListAsync(tag, keyword, ct);
        if (list.Count == 0) return "(empty)";
        return string.Join("\n", list.Select(n => $"[{n.Id}] {n.Title} :: {n.Content}"));
    }
}
