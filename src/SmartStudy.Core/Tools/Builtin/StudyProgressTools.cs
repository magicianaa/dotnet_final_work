using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartStudy.Core.Tools.Builtin;

public sealed class StudyTask
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("topic")] public string Topic { get; set; } = "";
    [JsonPropertyName("minutes")] public int Minutes { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "todo";
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.Now;
    [JsonPropertyName("completedAt")] public DateTime? CompletedAt { get; set; }
    [JsonPropertyName("reflection")] public string Reflection { get; set; } = "";
}

public sealed class StudySession
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    [JsonPropertyName("taskId")] public string TaskId { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("topic")] public string Topic { get; set; } = "";
    [JsonPropertyName("minutes")] public int Minutes { get; set; }
    [JsonPropertyName("reflection")] public string Reflection { get; set; } = "";
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public sealed class StudyProgressSnapshot
{
    public IReadOnlyList<StudyTask> Tasks { get; init; } = Array.Empty<StudyTask>();
    public IReadOnlyList<StudySession> Sessions { get; init; } = Array.Empty<StudySession>();
}

public interface IStudyProgressStore
{
    Task<StudyTask> AddTaskAsync(StudyTask task, CancellationToken ct = default);
    Task<StudyTask?> MarkDoneAsync(string taskIdOrTitle, string reflection, int? actualMinutes, CancellationToken ct = default);
    Task<StudySession> AddSessionAsync(StudySession session, CancellationToken ct = default);
    Task<StudyProgressSnapshot> GetAsync(CancellationToken ct = default);
}

public sealed class JsonStudyProgressStore : IStudyProgressStore
{
    private sealed class StudyProgressData
    {
        [JsonPropertyName("tasks")] public List<StudyTask> Tasks { get; set; } = new();
        [JsonPropertyName("sessions")] public List<StudySession> Sessions { get; set; } = new();
    }

    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public JsonStudyProgressStore(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
    }

    public async Task<StudyTask> AddTaskAsync(StudyTask task, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var data = await ReadAsync(ct);
            data.Tasks.Add(task);
            await WriteAsync(data, ct);
            return task;
        }
        finally { _lock.Release(); }
    }

    public async Task<StudyTask?> MarkDoneAsync(string taskIdOrTitle, string reflection, int? actualMinutes, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var data = await ReadAsync(ct);
            var task = data.Tasks
                .Where(t => !t.Status.Equals("done", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(t => t.Id.Equals(taskIdOrTitle, StringComparison.OrdinalIgnoreCase))
                ?? data.Tasks
                    .Where(t => !t.Status.Equals("done", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault(t => t.Title.Contains(taskIdOrTitle, StringComparison.OrdinalIgnoreCase)
                                      || t.Topic.Contains(taskIdOrTitle, StringComparison.OrdinalIgnoreCase));
            if (task is null) return null;

            task.Status = "done";
            task.CompletedAt = DateTime.Now;
            task.Reflection = reflection.Trim();
            if (actualMinutes.HasValue) task.Minutes = Math.Max(0, actualMinutes.Value);

            data.Sessions.Add(new StudySession
            {
                TaskId = task.Id,
                Title = task.Title,
                Topic = task.Topic,
                Minutes = task.Minutes,
                Reflection = task.Reflection
            });

            await WriteAsync(data, ct);
            return task;
        }
        finally { _lock.Release(); }
    }

    public async Task<StudySession> AddSessionAsync(StudySession session, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var data = await ReadAsync(ct);
            data.Sessions.Add(session);
            await WriteAsync(data, ct);
            return session;
        }
        finally { _lock.Release(); }
    }

    public async Task<StudyProgressSnapshot> GetAsync(CancellationToken ct = default)
    {
        var data = await ReadAsync(ct);
        return new StudyProgressSnapshot { Tasks = data.Tasks, Sessions = data.Sessions };
    }

    private async Task<StudyProgressData> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return new StudyProgressData();
        await using var fs = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<StudyProgressData>(fs, Opts, ct) ?? new StudyProgressData();
    }

    private async Task WriteAsync(StudyProgressData data, CancellationToken ct)
    {
        await using var fs = File.Create(_path);
        await JsonSerializer.SerializeAsync(fs, data, Opts, ct);
    }
}

