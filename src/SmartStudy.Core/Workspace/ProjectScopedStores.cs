using SmartStudy.Core.Rag;
using SmartStudy.Core.Tools.Builtin;
using System.Text.Json;

namespace SmartStudy.Core.Workspace;

public sealed class ProjectVectorStore : IVectorStore
{
    private readonly LearningProjectService _projects;
    private readonly JsonPersistentVectorStore _inner = new();
    private string _loadedIndexFile = "";

    public ProjectVectorStore(LearningProjectService projects)
    {
        _projects = projects;
        _projects.Changed += () => _loadedIndexFile = "";
    }

    public int Count
    {
        get
        {
            EnsureCurrentLoaded();
            return _inner.Count;
        }
    }

    public IReadOnlyList<KnowledgeChunk> Chunks
    {
        get
        {
            EnsureCurrentLoaded();
            return _inner.Chunks;
        }
    }

    public string StoreKind => "ProjectJsonPersistent";

    public string? PersistencePath
    {
        get
        {
            EnsureCurrentLoaded();
            return _inner.PersistencePath;
        }
    }

    public async Task SaveAsync(string path, CancellationToken ct = default)
    {
        await _inner.SaveAsync(path, ct);
        _loadedIndexFile = Path.GetFullPath(path);
    }

    public async Task LoadAsync(string path, CancellationToken ct = default)
    {
        await _inner.LoadAsync(path, ct);
        _loadedIndexFile = Path.GetFullPath(path);
    }

    public void Replace(IEnumerable<KnowledgeChunk> chunks) => _inner.Replace(chunks);

    public IReadOnlyList<SearchResult> Search(float[] queryVector, int topK)
    {
        EnsureCurrentLoaded();
        return _inner.Search(queryVector, topK);
    }

    private void EnsureCurrentLoaded()
    {
        var indexFile = Path.GetFullPath(_projects.GetCurrentProjectPaths().IndexFile);
        if (string.Equals(_loadedIndexFile, indexFile, StringComparison.OrdinalIgnoreCase)) return;
        LoadCurrentIndex(indexFile);
        _loadedIndexFile = indexFile;
    }

    private void LoadCurrentIndex(string indexFile)
    {
        if (!File.Exists(indexFile))
        {
            _inner.Replace(Array.Empty<KnowledgeChunk>());
            return;
        }

        using var fs = File.OpenRead(indexFile);
        using var doc = JsonDocument.Parse(fs);
        var chunks = doc.RootElement.ValueKind switch
        {
            JsonValueKind.Array => doc.RootElement.Deserialize<List<KnowledgeChunk>>(JsonSerializerOptions.Web),
            JsonValueKind.Object when doc.RootElement.TryGetProperty("chunks", out var items) =>
                items.Deserialize<List<KnowledgeChunk>>(JsonSerializerOptions.Web),
            _ => new List<KnowledgeChunk>()
        };

        _inner.Replace(chunks ?? new List<KnowledgeChunk>());
    }
}

public sealed class ProjectNoteStore : INoteStore
{
    private readonly LearningProjectService _projects;
    public ProjectNoteStore(LearningProjectService projects) => _projects = projects;

    public Task<Note> AddAsync(Note note, CancellationToken ct = default) =>
        Store().AddAsync(note, ct);

    public Task<IReadOnlyList<Note>> ListAsync(string? tagFilter, string? keyword, CancellationToken ct = default) =>
        Store().ListAsync(tagFilter, keyword, ct);

    private JsonNoteStore Store() => new(_projects.GetCurrentProjectPaths().NotesFile);
}

public sealed class ProjectLearningProfileStore : ILearningProfileStore
{
    private readonly LearningProjectService _projects;
    public ProjectLearningProfileStore(LearningProjectService projects) => _projects = projects;

    public Task<LearningProfile> GetAsync(CancellationToken ct = default) =>
        Store().GetAsync(ct);

    public Task<LearningProfile> UpdateAsync(LearningProfileUpdate update, CancellationToken ct = default) =>
        Store().UpdateAsync(update, ct);

    private JsonLearningProfileStore Store() => new(_projects.GetCurrentProjectPaths().LearningProfileFile);
}

public sealed class ProjectStudyProgressStore : IStudyProgressStore
{
    private readonly LearningProjectService _projects;
    public ProjectStudyProgressStore(LearningProjectService projects) => _projects = projects;

    public Task<StudyTask> AddTaskAsync(StudyTask task, CancellationToken ct = default) =>
        Store().AddTaskAsync(task, ct);

    public Task<StudyTask?> MarkDoneAsync(string taskIdOrTitle, string reflection, int? actualMinutes, CancellationToken ct = default) =>
        Store().MarkDoneAsync(taskIdOrTitle, reflection, actualMinutes, ct);

    public Task<StudySession> AddSessionAsync(StudySession session, CancellationToken ct = default) =>
        Store().AddSessionAsync(session, ct);

    public Task<StudyProgressSnapshot> GetAsync(CancellationToken ct = default) =>
        Store().GetAsync(ct);

    private JsonStudyProgressStore Store() => new(_projects.GetCurrentProjectPaths().StudyProgressFile);
}

public sealed class ProjectQuizResultStore : IQuizResultStore
{
    private readonly LearningProjectService _projects;
    public ProjectQuizResultStore(LearningProjectService projects) => _projects = projects;

    public Task<QuizAttempt> AddAsync(QuizAttempt attempt, CancellationToken ct = default) =>
        Store().AddAsync(attempt, ct);

    public Task<IReadOnlyList<QuizAttempt>> ListAsync(bool mistakesOnly, string? topic, CancellationToken ct = default) =>
        Store().ListAsync(mistakesOnly, topic, ct);

    private JsonQuizResultStore Store() => new(_projects.GetCurrentProjectPaths().QuizResultsFile);
}

public sealed class ProjectQuizSessionStore : IQuizSessionStore
{
    private readonly LearningProjectService _projects;
    public ProjectQuizSessionStore(LearningProjectService projects) => _projects = projects;

    public Task<QuizSession> SaveAsync(QuizSession session, CancellationToken ct = default) =>
        Store().SaveAsync(session, ct);

    public Task<QuizSession?> GetAsync(string quizId, CancellationToken ct = default) =>
        Store().GetAsync(quizId, ct);

    public Task<QuizSession?> GetLatestAsync(CancellationToken ct = default) =>
        Store().GetLatestAsync(ct);

    private JsonQuizSessionStore Store() => new(_projects.GetCurrentProjectPaths().QuizSessionsFile);
}
