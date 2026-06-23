using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartStudy.Core.Rag;

public sealed class KnowledgeChunk
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("vector")] public float[] Vector { get; set; } = Array.Empty<float>();
}

public sealed record SearchResult(KnowledgeChunk Chunk, double Score);

public interface IVectorStore
{
    int Count { get; }
    IReadOnlyList<KnowledgeChunk> Chunks { get; }
    string StoreKind { get; }
    string? PersistencePath { get; }
    Task SaveAsync(string path, CancellationToken ct = default);
    Task LoadAsync(string path, CancellationToken ct = default);
    void Replace(IEnumerable<KnowledgeChunk> chunks);
    IReadOnlyList<SearchResult> Search(float[] queryVector, int topK);
}

/// <summary>内存向量库 + 余弦相似度。零外部依赖；本课程演示规模足够。</summary>
public sealed class InMemoryVectorStore : IVectorStore
{
    private List<KnowledgeChunk> _chunks = new();
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

    public int Count => _chunks.Count;
    public IReadOnlyList<KnowledgeChunk> Chunks => _chunks;
    public string StoreKind => "InMemory";
    public string? PersistencePath { get; private set; }

    public void Replace(IEnumerable<KnowledgeChunk> chunks) => _chunks = chunks.ToList();

    public IReadOnlyList<SearchResult> Search(float[] q, int topK)
    {
        if (_chunks.Count == 0) return Array.Empty<SearchResult>();
        var qNorm = Norm(q);
        return _chunks
            .Select(c => new SearchResult(c, Cosine(q, qNorm, c.Vector)))
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();
    }

    public async Task SaveAsync(string path, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await using var fs = File.Create(path);
        await JsonSerializer.SerializeAsync(fs, _chunks, Opts, ct);
        PersistencePath = Path.GetFullPath(path);
    }

    public async Task LoadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) { _chunks = new(); return; }
        await using var fs = File.OpenRead(path);
        _chunks = await JsonSerializer.DeserializeAsync<List<KnowledgeChunk>>(fs, Opts, ct) ?? new();
        PersistencePath = Path.GetFullPath(path);
    }

    private static double Cosine(float[] a, double aNorm, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, bn = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; bn += b[i] * b[i]; }
        var denom = aNorm * Math.Sqrt(bn);
        return denom == 0 ? 0 : dot / denom;
    }

    private static double Norm(float[] v)
    {
        double s = 0;
        for (int i = 0; i < v.Length; i++) s += v[i] * v[i];
        return Math.Sqrt(s);
    }
}

public sealed class VectorStoreSnapshot
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("createdUtc")] public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("chunkCount")] public int ChunkCount { get; set; }
    [JsonPropertyName("chunks")] public List<KnowledgeChunk> Chunks { get; set; } = new();
}

/// <summary>
/// JSON-backed persistent vector store. It keeps chunks in memory for fast cosine search
/// and persists the authoritative snapshot to disk so RAG survives process restarts.
/// </summary>
public sealed class JsonPersistentVectorStore : IVectorStore
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private List<KnowledgeChunk> _chunks = new();

    public int Count => _chunks.Count;
    public IReadOnlyList<KnowledgeChunk> Chunks => _chunks;
    public string StoreKind => "JsonPersistent";
    public string? PersistencePath { get; private set; }

    public void Replace(IEnumerable<KnowledgeChunk> chunks) => _chunks = chunks.ToList();

    public IReadOnlyList<SearchResult> Search(float[] q, int topK)
    {
        if (_chunks.Count == 0) return Array.Empty<SearchResult>();
        var qNorm = Norm(q);
        return _chunks
            .Select(c => new SearchResult(c, Cosine(q, qNorm, c.Vector)))
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();
    }

    public async Task SaveAsync(string path, CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var snapshot = new VectorStoreSnapshot
        {
            CreatedUtc = DateTime.UtcNow,
            ChunkCount = _chunks.Count,
            Chunks = _chunks
        };

        var tempPath = fullPath + ".tmp";
        await using (var fs = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(fs, snapshot, Opts, ct);
        }

        File.Move(tempPath, fullPath, overwrite: true);
        PersistencePath = fullPath;
    }

    public async Task LoadAsync(string path, CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            _chunks = new();
            PersistencePath = fullPath;
            return;
        }

        await using var fs = File.OpenRead(fullPath);
        using var doc = await JsonDocument.ParseAsync(fs, cancellationToken: ct);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            _chunks = doc.RootElement.Deserialize<List<KnowledgeChunk>>(Opts) ?? new();
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                 doc.RootElement.TryGetProperty("chunks", out var chunks))
        {
            _chunks = chunks.Deserialize<List<KnowledgeChunk>>(Opts) ?? new();
        }
        else
        {
            _chunks = new();
        }

        PersistencePath = fullPath;
    }

    private static double Cosine(float[] a, double aNorm, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, bn = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; bn += b[i] * b[i]; }
        var denom = aNorm * Math.Sqrt(bn);
        return denom == 0 ? 0 : dot / denom;
    }

    private static double Norm(float[] v)
    {
        double s = 0;
        for (int i = 0; i < v.Length; i++) s += v[i] * v[i];
        return Math.Sqrt(s);
    }
}