public sealed class AddStudyTaskTool : ITool
{
    private readonly IStudyProgressStore _store;
    public AddStudyTaskTool(IStudyProgressStore store) => _store = store;

    public string Name => "add_study_task";
    public string Description => "添加一个可追踪的学习任务，例如复习某个主题、完成一组练习、阅读某份课件。用户要求安排/加入/记录待办学习任务时调用。";

    public JsonElement ParametersSchema { get; } = JsonSchema.Build("""
    {
      "type": "object",
      "properties": {
        "title": { "type": "string", "description": "任务标题" },
        "topic": { "type": "string", "description": "关联知识点或资料名" },
        "minutes": { "type": "integer", "description": "预计学习分钟数，默认 30", "minimum": 1, "maximum": 480 }
      },
      "required": ["title"]
    }
    """);

    public async Task<string> InvokeAsync(JsonElement args, CancellationToken ct = default)
    {
        var title = ReadString(args, "title");
        if (string.IsNullOrWhiteSpace(title)) return "添加学习任务失败：必须提供 title。";
        var task = await _store.AddTaskAsync(new StudyTask
        {
            Title = title,
            Topic = ReadString(args, "topic"),
            Minutes = Math.Clamp(ReadInt(args, "minutes") ?? 30, 1, 480)
        }, ct);

        return $"已添加学习任务 #{task.Id}：{task.Title}（主题：{Empty(task.Topic)}，预计 {task.Minutes} 分钟）";
    }

    private static string ReadString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
    private static int? ReadInt(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : null;
    private static string Empty(string value) => string.IsNullOrWhiteSpace(value) ? "未指定" : value;
}

public sealed class MarkTaskDoneTool : ITool
{
    private readonly IStudyProgressStore _store;
    public MarkTaskDoneTool(IStudyProgressStore store) => _store = store;

    public string Name => "mark_task_done";
    public string Description => "把学习任务标记为完成，并记录实际学习时长和反思。用户说完成了某个任务、今天学完了某主题时调用。";

    public JsonElement ParametersSchema { get; } = JsonSchema.Build("""
    {
      "type": "object",
      "properties": {
        "task": { "type": "string", "description": "任务 id、标题关键词或主题关键词" },
        "reflection": { "type": "string", "description": "本次学习收获、卡点或复盘内容" },
        "actualMinutes": { "type": "integer", "description": "实际学习分钟数，可选", "minimum": 0, "maximum": 480 }
      },
      "required": ["task"]
    }
    """);

    public async Task<string> InvokeAsync(JsonElement args, CancellationToken ct = default)
    {
        var task = ReadString(args, "task");
        if (string.IsNullOrWhiteSpace(task)) return "标记失败：必须提供 task。";

        var done = await _store.MarkDoneAsync(task, ReadString(args, "reflection"), ReadInt(args, "actualMinutes"), ct);
        if (done is null)
            return $"未找到待完成的学习任务：{task}。可以先调用 add_study_task 添加任务，或提供更准确的任务 id。";

        return $"已完成学习任务 #{done.Id}：{done.Title}\n主题：{Empty(done.Topic)}\n用时：{done.Minutes} 分钟\n反思：{Empty(done.Reflection)}";
    }

    private static string ReadString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
    private static int? ReadInt(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : null;
    private static string Empty(string value) => string.IsNullOrWhiteSpace(value) ? "暂无" : value;
}

public sealed class ShowProgressTool : ITool
{
    private readonly IStudyProgressStore _store;
    public ShowProgressTool(IStudyProgressStore store) => _store = store;

