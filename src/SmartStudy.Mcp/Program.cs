using System.ComponentModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using SmartStudy.Core.Configuration;
using SmartStudy.Core.Rag;
using SmartStudy.Core.Tools.Builtin;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);

// 把日志写到 stderr，stdout 留给 JSON-RPC
var builder = Host.CreateApplicationBuilder(args);
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables(prefix: "SMARTSTUDY_");
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.AddSingleton<IRagRuntimeContext, DefaultRagRuntimeContext>();

// 与 CLI 共用持久化文件
var noteFile = Environment.GetEnvironmentVariable("SMARTSTUDY_NOTES")
               ?? Path.Combine(AppContext.BaseDirectory, "data", "notes.json");
builder.Services.AddSingleton<INoteStore>(_ => new JsonNoteStore(noteFile));
builder.Services.AddHttpClient<ZhipuEmbeddingClient>();
builder.Services.AddSingleton<LocalHashEmbeddingClient>();
builder.Services.AddSingleton<IEmbeddingClient>(sp =>
{
    var provider = sp.GetRequiredService<IOptions<AgentOptions>>().Value.Embedding.Provider;
    return provider.Equals("local", StringComparison.OrdinalIgnoreCase)
        ? sp.GetRequiredService<LocalHashEmbeddingClient>()
        : sp.GetRequiredService<ZhipuEmbeddingClient>();
});
builder.Services.AddSingleton<IVectorStore, JsonPersistentVectorStore>();
builder.Services.AddSingleton(sp => new KnowledgeSearchService(
    sp.GetRequiredService<IEmbeddingClient>(),
    sp.GetRequiredService<IVectorStore>(),
    sp.GetRequiredService<IRagRuntimeContext>()));
builder.Services.AddSingleton(sp => new CourseMaterialCatalog(
    sp.GetRequiredService<IRagRuntimeContext>()));
builder.Services.AddHostedService<RagIndexLoader>();

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

/// <summary>把课程资料检索能力暴露为 MCP 工具，供外部 Host 直接复用 SmartStudy 的 RAG。</summary>
[McpServerToolType]
public sealed class KnowledgeMcpTools
{
    private readonly KnowledgeSearchService _search;
    private readonly CourseMaterialCatalog _catalog;

    public KnowledgeMcpTools(KnowledgeSearchService search, CourseMaterialCatalog catalog)
    {
        _search = search;
        _catalog = catalog;
    }

    [McpServerTool, Description("在 SmartStudy 课程知识库中按语义检索相关片段。")]
    public Task<string> SearchKnowledge(
        [Description("检索问题或关键词")] string query,
        [Description("返回片段数量，默认 4，范围 1-8")] int topK = 4,
        CancellationToken ct = default)
    {
        return _search.SearchAsync(query, topK, ct);
    }

    [McpServerTool, Description("列出已经导入 SmartStudy 知识库的课程资料文件。")]
    public string ListImportedMaterials(
        [Description("最多返回多少个文件，默认 50")] int limit = 50)
    {
        var materials = _catalog.ListImportedMaterials(limit);
        if (materials.Count == 0) return "(empty)";
        return string.Join("\n", materials.Select(m => $"{m.FileName} :: {m.Bytes} bytes :: {m.LastWriteTime:yyyy-MM-dd HH:mm}"));
    }
}

public sealed class RagIndexLoader : IHostedService
{
    private readonly IVectorStore _store;
    private readonly AgentOptions _options;
    private readonly ILogger<RagIndexLoader> _logger;

    public RagIndexLoader(IVectorStore store, IOptions<AgentOptions> options, ILogger<RagIndexLoader> logger)
    {
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var indexFile = ResolveIndexFile();
        if (!File.Exists(indexFile))
        {
            _logger.LogWarning("RAG index not found. SearchKnowledge will report that the knowledge base is not indexed. Checked: {IndexFile}", Path.GetFullPath(indexFile));
            return;
        }

        await _store.LoadAsync(indexFile, cancellationToken);
        _logger.LogInformation("Loaded RAG index from {IndexFile}, chunks={Count}", Path.GetFullPath(indexFile), _store.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private string ResolveIndexFile()
    {
        var configured = Path.GetFullPath(_options.Rag.IndexFile);
        if (File.Exists(configured)) return configured;

        var cliDebugIndex = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "SmartStudy.Cli", "bin", "Debug", "net8.0",
            _options.Rag.IndexFile));
        if (File.Exists(cliDebugIndex)) return cliDebugIndex;

        return configured;
    }
}
