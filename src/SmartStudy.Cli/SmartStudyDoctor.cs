using Microsoft.Extensions.Options;
using SmartStudy.Core.Configuration;
using SmartStudy.Core.Rag;
using SmartStudy.Core.Tools;
using SmartStudy.Core.Tools.Builtin;
using SmartStudy.Core.Workspace;

namespace SmartStudy.Cli;

public sealed record DoctorStatusItem(string Name, string Value, bool IsHealthy, string Detail);

public sealed record ToolSummary(string Name, string Description);

public sealed record SmartStudyDoctorSnapshot(
    string BaseDirectory,
    string CurrentLlmProfile,
    string CurrentLlmModel,
    string CurrentLlmBaseUrl,
    bool CurrentLlmApiKeyConfigured,
    string EmbeddingProvider,
    string EmbeddingModel,
    bool EmbeddingApiKeyConfigured,
    string KnowledgeDirectory,
    bool KnowledgeDirectoryExists,
    int MarkdownFileCount,
    int ImportedMaterialCount,
    string IndexFile,
    bool IndexFileExists,
    long IndexFileBytes,
    string VectorStoreKind,
    string? VectorStorePersistencePath,
    int LoadedChunkCount,
    string NotesFile,
    bool NotesFileExists,
    int NoteCount,
    string LearningProfileFile,
    bool LearningProfileFileExists,
    IReadOnlyList<ToolSummary> Tools,
    IReadOnlyList<DoctorStatusItem> StatusItems)
{
    public bool IsHealthy => StatusItems.All(s => s.IsHealthy);
}

public sealed class SmartStudyDoctor
{
    private readonly AgentOptions _options;
    private readonly LlmProfileManager _profiles;
    private readonly IVectorStore _store;
    private readonly ToolRegistry _tools;
    private readonly IRagRuntimeContext _ragContext;
    private readonly LearningProjectService _projects;

    public SmartStudyDoctor(
        IOptions<AgentOptions> options,
        LlmProfileManager profiles,
        IVectorStore store,
        ToolRegistry tools,
        IRagRuntimeContext ragContext,
        LearningProjectService projects)
    {
        _options = options.Value;
        _profiles = profiles;
        _store = store;
        _tools = tools;
        _ragContext = ragContext;
        _projects = projects;
    }

    public async Task<SmartStudyDoctorSnapshot> InspectAsync(CancellationToken ct = default)
    {
        var llm = _profiles.Current;
        var rag = _ragContext.Current;
        var paths = _projects.GetCurrentProjectPaths();
        var embedding = _options.Embedding;
        var knowledgeDir = Path.GetFullPath(rag.KnowledgeDirectory);
        var importedDir = Path.Combine(knowledgeDir, "imported");
        var indexFile = Path.GetFullPath(rag.IndexFile);
        var notesFile = Path.GetFullPath(paths.NotesFile);
        var learningProfileFile = Path.GetFullPath(paths.LearningProfileFile);

        var knowledgeExists = Directory.Exists(knowledgeDir);
        var markdownCount = knowledgeExists
            ? Directory.EnumerateFiles(knowledgeDir, "*.md", SearchOption.AllDirectories).Count()
            : 0;
        var importedCount = Directory.Exists(importedDir)
            ? Directory.EnumerateFiles(importedDir, "*.md", SearchOption.TopDirectoryOnly).Count()
            : 0;
        var indexExists = File.Exists(indexFile);
        var indexBytes = indexExists ? new FileInfo(indexFile).Length : 0;
        var noteCount = await CountNotesAsync(notesFile, ct);

        var tools = _tools.All
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new ToolSummary(t.Name, t.Description))
            .ToList();

        var loadedChunkCount = _store.Count;
        var storePath = _store.PersistencePath ?? indexFile;
        var statuses = new List<DoctorStatusItem>
        {
            new(
                "LLM API Key",
                llm.ApiKey.Length > 0 ? "configured" : "missing",
                llm.ApiKey.Length > 0,
                llm.ApiKey.Length > 0
                    ? $"当前 profile `{_profiles.CurrentName}` 可解析到 API Key。"
                    : $"当前 profile `{_profiles.CurrentName}` 没有 API Key，请检查 appsettings.Local.json 或环境变量。"),
            new(
                "Embedding",
                embedding.Provider,
                embedding.Provider.Equals("local", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(embedding.ApiKey),
                embedding.Provider.Equals("local", StringComparison.OrdinalIgnoreCase)
                    ? $"本地 embedding，维度 {embedding.LocalDimensions}，不依赖网络。"
                    : "云端 embedding 需要配置 API Key。"),
            new(
                "Knowledge Directory",
                knowledgeExists ? "exists" : "missing",
                knowledgeExists,
                knowledgeExists ? $"Markdown 文件 {markdownCount} 个，导入课件 {importedCount} 个。" : $"目录不存在：{knowledgeDir}"),
            new(
                "RAG Index File",
                indexExists ? $"{indexBytes} bytes" : "missing",
                indexExists,
                indexExists
                    ? $"持久化索引快照存在；{_store.StoreKind} 已加载 chunk {loadedChunkCount} 个。路径：{storePath}"
                    : $"索引文件不存在：{indexFile}"),
            new(
                "Vector Store",
                _store.StoreKind,
                _store.StoreKind.Contains("Persistent", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(storePath),
                $"向量库类型：{_store.StoreKind}；持久化路径：{storePath}"),
            new(
                "Tool Registry",
                $"{tools.Count} tools",
                tools.Count >= 3,
                $"已注册工具：{string.Join(", ", tools.Select(t => t.Name))}"),
            new(
                "Notes",
                File.Exists(notesFile) ? $"{noteCount} notes" : "not created",
                true,
                File.Exists(notesFile) ? $"笔记文件存在：{notesFile}" : "尚未创建笔记文件，首次 add_note 后会自动生成。"),
            new(
                "Learning Profile",
                File.Exists(learningProfileFile) ? "created" : "not created",
                true,
                File.Exists(learningProfileFile) ? $"学习画像文件存在：{learningProfileFile}" : "尚未创建学习画像，首次 update_learning_profile 后会自动生成。")
        };

        return new SmartStudyDoctorSnapshot(
            AppContext.BaseDirectory,
            _profiles.CurrentName,
            llm.Model,
            llm.BaseUrl,
            llm.ApiKey.Length > 0,
            embedding.Provider,
            embedding.Model,
            !string.IsNullOrWhiteSpace(embedding.ApiKey),
            knowledgeDir,
            knowledgeExists,
            markdownCount,
            importedCount,
            indexFile,
            indexExists,
            indexBytes,
            _store.StoreKind,
            storePath,
            loadedChunkCount,
            notesFile,
            File.Exists(notesFile),
            noteCount,
            learningProfileFile,
            File.Exists(learningProfileFile),
            tools,
            statuses);
    }

    private static async Task<int> CountNotesAsync(string notesFile, CancellationToken ct)
    {
        if (!File.Exists(notesFile)) return 0;

        try
        {
            await using var fs = File.OpenRead(notesFile);
            var notes = await System.Text.Json.JsonSerializer.DeserializeAsync<List<Note>>(fs, cancellationToken: ct);
            return notes?.Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }
}