    public string Name => "show_progress";
    public string Description => "查看学习进度，包括任务完成率、总学习时长、待办任务和最近完成记录。用户询问学习进度/完成情况时调用。";

    public JsonElement ParametersSchema { get; } = JsonSchema.Build("""
    {
      "type": "object",
      "properties": {}
    }
    """);

    public async Task<string> InvokeAsync(JsonElement args, CancellationToken ct = default)
    {
        var snapshot = await _store.GetAsync(ct);
        return StudyProgressFormatter.FormatProgress(snapshot);
    }
}

public sealed class ReviewHistoryTool : ITool
{
    private readonly IStudyProgressStore _store;
    public ReviewHistoryTool(IStudyProgressStore store) => _store = store;

    public string Name => "review_history";
    public string Description => "查看最近学习历史和复盘记录。用户要求回顾历史、查看今天/最近学了什么时调用。";

    public JsonElement ParametersSchema { get; } = JsonSchema.Build("""
    {
      "type": "object",
      "properties": {
        "limit": { "type": "integer", "description": "最多返回多少条，默认 10", "minimum": 1, "maximum": 50 }
      }
    }
    """);

    public async Task<string> InvokeAsync(JsonElement args, CancellationToken ct = default)
    {
        var limit = Math.Clamp(ReadInt(args, "limit") ?? 10, 1, 50);
        var snapshot = await _store.GetAsync(ct);
        return StudyProgressFormatter.FormatHistory(snapshot, limit);
    }

    private static int? ReadInt(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : null;
}

internal static class StudyProgressFormatter
{
    public static string FormatProgress(StudyProgressSnapshot snapshot)
    {
        var total = snapshot.Tasks.Count;
        var done = snapshot.Tasks.Count(t => t.Status.Equals("done", StringComparison.OrdinalIgnoreCase));
        var todo = snapshot.Tasks.Where(t => !t.Status.Equals("done", StringComparison.OrdinalIgnoreCase)).ToList();
        var minutes = snapshot.Sessions.Sum(s => s.Minutes);
        var rate = total == 0 ? 0 : done * 100.0 / total;

        var sb = new StringBuilder();
        sb.AppendLine($"学习进度：{done}/{total} 已完成（{rate:F0}%）");
        sb.AppendLine($"累计学习时长：{minutes} 分钟");
        sb.AppendLine($"最近完成：{LatestDone(snapshot)}");
        sb.AppendLine();
        sb.AppendLine("待办任务：");
        if (todo.Count == 0)
            sb.AppendLine("- 暂无待办任务");
        else
        {
            foreach (var task in todo.Take(10))
                sb.AppendLine($"- [{task.Id}] {task.Title} | 主题：{Empty(task.Topic)} | 预计 {task.Minutes} 分钟");
        }
        return sb.ToString().TrimEnd();
    }

    public static string FormatHistory(StudyProgressSnapshot snapshot, int limit)
    {
        var sessions = snapshot.Sessions.OrderByDescending(s => s.CreatedAt).Take(limit).ToList();
        if (sessions.Count == 0) return "暂无学习历史。";

        var sb = new StringBuilder();
        sb.AppendLine($"最近 {sessions.Count} 条学习历史：");
        foreach (var s in sessions)
        {
            sb.AppendLine($"- {s.CreatedAt:yyyy-MM-dd HH:mm} [{s.TaskId}] {s.Title} | {Empty(s.Topic)} | {s.Minutes} 分钟");
            if (!string.IsNullOrWhiteSpace(s.Reflection))
                sb.AppendLine($"  复盘：{s.Reflection}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string LatestDone(StudyProgressSnapshot snapshot)
    {
        var latest = snapshot.Sessions.OrderByDescending(s => s.CreatedAt).FirstOrDefault();
        return latest is null ? "暂无" : $"{latest.Title}（{latest.CreatedAt:yyyy-MM-dd HH:mm}）";
    }

    private static string Empty(string value) => string.IsNullOrWhiteSpace(value) ? "未指定" : value;
}
