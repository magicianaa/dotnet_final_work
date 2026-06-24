using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SmartStudy.Core.Configuration;
using SmartStudy.Core.Llm;
using SmartStudy.Core.Memory;
using SmartStudy.Core.Rag;

namespace SmartStudy.Core.Workspace;

internal static class LearningProjectJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

public sealed class LearningProject
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("sourceDirectory")] public string SourceDirectory { get; set; } = "";
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.Now;
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public sealed class LearningConversation
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    [JsonPropertyName("projectId")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "新对话";
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.Now;
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public sealed record LearningWorkspaceState(
    IReadOnlyList<LearningProject> Projects,
    LearningProject CurrentProject,
    IReadOnlyList<LearningConversation> Conversations,
    LearningConversation CurrentConversation);

public sealed class ProjectPaths
{
    public ProjectPaths(string root)
    {
        Root = root;
        DataDirectory = Path.Combine(root, "data");
        KnowledgeDirectory = Path.Combine(root, "knowledge");
        IndexFile = Path.Combine(DataDirectory, "index.json");
        NotesFile = Path.Combine(DataDirectory, "notes.json");
        LearningProfileFile = Path.Combine(DataDirectory, "learning-profile.json");
        StudyProgressFile = Path.Combine(DataDirectory, "study-progress.json");
        QuizResultsFile = Path.Combine(DataDirectory, "quiz-results.json");
        QuizSessionsFile = Path.Combine(DataDirectory, "quiz-sessions.json");
        ConversationsFile = Path.Combine(DataDirectory, "conversations.json");
        MemoryDirectory = Path.Combine(DataDirectory, "memory");
    }

    public string Root { get; }
    public string DataDirectory { get; }
    public string KnowledgeDirectory { get; }
    public string IndexFile { get; }
    public string NotesFile { get; }
    public string LearningProfileFile { get; }
    public string StudyProgressFile { get; }
    public string QuizResultsFile { get; }
    public string QuizSessionsFile { get; }
    public string ConversationsFile { get; }
    public string MemoryDirectory { get; }

    public string MemoryFile(string conversationId) => Path.Combine(MemoryDirectory, $"{SanitizeId(conversationId)}.json");

    private static string SanitizeId(string value) =>
        string.Concat(value.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'));
}

public sealed class LearningProjectService : IRagRuntimeContext
{
    private sealed class ProjectCatalog
    {
        [JsonPropertyName("activeProjectId")] public string ActiveProjectId { get; set; } = "";
        [JsonPropertyName("projects")] public List<LearningProject> Projects { get; set; } = new();
    }

    private sealed class ConversationCatalog
    {
        [JsonPropertyName("activeConversationId")] public string ActiveConversationId { get; set; } = "";
        [JsonPropertyName("conversations")] public List<LearningConversation> Conversations { get; set; } = new();
    }

    private readonly AgentOptions _options;
    private readonly string _workspaceBaseDirectory;
    private readonly string _dataDirectory;
    private readonly string _rootDirectory;
    private readonly string _catalogFile;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly object _syncGate = new();
    private bool _loaded;
    private ProjectCatalog _catalog = new();
    private string _activeConversationId = "";

    public LearningProjectService(IOptions<AgentOptions> options)
    {
        _options = options.Value;
        var configuredDataDir = Path.GetDirectoryName(_options.Rag.IndexFile);
        _workspaceBaseDirectory = ResolveWorkspaceBaseDirectory();
        _dataDirectory = string.IsNullOrWhiteSpace(configuredDataDir) ? "data" : configuredDataDir;
        var sharedDataDirectory = ResolvePathFromWorkspace(_dataDirectory);
        _rootDirectory = Path.GetFullPath(Path.Combine(sharedDataDirectory, "web-projects"));
        _catalogFile = Path.Combine(_rootDirectory, "projects.json");
    }

    public event Action? Changed;

    public RagOptions Current
    {
        get
        {
            EnsureLoaded();
            var paths = GetProjectPaths(CurrentProject.Id);
            return new RagOptions
            {
                KnowledgeDirectory = paths.KnowledgeDirectory,
                IndexFile = paths.IndexFile,
                ChunkSize = _options.Rag.ChunkSize,
                ChunkOverlap = _options.Rag.ChunkOverlap,
                TopK = _options.Rag.TopK
            };
        }
    }

    public LearningProject CurrentProject
    {
        get
        {
            EnsureLoaded();
            return _catalog.Projects.First(p => p.Id == _catalog.ActiveProjectId);
        }
    }

    public LearningConversation CurrentConversation
    {
        get
        {
            EnsureLoaded();
            return GetConversationCatalog(CurrentProject.Id)
                .Conversations
                .First(c => c.Id == _activeConversationId);
        }
    }

    public async Task<LearningWorkspaceState> GetStateAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        var conversations = await ReadConversationCatalogAsync(CurrentProject.Id, ct);
        return new LearningWorkspaceState(
            _catalog.Projects.OrderByDescending(p => p.UpdatedAt).ToList(),
            CurrentProject,
            conversations.Conversations.OrderByDescending(c => c.UpdatedAt).ToList(),
            conversations.Conversations.First(c => c.Id == conversations.ActiveConversationId));
    }

    public async Task<LearningProject> CreateProjectAsync(string sourceDirectory, string? name = null, CancellationToken ct = default)
    {
        var directory = NormalizeDirectory(sourceDirectory);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"项目目录不存在：{directory}");

        await _lock.WaitAsync(ct);
        try
        {
            await EnsureLoadedCoreAsync(ct);
            var project = new LearningProject
            {
                Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) : name.Trim(),
                SourceDirectory = directory
            };

            _catalog.Projects.Add(project);
            _catalog.ActiveProjectId = project.Id;
            EnsureProjectDirectories(project.Id);
            var conversations = NewConversationCatalog(project.Id, "默认对话");
            await WriteConversationCatalogAsync(project.Id, conversations, ct);
            _activeConversationId = conversations.ActiveConversationId;
            await WriteProjectCatalogAsync(ct);
        }
        finally
        {
            _lock.Release();
        }

        Changed?.Invoke();
        return CurrentProject;
    }

    public async Task SelectProjectAsync(string projectId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureLoadedCoreAsync(ct);
            var project = FindProject(projectId)
                ?? throw new InvalidOperationException($"未找到学习项目：{projectId}");
            project.UpdatedAt = DateTime.Now;
            _catalog.ActiveProjectId = project.Id;
            var conversations = await ReadConversationCatalogAsync(project.Id, ct);
            _activeConversationId = conversations.ActiveConversationId;
            await WriteProjectCatalogAsync(ct);
        }
        finally
        {
            _lock.Release();
        }

        Changed?.Invoke();
    }

    public async Task<LearningProject> DeleteProjectAsync(string projectId, CancellationToken ct = default)
    {
        LearningProject deleted;
        string deletedRoot;

        await _lock.WaitAsync(ct);
        try
        {
            await EnsureLoadedCoreAsync(ct);
            if (_catalog.Projects.Count <= 1)
                throw new InvalidOperationException("至少需要保留一个学习项目。");

            var project = FindProject(projectId)
                ?? throw new InvalidOperationException($"未找到学习项目：{projectId}");

            deleted = project;
            deletedRoot = Path.Combine(_rootDirectory, deleted.Id);
            var wasActive = string.Equals(_catalog.ActiveProjectId, deleted.Id, StringComparison.OrdinalIgnoreCase);
            _catalog.Projects.Remove(project);

            if (wasActive)
            {
                var fallback = _catalog.Projects.OrderByDescending(p => p.UpdatedAt).First();
                _catalog.ActiveProjectId = fallback.Id;
                var conversations = await ReadConversationCatalogAsync(fallback.Id, ct);
                _activeConversationId = conversations.ActiveConversationId;
            }

            await WriteProjectCatalogAsync(ct);
        }
        finally
        {
            _lock.Release();
        }

        DeleteDirectoryIfExists(deletedRoot);
        Changed?.Invoke();
        return deleted;
    }

    public async Task<LearningConversation> CreateConversationAsync(string? title = null, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureLoadedCoreAsync(ct);
            var project = CurrentProject;
            var conversations = await ReadConversationCatalogAsync(project.Id, ct);
            var conversation = new LearningConversation
            {
                ProjectId = project.Id,
                Title = string.IsNullOrWhiteSpace(title) ? $"学习对话 {conversations.Conversations.Count + 1}" : title.Trim()
            };
            conversations.Conversations.Add(conversation);
            conversations.ActiveConversationId = conversation.Id;
            _activeConversationId = conversation.Id;
            project.UpdatedAt = DateTime.Now;
            await WriteConversationCatalogAsync(project.Id, conversations, ct);
            await WriteProjectCatalogAsync(ct);
            Changed?.Invoke();
            return conversation;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SelectConversationAsync(string conversationId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureLoadedCoreAsync(ct);
            var project = CurrentProject;
            var conversations = await ReadConversationCatalogAsync(project.Id, ct);
            var conversation = FindConversation(conversations, conversationId)
                ?? throw new InvalidOperationException($"未找到学习对话：{conversationId}");
            conversation.UpdatedAt = DateTime.Now;
            conversations.ActiveConversationId = conversation.Id;
            _activeConversationId = conversation.Id;
            project.UpdatedAt = DateTime.Now;
            await WriteConversationCatalogAsync(project.Id, conversations, ct);
            await WriteProjectCatalogAsync(ct);
        }
        finally
        {
            _lock.Release();
        }

        Changed?.Invoke();
    }

    public async Task<LearningConversation> DeleteConversationAsync(string conversationId, CancellationToken ct = default)
    {
        LearningConversation deleted;
        string memoryFile;

        await _lock.WaitAsync(ct);
        try
        {
            await EnsureLoadedCoreAsync(ct);
            var project = CurrentProject;
            var conversations = await ReadConversationCatalogAsync(project.Id, ct);
            var conversation = FindConversation(conversations, conversationId)
                ?? throw new InvalidOperationException($"未找到学习对话：{conversationId}");

            deleted = conversation;
            memoryFile = GetProjectPaths(project.Id).MemoryFile(deleted.Id);
            conversations.Conversations.Remove(conversation);

            if (conversations.Conversations.Count == 0)
            {
                conversations = NewConversationCatalog(project.Id, "默认对话");
            }
            else if (string.Equals(conversations.ActiveConversationId, deleted.Id, StringComparison.OrdinalIgnoreCase))
            {
                conversations.ActiveConversationId = conversations.Conversations
                    .OrderByDescending(c => c.UpdatedAt)
                    .First()
                    .Id;
            }

            _activeConversationId = conversations.ActiveConversationId;
            project.UpdatedAt = DateTime.Now;
            await WriteConversationCatalogAsync(project.Id, conversations, ct);
            await WriteProjectCatalogAsync(ct);
        }
        finally
        {
            _lock.Release();
        }

        DeleteFileIfExists(memoryFile);
        Changed?.Invoke();
        return deleted;
    }

    public async Task TouchConversationAsync(string? titleHint = null, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureLoadedCoreAsync(ct);
            var project = CurrentProject;
            var conversations = await ReadConversationCatalogAsync(project.Id, ct);
            var conversation = conversations.Conversations.FirstOrDefault(c => c.Id == _activeConversationId);
            if (conversation is not null)
            {
                if (!string.IsNullOrWhiteSpace(titleHint) && conversation.Title.StartsWith("学习对话 ", StringComparison.OrdinalIgnoreCase))
                    conversation.Title = titleHint.Trim().Length > 28 ? titleHint.Trim()[..28] + "..." : titleHint.Trim();
                conversation.UpdatedAt = DateTime.Now;
            }
            project.UpdatedAt = DateTime.Now;
            await WriteConversationCatalogAsync(project.Id, conversations, ct);
            await WriteProjectCatalogAsync(ct);
        }
        finally
        {
            _lock.Release();
        }

        Changed?.Invoke();
    }

    public ProjectPaths GetCurrentProjectPaths()
    {
        EnsureLoaded();
        return GetProjectPaths(CurrentProject.Id);
    }

    public ProjectPaths GetProjectPaths(string projectId)
    {
        var paths = new ProjectPaths(Path.Combine(_rootDirectory, projectId));
        Directory.CreateDirectory(paths.Root);
        Directory.CreateDirectory(paths.DataDirectory);
        Directory.CreateDirectory(paths.KnowledgeDirectory);
        Directory.CreateDirectory(paths.MemoryDirectory);
        return paths;
    }

    public string CurrentMemoryFile()
    {
        EnsureLoaded();
        return GetCurrentProjectPaths().MemoryFile(_activeConversationId);
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureLoadedCoreAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_syncGate)
        {
            if (_loaded) return;
            EnsureLoadedCore();
        }
    }

    private async Task EnsureLoadedCoreAsync(CancellationToken ct)
    {
        if (_loaded) return;

        Directory.CreateDirectory(_rootDirectory);
        MigrateLegacyProjectDataIfNeeded();
        if (File.Exists(_catalogFile))
        {
            await using var fs = OpenReadShared(_catalogFile);
            _catalog = await JsonSerializer.DeserializeAsync<ProjectCatalog>(fs, LearningProjectJson.Options, ct) ?? new ProjectCatalog();
        }

        if (_catalog.Projects.Count == 0)
        {
            var defaultProject = new LearningProject
            {
                Id = "default",
                Name = "默认项目",
                SourceDirectory = ResolvePathFromWorkspace(_options.Rag.KnowledgeDirectory)
            };
            _catalog.Projects.Add(defaultProject);
            _catalog.ActiveProjectId = defaultProject.Id;
            EnsureProjectDirectories(defaultProject.Id);
            SeedDefaultProjectIfEmpty(defaultProject.Id);
            await WriteConversationCatalogAsync(defaultProject.Id, NewConversationCatalog(defaultProject.Id, "默认对话"), ct);
            await WriteProjectCatalogAsync(ct);
        }

        if (NormalizeProjectCatalog())
            await WriteProjectCatalogAsync(ct);

        if (_catalog.ActiveProjectId.Equals("default", StringComparison.OrdinalIgnoreCase))
            SeedDefaultProjectIfEmpty(_catalog.ActiveProjectId);

        var conversationCatalog = await ReadConversationCatalogAsync(_catalog.ActiveProjectId, ct);
        _activeConversationId = conversationCatalog.ActiveConversationId;
        _loaded = true;
    }

    private void EnsureLoadedCore()
    {
        if (_loaded) return;

        Directory.CreateDirectory(_rootDirectory);
        MigrateLegacyProjectDataIfNeeded();
        if (File.Exists(_catalogFile))
        {
            using var fs = OpenReadShared(_catalogFile);
            _catalog = JsonSerializer.Deserialize<ProjectCatalog>(fs, LearningProjectJson.Options) ?? new ProjectCatalog();
        }

        if (_catalog.Projects.Count == 0)
        {
            var defaultProject = new LearningProject
            {
                Id = "default",
                Name = "默认项目",
                SourceDirectory = ResolvePathFromWorkspace(_options.Rag.KnowledgeDirectory)
            };
            _catalog.Projects.Add(defaultProject);
            _catalog.ActiveProjectId = defaultProject.Id;
            EnsureProjectDirectories(defaultProject.Id);
            SeedDefaultProjectIfEmpty(defaultProject.Id);
            WriteConversationCatalog(defaultProject.Id, NewConversationCatalog(defaultProject.Id, "默认对话"));
            WriteProjectCatalog();
        }

        if (NormalizeProjectCatalog())
            WriteProjectCatalog();

        if (_catalog.ActiveProjectId.Equals("default", StringComparison.OrdinalIgnoreCase))
            SeedDefaultProjectIfEmpty(_catalog.ActiveProjectId);

        var conversationCatalog = ReadConversationCatalog(_catalog.ActiveProjectId);
        _activeConversationId = conversationCatalog.ActiveConversationId;
        _loaded = true;
    }

    private ConversationCatalog NewConversationCatalog(string projectId, string title)
    {
        var conversation = new LearningConversation
        {
            ProjectId = projectId,
            Title = title
        };
        return new ConversationCatalog
        {
            ActiveConversationId = conversation.Id,
            Conversations = new List<LearningConversation> { conversation }
        };
    }

    private bool NormalizeProjectCatalog()
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(_catalog.ActiveProjectId)
            || _catalog.Projects.All(p => p.Id != _catalog.ActiveProjectId))
        {
            _catalog.ActiveProjectId = _catalog.Projects[0].Id;
            changed = true;
        }

        var defaultProject = _catalog.Projects.FirstOrDefault(p => p.Id.Equals("default", StringComparison.OrdinalIgnoreCase));
        if (defaultProject is not null)
        {
            var defaultSource = ResolvePathFromWorkspace(_options.Rag.KnowledgeDirectory);
            if (!string.Equals(defaultProject.SourceDirectory, defaultSource, StringComparison.OrdinalIgnoreCase)
                && ShouldNormalizeDefaultSource(defaultProject.SourceDirectory))
            {
                defaultProject.SourceDirectory = defaultSource;
                defaultProject.UpdatedAt = DateTime.Now;
                changed = true;
            }
        }

        return changed;
    }

    private LearningProject? FindProject(string projectIdOrName)
    {
        var value = projectIdOrName.Trim();
        return _catalog.Projects.FirstOrDefault(p => p.Id.Equals(value, StringComparison.OrdinalIgnoreCase))
            ?? _catalog.Projects.FirstOrDefault(p => p.Name.Equals(value, StringComparison.OrdinalIgnoreCase))
            ?? _catalog.Projects.FirstOrDefault(p => p.Name.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private static LearningConversation? FindConversation(ConversationCatalog catalog, string conversationIdOrTitle)
    {
        var value = conversationIdOrTitle.Trim();
        return catalog.Conversations.FirstOrDefault(c => c.Id.Equals(value, StringComparison.OrdinalIgnoreCase))
            ?? catalog.Conversations.FirstOrDefault(c => c.Title.Equals(value, StringComparison.OrdinalIgnoreCase))
            ?? catalog.Conversations.FirstOrDefault(c => c.Title.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private ConversationCatalog GetConversationCatalog(string projectId) => ReadConversationCatalog(projectId);

    private ConversationCatalog ReadConversationCatalog(string projectId)
    {
        var paths = GetProjectPaths(projectId);
        ConversationCatalog catalog;
        if (File.Exists(paths.ConversationsFile))
        {
            using var fs = OpenReadShared(paths.ConversationsFile);
            catalog = JsonSerializer.Deserialize<ConversationCatalog>(fs, LearningProjectJson.Options) ?? new ConversationCatalog();
        }
        else
        {
            catalog = NewConversationCatalog(projectId, "默认对话");
            WriteConversationCatalog(projectId, catalog);
        }

        catalog = NormalizeConversationCatalog(projectId, catalog);
        return catalog;
    }

    private async Task<ConversationCatalog> ReadConversationCatalogAsync(string projectId, CancellationToken ct)
    {
        var paths = GetProjectPaths(projectId);
        ConversationCatalog catalog;
        if (File.Exists(paths.ConversationsFile))
        {
            await using var fs = OpenReadShared(paths.ConversationsFile);
            catalog = await JsonSerializer.DeserializeAsync<ConversationCatalog>(fs, LearningProjectJson.Options, ct) ?? new ConversationCatalog();
        }
        else
        {
            catalog = NewConversationCatalog(projectId, "默认对话");
            await WriteConversationCatalogAsync(projectId, catalog, ct);
        }

        catalog = NormalizeConversationCatalog(projectId, catalog);
        return catalog;
    }

    private ConversationCatalog NormalizeConversationCatalog(string projectId, ConversationCatalog catalog)
    {
        if (catalog.Conversations.Count == 0)
            catalog = NewConversationCatalog(projectId, "默认对话");

        if (string.IsNullOrWhiteSpace(catalog.ActiveConversationId)
            || catalog.Conversations.All(c => c.Id != catalog.ActiveConversationId))
        {
            catalog.ActiveConversationId = catalog.Conversations[0].Id;
        }

        return catalog;
    }

    private void WriteProjectCatalog()
    {
        WriteJsonFile(_catalogFile, _catalog);
    }

    private async Task WriteProjectCatalogAsync(CancellationToken ct)
    {
        await WriteJsonFileAsync(_catalogFile, _catalog, ct);
    }

    private async Task WriteConversationCatalogAsync(string projectId, ConversationCatalog catalog, CancellationToken ct)
    {
        var paths = GetProjectPaths(projectId);
        await WriteJsonFileAsync(paths.ConversationsFile, catalog, ct);
    }

    private void WriteConversationCatalog(string projectId, ConversationCatalog catalog)
    {
        var paths = GetProjectPaths(projectId);
        WriteJsonFile(paths.ConversationsFile, catalog);
    }

    private void EnsureProjectDirectories(string projectId)
    {
        var paths = GetProjectPaths(projectId);
        Directory.CreateDirectory(paths.DataDirectory);
        Directory.CreateDirectory(paths.KnowledgeDirectory);
        Directory.CreateDirectory(paths.MemoryDirectory);
    }

    private static FileStream OpenReadShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static async Task WriteJsonFileAsync<T>(string path, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(fs, value, LearningProjectJson.Options, ct);
            }

            ReplaceFileWithRetry(tempPath, path);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static void WriteJsonFile<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(fs, value, LearningProjectJson.Options);
            }

            ReplaceFileWithRetry(tempPath, path);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static void ReplaceFileWithRetry(string sourcePath, string targetPath)
    {
        const int attempts = 5;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                File.Move(sourcePath, targetPath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < attempts)
            {
                Thread.Sleep(40 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < attempts)
            {
                Thread.Sleep(40 * attempt);
            }
        }

        File.Move(sourcePath, targetPath, overwrite: true);
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private void SeedDefaultProjectIfEmpty(string projectId)
    {
        var paths = GetProjectPaths(projectId);
        SeedDefaultKnowledgeIfEmpty(paths);
        SeedDefaultIndexIfExists(paths);
    }

    private void SeedDefaultKnowledgeIfEmpty(ProjectPaths paths)
    {
        if (Directory.EnumerateFiles(paths.KnowledgeDirectory, "*.md", SearchOption.AllDirectories).Any())
            return;

        var source = ResolvePathFromWorkspace(_options.Rag.KnowledgeDirectory);
        if (!Directory.Exists(source)) return;

        foreach (var file in Directory.EnumerateFiles(source, "*.md", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(paths.KnowledgeDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private void SeedDefaultIndexIfExists(ProjectPaths paths)
    {
        if (File.Exists(paths.IndexFile)) return;

        var sourceIndex = CandidateDefaultIndexFiles().FirstOrDefault(File.Exists);
        if (sourceIndex is null) return;

        Directory.CreateDirectory(Path.GetDirectoryName(paths.IndexFile)!);
        File.Copy(sourceIndex, paths.IndexFile, overwrite: false);
    }

    private IEnumerable<string> CandidateDefaultIndexFiles()
    {
        yield return ResolvePathFromWorkspace(_options.Rag.IndexFile);
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, _options.Rag.IndexFile));
        yield return Path.GetFullPath(Path.Combine(
            _workspaceBaseDirectory,
            "src", "SmartStudy.Cli", "bin", "Debug", "net8.0",
            _options.Rag.IndexFile));
        yield return Path.GetFullPath(Path.Combine(
            _workspaceBaseDirectory,
            "src", "SmartStudy.Web", "bin", "Debug", "net8.0",
            _options.Rag.IndexFile));
    }

    private void MigrateLegacyProjectDataIfNeeded()
    {
        if (File.Exists(_catalogFile)) return;

        foreach (var legacyRoot in CandidateLegacyProjectRoots())
        {
            if (string.Equals(legacyRoot, _rootDirectory, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!File.Exists(Path.Combine(legacyRoot, "projects.json")))
                continue;

            CopyDirectory(legacyRoot, _rootDirectory);
            return;
        }
    }

    private IEnumerable<string> CandidateLegacyProjectRoots()
    {
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, _dataDirectory, "web-projects"));
        yield return Path.GetFullPath(Path.Combine(
            _workspaceBaseDirectory,
            "src", "SmartStudy.Web", "bin", "Debug", "net8.0",
            _dataDirectory, "web-projects"));
        yield return Path.GetFullPath(Path.Combine(
            _workspaceBaseDirectory,
            "src", "SmartStudy.Cli", "bin", "Debug", "net8.0",
            _dataDirectory, "web-projects"));
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(targetDirectory, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, file);
            var target = Path.Combine(targetDirectory, relative);
            if (File.Exists(target))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private string NormalizeDirectory(string directory) => ResolvePathFromWorkspace(directory);

    private string ResolvePathFromWorkspace(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        return Path.GetFullPath(Path.IsPathFullyQualified(expanded)
            ? expanded
            : Path.Combine(_workspaceBaseDirectory, expanded));
    }

    private static bool ShouldNormalizeDefaultSource(string sourceDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
            return true;
        if (!Directory.Exists(sourceDirectory))
            return true;

        var normalized = sourceDirectory.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalized.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveWorkspaceBaseDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SmartStudy.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        return AppContext.BaseDirectory;
    }
}

public sealed class ProjectConversationMemory : IConversationMemory
{
    private readonly LearningProjectService _projects;
    private readonly IOptions<AgentOptions> _options;
    private readonly int _maxNonSystem;
    private readonly object _gate = new();
    private string _loadedPath = "";
    private List<ChatMessage> _messages = new();

    public ProjectConversationMemory(
        LearningProjectService projects,
        IOptions<AgentOptions> options,
        int maxNonSystemMessages = 40)
    {
        _projects = projects;
        _options = options;
        _maxNonSystem = maxNonSystemMessages;
        _projects.Changed += ResetLoadedPath;
    }

    public IReadOnlyList<ChatMessage> Messages
    {
        get
        {
            EnsureLoaded();
            return _messages;
        }
    }

    public void AddSystem(string content)
    {
        EnsureLoaded();
        _messages.RemoveAll(m => m.Role == ChatRoles.System);
        _messages.Insert(0, new ChatMessage { Role = ChatRoles.System, Content = content });
        Save();
    }

    public void AddUser(string content)
    {
        EnsureLoaded();
        Append(new ChatMessage { Role = ChatRoles.User, Content = content });
    }

    public void AddAssistant(ChatMessage message)
    {
        EnsureLoaded();
        Append(message);
    }

    public void AddToolResult(string toolCallId, string toolName, string content)
    {
        EnsureLoaded();
        Append(new ChatMessage
        {
            Role = ChatRoles.Tool,
            ToolCallId = toolCallId,
            Name = toolName,
            Content = content
        });
    }

    public void Reload()
    {
        ResetLoadedPath();
        EnsureLoaded();
    }

    public void Reset()
    {
        EnsureLoaded();
        var sys = _messages.FirstOrDefault(m => m.Role == ChatRoles.System);
        _messages.Clear();
        _messages.Add(sys ?? new ChatMessage { Role = ChatRoles.System, Content = _options.Value.SystemPrompt });
        Save();
    }

    private void Append(ChatMessage msg)
    {
        _messages.Add(msg);
        var nonSys = _messages.Count(m => m.Role != ChatRoles.System);
        while (nonSys > _maxNonSystem)
        {
            var idx = _messages.FindIndex(m => m.Role != ChatRoles.System);
            if (idx < 0) break;
            _messages.RemoveAt(idx);
            nonSys--;
        }
        Save();
    }

    private void EnsureLoaded()
    {
        lock (_gate)
        {
            var path = _projects.CurrentMemoryFile();
            if (string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase)) return;

            _loadedPath = path;
            if (File.Exists(path))
            {
                using var fs = File.OpenRead(path);
                _messages = JsonSerializer.Deserialize<List<ChatMessage>>(fs, LearningProjectJson.Options) ?? new List<ChatMessage>();
            }
            else
            {
                _messages = new List<ChatMessage>();
            }

            if (_messages.All(m => m.Role != ChatRoles.System))
                _messages.Insert(0, new ChatMessage { Role = ChatRoles.System, Content = _options.Value.SystemPrompt });

            Save();
        }
    }

    private void Save()
    {
        var path = _loadedPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        WriteMemoryFile(path, _messages);
    }

    private static void WriteMemoryFile(string path, List<ChatMessage> messages)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(fs, messages, LearningProjectJson.Options);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private void ResetLoadedPath()
    {
        lock (_gate)
        {
            _loadedPath = "";
            _messages = new List<ChatMessage>();
        }
    }
}
