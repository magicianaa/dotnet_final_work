using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartStudy.Core.Llm;
using SmartStudy.Core.Memory;
using SmartStudy.Core.Rag;

namespace SmartStudy.Core.Tools.Builtin;

public sealed class LearningProfile
{
    [JsonPropertyName("weakTopics")] public List<string> WeakTopics { get; set; } = new();
    [JsonPropertyName("strongTopics")] public List<string> StrongTopics { get; set; } = new();
    [JsonPropertyName("goals")] public List<string> Goals { get; set; } = new();
    [JsonPropertyName("preferredStyle")] public string PreferredStyle { get; set; } = "";
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public sealed class LearningProfileUpdate
{
    public List<string> WeakTopics { get; set; } = new();
    public List<string> StrongTopics { get; set; } = new();
    public List<string> Goals { get; set; } = new();
    public string PreferredStyle { get; set; } = "";
    public bool Replace { get; set; }
}

public interface ILearningProfileStore
{
    Task<LearningProfile> GetAsync(CancellationToken ct = default);
    Task<LearningProfile> UpdateAsync(LearningProfileUpdate update, CancellationToken ct = default);
}

public sealed class JsonLearningProfileStore : ILearningProfileStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public JsonLearningProfileStore(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public async Task<LearningProfile> GetAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return new LearningProfile();

        await using var fs = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<LearningProfile>(fs, Opts, ct) ?? new LearningProfile();
    }

    public async Task<LearningProfile> UpdateAsync(LearningProfileUpdate update, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var profile = update.Replace ? new LearningProfile() : await GetAsync(ct);
            Merge(profile.WeakTopics, update.WeakTopics);
            Merge(profile.StrongTopics, update.StrongTopics);
            Merge(profile.Goals, update.Goals);
            if (!string.IsNullOrWhiteSpace(update.PreferredStyle))
                profile.PreferredStyle = update.PreferredStyle.Trim();

            profile.UpdatedAt = DateTime.Now;
            await using var fs = File.Create(_path);
            await JsonSerializer.SerializeAsync(fs, profile, Opts, ct);
            return profile;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static void Merge(List<string> target, IEnumerable<string> values)
    {
        foreach (var value in values.Select(Normalize).Where(s => s.Length > 0))
        {
            if (!target.Contains(value, StringComparer.OrdinalIgnoreCase))
                target.Add(value);
        }
    }

    private static string Normalize(string value) => value.Trim().Trim('，', ',', '。', '.', ';', '；');
}

public sealed class UpdateLearningProfileTool : ITool
{
    private readonly ILearningProfileStore _store;
    private readonly IConversationMemory? _memory;

    public UpdateLearningProfileTool(ILearningProfileStore store, IConversationMemory? memory = null)
    {
        _store = store;
        _memory = memory;
    }

    public string Name => "update_learning_profile";

    public string Description =>
        "更新学生长期学习画像，包括薄弱知识点、优势知识点、学习目标和偏好的讲解风格。用户表达自己哪里不会/薄弱/需要加强时写入 weakTopics；表达已经掌握/会/熟悉/擅长时写入 strongTopics；表达想准备考试/项目/答辩时写入 goals。";

    public JsonElement ParametersSchema { get; } = JsonSchema.Build("""
    {
      "type": "object",
      "properties": {
        "weakTopics": {
          "type": "array",
          "items": { "type": "string" },
          "description": "学生薄弱或需要加强的知识点"
        },
        "strongTopics": {
          "type": "array",
          "items": { "type": "string" },
          "description": "学生已经掌握较好的知识点"
        },
        "goals": {
          "type": "array",
          "items": { "type": "string" },
          "description": "学习目标，例如准备期末考试、完成课程项目、复习某章节"
        },
        "preferredStyle": {
          "type": "string",
          "description": "偏好的讲解方式，例如先讲概念再举例、按页讲解、给出项目化建议"
        },
        "replace": {
          "type": "boolean",
          "description": "是否覆盖旧画像。默认 false 表示合并更新。"
        }
      }
    }
    """);

    public async Task<string> InvokeAsync(JsonElement args, CancellationToken ct = default)
    {
        var update = new LearningProfileUpdate
        {
            WeakTopics = ReadStringArray(args, "weakTopics"),
            StrongTopics = ReadStringArray(args, "strongTopics"),
            Goals = ReadStringArray(args, "goals"),
            PreferredStyle = ReadString(args, "preferredStyle"),
            Replace = ReadBool(args, "replace")
        };
        EnrichFromLatestUserMessage(update);

        var profile = await _store.UpdateAsync(update, ct);
        return "学习画像已更新。\n" + LearningProfileFormatter.Format(profile);
    }

