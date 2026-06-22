using Microsoft.Extensions.Options;
using SmartStudy.Core.Configuration;
using SmartStudy.Core.Rag;
using SmartStudy.Core.Tools;
using SmartStudy.Core.Tools.Builtin;

namespace SmartStudy.Web.Services;

public sealed record WebToolSummary(string Name, string Description);

public sealed record DashboardState(
    string LlmProfile,
    string LlmModel,
    bool LlmApiKeyConfigured,
    string EmbeddingProvider,
    string EmbeddingModel,
    string KnowledgeDirectory,
    int KnowledgeFileCount,
    int ImportedMaterialCount,
    bool IndexFileExists,
    long IndexFileBytes,
    int LoadedChunkCount,
    IReadOnlyList<WebToolSummary> Tools,
    IReadOnlyList<Note> RecentNotes);

public sealed class DashboardStateService
{
    private readonly AgentOptions _options;
    private readonly LlmProfileManager _profiles;
    private readonly IVectorStore _store;
    private readonly ToolRegistry _tools;
    private readonly INoteStore _notes;

    public DashboardStateService(
        IOptions<AgentOptions> options,
        LlmProfileManager profiles,
        IVectorStore store,
        ToolRegistry tools,
        INoteStore notes)
    {
        _options = options.Value;
        _profiles = profiles;
        _store = store;
        _tools = tools;
        _notes = notes;
    }

    public async Task<DashboardState> GetAsync(CancellationToken ct = default)
    {
        var rag = _options.Rag;
        var knowledgeDir = Path.GetFullPath(rag.KnowledgeDirectory);
        var importedDir = Path.Combine(knowledgeDir, "imported");
        var indexFile = Path.GetFullPath(rag.IndexFile);
        var llm = _profiles.Current;
        var notes = await _notes.ListAsync(null, null, ct);

        return new DashboardState(
            _profiles.CurrentName,
            llm.Model,
            !string.IsNullOrWhiteSpace(llm.ApiKey),
            _options.Embedding.Provider,
            _options.Embedding.Model,
            knowledgeDir,
            Directory.Exists(knowledgeDir) ? Directory.EnumerateFiles(knowledgeDir, "*.md", SearchOption.AllDirectories).Count() : 0,
            Directory.Exists(importedDir) ? Directory.EnumerateFiles(importedDir, "*.md", SearchOption.TopDirectoryOnly).Count() : 0,
            File.Exists(indexFile),
            File.Exists(indexFile) ? new FileInfo(indexFile).Length : 0,
            _store.Count,
            _tools.All
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(t => new WebToolSummary(t.Name, t.Description))
                .ToList(),
            notes.Take(5).ToList());
    }
}
