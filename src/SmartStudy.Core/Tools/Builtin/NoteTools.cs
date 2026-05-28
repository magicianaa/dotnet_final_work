using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartStudy.Core.Tools.Builtin;

public sealed class Note
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>笔记存储抽象：便于替换为 SQLite/数据库实现，也方便单元测试。</summary>
public interface INoteStore
{
    Task<Note> AddAsync(Note note, CancellationToken ct = default);
    Task<IReadOnlyList<Note>> ListAsync(string? tagFilter, string? keyword, CancellationToken ct = default);
}

/// <summary>把笔记以 JSON 数组持久化到磁盘的简单实现（足够本课程演示）。</summary>
public sealed class JsonNoteStore : INoteStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public JsonNoteStore(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    private async Task<List<Note>> ReadAllAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return new();
        await using var fs = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<Note>>(fs, Opts, ct) ?? new();
    }

    private async Task WriteAllAsync(List<Note> notes, CancellationToken ct)
    {
        await using var fs = File.Create(_path);
        await JsonSerializer.SerializeAsync(fs, notes, Opts, ct);
    }

    public async Task<Note> AddAsync(Note note, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            all.Add(note);
            await WriteAllAsync(all, ct);
            return note;
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<Note>> ListAsync(string? tag, string? keyword, CancellationToken ct = default)
    {
        var all = await ReadAllAsync(ct);
        IEnumerable<Note> q = all;
        if (!string.IsNullOrWhiteSpace(tag))
            q = q.Where(n => n.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(keyword))
            q = q.Where(n => n.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                          || n.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        return q.OrderByDescending(n => n.CreatedAt).ToList();
    }
}

public sealed class AddNoteTool : ITool
{
    private readonly INoteStore _store;
    public AddNoteTool(INoteStore store) => _store = store;

    public string Name => "add_note";
    public string Description => "把一条学习笔记保存下来（持久化）。当用户说\"记录/笔记/记下来\"时调用。";

    public JsonElement ParametersSchema { get; } = JsonSchema.Build("""
    {
      "type": "object",
      "properties": {
        "title":   { "type": "string", "description": "笔记简短标题" },
        "content": { "type": "string", "description": "笔记正文" },
        "tags":    { "type": "array", "items": { "type": "string" }, "description": "可选标签" }
      },
      "required": ["title", "content"]
    }
    """);

    public async Task<string> InvokeAsync(JsonElement args, CancellationToken ct = default)
    {
        var title = args.GetProperty("title").GetString() ?? "";
        var content = args.GetProperty("content").GetString() ?? "";
        var tags = args.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList()
            : new List<string>();
        var note = await _store.AddAsync(new Note { Title = title, Content = content, Tags = tags }, ct);
        return $"已保存笔记 #{note.Id}：{note.Title}（标签：{string.Join(",", note.Tags)}）";
    }
}

public sealed class ListNotesTool : ITool
{
    private readonly INoteStore _store;
    public ListNotesTool(INoteStore store) => _store = store;

    public string Name => "list_notes";
    public string Description => "按标签或关键字检索已保存的学习笔记。不传参数时返回全部。";

    public JsonElement ParametersSchema { get; } = JsonSchema.Build("""
    {
      "type": "object",
      "properties": {
        "tag":     { "type": "string", "description": "按标签过滤，可选" },
        "keyword": { "type": "string", "description": "按标题/正文关键字过滤，可选" }
      }
    }
    """);

    public async Task<string> InvokeAsync(JsonElement args, CancellationToken ct = default)
    {
        var tag = args.TryGetProperty("tag", out var tg) ? tg.GetString() : null;
        var kw = args.TryGetProperty("keyword", out var kg) ? kg.GetString() : null;
        var list = await _store.ListAsync(tag, kw, ct);
        if (list.Count == 0) return "（没有符合条件的笔记）";
        return string.Join("\n", list.Take(20).Select(n =>
            $"- [{n.Id}] {n.Title}  标签:{string.Join(",", n.Tags)}  时间:{n.CreatedAt:yyyy-MM-dd HH:mm}\n  {n.Content}"));
    }
}