    private void EnrichFromLatestUserMessage(LearningProfileUpdate update)
    {
        var text = _memory?.Messages.LastOrDefault(m => m.Role == ChatRoles.User)?.Content;
        if (string.IsNullOrWhiteSpace(text)) return;

        AddMissing(update.StrongTopics, ExtractAfterAny(text, "已经掌握", "已掌握", "掌握了", "熟悉", "擅长"));
        AddMissing(update.WeakTopics, ExtractAfterAny(text, "需要加强", "仍需要加强", "还需要加强", "不太懂", "不会"));

        var goal = ExtractSingleAfterAny(text, "目标是", "目标：", "目标为");
        if (!string.IsNullOrWhiteSpace(goal)) AddMissing(update.Goals, new[] { goal });

        var style = ExtractSingleAfterAny(text, "偏好", "喜欢");
        if (string.IsNullOrWhiteSpace(update.PreferredStyle) && !string.IsNullOrWhiteSpace(style))
            update.PreferredStyle = style;
    }

    private static void AddMissing(List<string> target, IEnumerable<string> values)
    {
        foreach (var value in values.Select(CleanTopic).Where(s => s.Length > 0))
        {
            if (!target.Contains(value, StringComparer.OrdinalIgnoreCase))
                target.Add(value);
        }
    }

    private static IEnumerable<string> ExtractAfterAny(string text, params string[] markers)
    {
        foreach (var marker in markers)
        {
            var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;

            var segment = text[(index + marker.Length)..];
            segment = CutAtFirst(segment, "，", "。", "；", ";", "\n", "但", "但是", "仍", "还", "需要", "请", "目标", "偏好");
            foreach (var topic in SplitTopics(segment))
                yield return topic;
        }
    }

    private static string ExtractSingleAfterAny(string text, params string[] markers)
    {
        foreach (var marker in markers)
        {
            var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;

            var segment = text[(index + marker.Length)..];
            return CleanTopic(CutAtFirst(segment, "，", "。", "；", ";", "\n", "请"));
        }

        return "";
    }

    private static IEnumerable<string> SplitTopics(string text) =>
        text.Split(new[] { "和", "、", ",", "，", "/", " " }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string CutAtFirst(string text, params string[] stops)
    {
        var end = text.Length;
        foreach (var stop in stops)
        {
            var idx = text.IndexOf(stop, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && idx < end) end = idx;
        }

        return text[..end];
    }

    private static string CleanTopic(string value) =>
        value.Trim().Trim('：', ':', '，', ',', '。', '.', ';', '；', '"', '\'', '“', '”');

    private static List<string> ReadStringArray(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array) return new();
        return el.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString() ?? "")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    private static string ReadString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? ""
            : "";

    private static bool ReadBool(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;
}

public sealed class ShowLearningProfileTool : ITool
{
    private readonly ILearningProfileStore _store;
    public ShowLearningProfileTool(ILearningProfileStore store) => _store = store;

    public string Name => "show_learning_profile";
    public string Description => "查看当前学生长期学习画像，包括薄弱项、优势项、学习目标和偏好的讲解风格。";

    public JsonElement ParametersSchema { get; } = JsonSchema.Build("""
    {
      "type": "object",
      "properties": {}
    }
    """);

    public async Task<string> InvokeAsync(JsonElement args, CancellationToken ct = default)
    {
        var profile = await _store.GetAsync(ct);
        return LearningProfileFormatter.Format(profile);
    }
}

public sealed class StudyPlanTool : ITool
{
    private readonly ILearningProfileStore _profiles;
    private readonly KnowledgeSearchService _search;

    public StudyPlanTool(ILearningProfileStore profiles, KnowledgeSearchService search)
    {
        _profiles = profiles;
        _search = search;
    }

    public string Name => "study_plan";

    public string Description =>
        "根据学习画像、目标、薄弱知识点和课程资料生成短期复习计划。用户要求制定复习计划、学习路线、备考安排或项目推进计划时必须调用；即使用户没有说明课程名，也应基于已有学习画像和默认参数生成计划。";

    public JsonElement ParametersSchema { get; } = JsonSchema.Build("""
    {
      "type": "object",
      "properties": {
        "goal": {
          "type": "string",
          "description": "本次复习或学习计划目标"
        },
        "days": {
          "type": "integer",
          "description": "计划天数，默认 3，范围 1-30",
          "minimum": 1,
          "maximum": 30
        },
        "minutesPerDay": {
          "type": "integer",
          "description": "每天可投入分钟数，默认 60，范围 15-240",
          "minimum": 15,
          "maximum": 240
        },
        "focusTopics": {
          "type": "array",
          "items": { "type": "string" },
          "description": "用户指定的重点知识点，可选"
        }
      }
    }
    """);

    public async Task<string> InvokeAsync(JsonElement args, CancellationToken ct = default)
    {
        var goal = ReadString(args, "goal");
        var days = Math.Clamp(ReadInt(args, "days") ?? 3, 1, 30);
        var minutesPerDay = Math.Clamp(ReadInt(args, "minutesPerDay") ?? 60, 15, 240);
        var focusTopics = ReadStringArray(args, "focusTopics");
        var profile = await _profiles.GetAsync(ct);

        var topics = focusTopics
            .Concat(profile.WeakTopics)
            .Concat(profile.Goals.Where(_ => string.IsNullOrWhiteSpace(goal)))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (topics.Count == 0)
            topics.Add(string.IsNullOrWhiteSpace(goal) ? "课程核心概念" : goal);

        var query = string.Join(" ", topics.Take(5));
        var evidence = await _search.SearchAsync(query, 3, ct);

        var sb = new StringBuilder();
        sb.AppendLine($"复习目标：{(string.IsNullOrWhiteSpace(goal) ? "巩固课程重点并查漏补缺" : goal)}");
        sb.AppendLine($"周期：{days} 天，每天约 {minutesPerDay} 分钟");
        sb.AppendLine($"重点主题：{string.Join("、", topics.Take(8))}");
        if (!string.IsNullOrWhiteSpace(profile.PreferredStyle))
            sb.AppendLine($"偏好讲解方式：{profile.PreferredStyle}");

        if (profile.WeakTopics.Count > 0 || profile.StrongTopics.Count > 0)
            sb.AppendLine($"画像摘要：薄弱项 {JoinOrDefault(profile.WeakTopics)}；优势项 {JoinOrDefault(profile.StrongTopics)}");

        sb.AppendLine();
        sb.AppendLine("每日安排：");
        for (var day = 1; day <= days; day++)
        {
            var topic = topics[(day - 1) % topics.Count];
            var reading = Math.Max(10, minutesPerDay / 2);
            var practice = Math.Max(5, minutesPerDay / 4);
            var review = Math.Max(5, minutesPerDay - reading - practice);

            sb.AppendLine($"第 {day} 天：{topic}");
            sb.AppendLine($"- {reading} 分钟：用 knowledge_search / read_course_material 回顾相关课程资料，整理 3 个核心概念。");
            sb.AppendLine($"- {practice} 分钟：让 make_quiz 生成 2-3 道题，记录错题原因。");
            sb.AppendLine($"- {review} 分钟：用 add_note 保存总结，并把仍不清楚的点更新到学习画像。");
        }

        sb.AppendLine();
        sb.AppendLine("资料线索：");
        sb.AppendLine(evidence);
        return sb.ToString();
    }

    private static List<string> ReadStringArray(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array) return new();
        return el.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString() ?? "")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    private static string ReadString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? ""
            : "";

    private static int? ReadInt(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetInt32()
            : null;

    private static string JoinOrDefault(IReadOnlyCollection<string> values) =>
        values.Count == 0 ? "暂无" : string.Join("、", values);
}

internal static class LearningProfileFormatter
{
    public static string Format(LearningProfile profile)
    {
        return new StringBuilder()
            .AppendLine($"薄弱知识点：{JoinOrDefault(profile.WeakTopics)}")
            .AppendLine($"优势知识点：{JoinOrDefault(profile.StrongTopics)}")
            .AppendLine($"学习目标：{JoinOrDefault(profile.Goals)}")
            .AppendLine($"偏好讲解方式：{(string.IsNullOrWhiteSpace(profile.PreferredStyle) ? "暂无" : profile.PreferredStyle)}")
            .AppendLine($"更新时间：{profile.UpdatedAt:yyyy-MM-dd HH:mm}")
            .ToString()
            .TrimEnd();
    }

    private static string JoinOrDefault(IReadOnlyCollection<string> values) =>
        values.Count == 0 ? "暂无" : string.Join("、", values);
}
