using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartStudy.Core.Tools.Builtin;

public sealed class QuizAttempt
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    [JsonPropertyName("question")] public string Question { get; set; } = "";
    [JsonPropertyName("topic")] public string Topic { get; set; } = "";
    [JsonPropertyName("userAnswer")] public string UserAnswer { get; set; } = "";
    [JsonPropertyName("correctAnswer")] public string CorrectAnswer { get; set; } = "";
    [JsonPropertyName("isCorrect")] public bool IsCorrect { get; set; }
    [JsonPropertyName("explanation")] public string Explanation { get; set; } = "";
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public sealed class QuizQuestion
{
    [JsonPropertyName("number")] public int Number { get; set; }
    [JsonPropertyName("question")] public string Question { get; set; } = "";
    [JsonPropertyName("options")] public List<string> Options { get; set; } = new();
    [JsonPropertyName("answer")] public string Answer { get; set; } = "";
    [JsonPropertyName("explanation")] public string Explanation { get; set; } = "";
}

public sealed class QuizSession
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    [JsonPropertyName("questions")] public List<QuizQuestion> Questions { get; set; } = new();
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public interface IQuizSessionStore
{
    Task<QuizSession> SaveAsync(QuizSession session, CancellationToken ct = default);
    Task<QuizSession?> GetAsync(string quizId, CancellationToken ct = default);
    Task<QuizSession?> GetLatestAsync(CancellationToken ct = default);
}

public sealed class JsonQuizSessionStore : IQuizSessionStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public JsonQuizSessionStore(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
    }

    public async Task<QuizSession> SaveAsync(QuizSession session, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            all.Add(session);
            await WriteAllAsync(all, ct);
            return session;
        }
        finally { _lock.Release(); }
    }

    public async Task<QuizSession?> GetAsync(string quizId, CancellationToken ct = default)
    {
        var all = await ReadAllAsync(ct);
        if (string.IsNullOrWhiteSpace(quizId) || quizId.Equals("latest", StringComparison.OrdinalIgnoreCase))
            return all.OrderByDescending(x => x.CreatedAt).FirstOrDefault();

        return all.FirstOrDefault(x => x.Id.Equals(quizId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<QuizSession?> GetLatestAsync(CancellationToken ct = default)
    {
        var all = await ReadAllAsync(ct);
        return all.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
    }

    private async Task<List<QuizSession>> ReadAllAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return new List<QuizSession>();
        await using var fs = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<QuizSession>>(fs, Opts, ct) ?? new List<QuizSession>();
    }

    private async Task WriteAllAsync(List<QuizSession> sessions, CancellationToken ct)
    {
        await using var fs = File.Create(_path);
        await JsonSerializer.SerializeAsync(fs, sessions, Opts, ct);
    }
}

public interface IQuizResultStore
{
    Task<QuizAttempt> AddAsync(QuizAttempt attempt, CancellationToken ct = default);
    Task<IReadOnlyList<QuizAttempt>> ListAsync(bool mistakesOnly, string? topic, CancellationToken ct = default);
}

public sealed class JsonQuizResultStore : IQuizResultStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public JsonQuizResultStore(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
    }

    public async Task<QuizAttempt> AddAsync(QuizAttempt attempt, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            all.Add(attempt);
            await WriteAllAsync(all, ct);
            return attempt;
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<QuizAttempt>> ListAsync(bool mistakesOnly, string? topic, CancellationToken ct = default)
    {
        var all = await ReadAllAsync(ct);
        IEnumerable<QuizAttempt> query = all;
        if (mistakesOnly) query = query.Where(x => !x.IsCorrect);
        if (!string.IsNullOrWhiteSpace(topic))
            query = query.Where(x => x.Topic.Contains(topic, StringComparison.OrdinalIgnoreCase)
                                  || x.Question.Contains(topic, StringComparison.OrdinalIgnoreCase));
        return query.OrderByDescending(x => x.CreatedAt).ToList();
    }

    private async Task<List<QuizAttempt>> ReadAllAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return new List<QuizAttempt>();
        await using var fs = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<QuizAttempt>>(fs, Opts, ct) ?? new List<QuizAttempt>();
    }

    private async Task WriteAllAsync(List<QuizAttempt> attempts, CancellationToken ct)
    {
        await using var fs = File.Create(_path);
        await JsonSerializer.SerializeAsync(fs, attempts, Opts, ct);
    }
}

public sealed class RecordQuizResultTool : ITool
{
    private readonly IQuizResultStore _quizResults;
    private readonly ILearningProfileStore _profiles;

    public RecordQuizResultTool(IQuizResultStore quizResults, ILearningProfileStore profiles)
    {
        _quizResults = quizResults;
        _profiles = profiles;
    }

    public string Name => "record_quiz_result";

    public string Description =>
        "记录一次练习题答题结果，形成错题本。答错时会把关联主题自动写入学习画像的薄弱项。用户提交答案、说某题答错/答对或要求记录错题时调用。";

    public JsonElement ParametersSchema { get; } = JsonSchema.Build("""
    {
      "type": "object",
      "properties": {
        "question": { "type": "string", "description": "题目内容" },
        "topic": { "type": "string", "description": "关联知识点，例如 ReAct、RAG、MCP" },
        "userAnswer": { "type": "string", "description": "用户答案" },
        "correctAnswer": { "type": "string", "description": "标准答案" },
        "isCorrect": { "type": "boolean", "description": "用户是否答对" },
        "explanation": { "type": "string", "description": "解析或错因" }
      },
      "required": ["question", "userAnswer", "correctAnswer", "isCorrect"]
    }
    """);

    public async Task<string> InvokeAsync(JsonElement args, CancellationToken ct = default)
    {
        var question = ReadString(args, "question");
        var userAnswer = ReadString(args, "userAnswer");
        var correctAnswer = ReadString(args, "correctAnswer");
        if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(userAnswer) || string.IsNullOrWhiteSpace(correctAnswer))
            return "记录答题结果失败：必须提供 question、userAnswer、correctAnswer。";

        var attempt = await _quizResults.AddAsync(new QuizAttempt
        {
            Question = question,
            Topic = ReadString(args, "topic"),
            UserAnswer = userAnswer,
            CorrectAnswer = correctAnswer,
            IsCorrect = ReadBool(args, "isCorrect"),
            Explanation = ReadString(args, "explanation")
        }, ct);

        var profileHint = "";
        if (!attempt.IsCorrect && !string.IsNullOrWhiteSpace(attempt.Topic))
        {
            await _profiles.UpdateAsync(new LearningProfileUpdate
            {
                WeakTopics = new List<string> { attempt.Topic }
            }, ct);
            profileHint = $"\n已把 `{attempt.Topic}` 加入学习画像的薄弱知识点。";
        }

        return $"已记录答题结果 #{attempt.Id}：{(attempt.IsCorrect ? "答对" : "答错")}\n" +
               $"主题：{Empty(attempt.Topic)}\n" +
               $"你的答案：{attempt.UserAnswer}\n" +
               $"标准答案：{attempt.CorrectAnswer}\n" +
               $"解析：{Empty(attempt.Explanation)}" +
               profileHint;
    }

    private static string ReadString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";

    private static bool ReadBool(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;

    private static string Empty(string value) => string.IsNullOrWhiteSpace(value) ? "暂无" : value;
}

public sealed class SubmitQuizAnswerTool : ITool
{
    private readonly IQuizSessionStore _sessions;
    private readonly IQuizResultStore _quizResults;
    private readonly ILearningProfileStore _profiles;

    public SubmitQuizAnswerTool(IQuizSessionStore sessions, IQuizResultStore quizResults, ILearningProfileStore profiles)
    {
        _sessions = sessions;
        _quizResults = quizResults;
        _profiles = profiles;
    }

    public string Name => "submit_quiz_answer";

    public string Description =>
        "提交某道练习题的答案。工具会读取 make_quiz 保存的标准答案，判断是否正确，给出解析，并把错题写入错题本和学习画像。用户回答练习题时应调用。";

    public JsonElement ParametersSchema { get; } = JsonSchema.Build("""
    {
      "type": "object",
      "properties": {
        "quizId": { "type": "string", "description": "练习编号；可传 latest 表示最近一次练习" },
        "questionNumber": { "type": "integer", "description": "题号，从 1 开始", "minimum": 1 },
        "answer": { "type": "string", "description": "用户答案，例如 A、调用工具、工具返回结果" },
        "topic": { "type": "string", "description": "关联知识点，可选，例如 ReAct、RAG、MCP" }
      },
      "required": ["questionNumber", "answer"]
    }
    """);

    public async Task<string> InvokeAsync(JsonElement args, CancellationToken ct = default)
    {
        var quizId = ReadString(args, "quizId");
        var number = ReadInt(args, "questionNumber") ?? 0;
        var answer = ReadString(args, "answer");
        var topic = ReadString(args, "topic");
        if (number <= 0 || string.IsNullOrWhiteSpace(answer))
            return "判分失败：必须提供 questionNumber 和 answer。";

        var session = string.IsNullOrWhiteSpace(quizId)
            ? await _sessions.GetLatestAsync(ct)
            : await _sessions.GetAsync(quizId, ct);
        if (session is null)
            return "判分失败：未找到练习。请先调用 make_quiz 出题，或提供正确的 quizId。";

        var question = session.Questions.FirstOrDefault(q => q.Number == number);
        if (question is null)
            return $"判分失败：练习 #{session.Id} 中没有第 {number} 题。";

        var isCorrect = IsAnswerCorrect(answer, question.Answer, question.Options);
        await _quizResults.AddAsync(new QuizAttempt
        {
            Question = question.Question,
            Topic = topic,
            UserAnswer = answer,
            CorrectAnswer = question.Answer,
            IsCorrect = isCorrect,
            Explanation = question.Explanation
        }, ct);

        var profileHint = "";
        if (!isCorrect && !string.IsNullOrWhiteSpace(topic))
        {
            await _profiles.UpdateAsync(new LearningProfileUpdate
            {
                WeakTopics = new List<string> { topic }
            }, ct);
            profileHint = $"\n已把 `{topic}` 加入学习画像的薄弱知识点。";
        }

        return $"练习 #{session.Id} 第 {number} 题：{(isCorrect ? "答对" : "答错")}\n" +
               $"题目：{question.Question}\n" +
               $"你的答案：{answer}\n" +
               $"标准答案：{question.Answer}\n" +
               $"解析：{Empty(question.Explanation)}" +
               profileHint;
    }

    private static bool IsAnswerCorrect(string userAnswer, string correctAnswer, IReadOnlyList<string> options)
    {
        var normalizedUser = NormalizeAnswer(userAnswer);
        var normalizedCorrect = NormalizeAnswer(correctAnswer);
        if (normalizedUser == normalizedCorrect) return true;

        var optionIndex = ParseOptionIndex(userAnswer);
        if (optionIndex.HasValue && optionIndex.Value >= 0 && optionIndex.Value < options.Count)
            return NormalizeAnswer(options[optionIndex.Value]) == normalizedCorrect;

        return false;
    }

    private static int? ParseOptionIndex(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 1)
        {
            var ch = char.ToUpperInvariant(trimmed[0]);
            if (ch is >= 'A' and <= 'Z') return ch - 'A';
        }

        return int.TryParse(trimmed, out var n) ? n - 1 : null;
    }

    private static string NormalizeAnswer(string value) =>
        value.Trim().Trim('。', '.', '，', ',', ';', '；').ToLowerInvariant();

    private static string ReadString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";

    private static int? ReadInt(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : null;

    private static string Empty(string value) => string.IsNullOrWhiteSpace(value) ? "暂无" : value;
}

public sealed class ShowMistakesTool : ITool
{
    private readonly IQuizResultStore _store;
    public ShowMistakesTool(IQuizResultStore store) => _store = store;

    public string Name => "show_mistakes";

    public string Description =>
        "查看错题本或答题统计。用户要求查看错题、复盘练习、统计正确率或按主题查看薄弱题目时调用。";

    public JsonElement ParametersSchema { get; } = JsonSchema.Build("""
    {
      "type": "object",
      "properties": {
        "topic": { "type": "string", "description": "按知识点或题目关键词过滤，可选" },
        "includeCorrect": { "type": "boolean", "description": "是否包含答对记录，默认 false" },
        "limit": { "type": "integer", "description": "最多返回多少条，默认 10", "minimum": 1, "maximum": 50 }
      }
    }
    """);

    public async Task<string> InvokeAsync(JsonElement args, CancellationToken ct = default)
    {
        var topic = ReadString(args, "topic");
        var includeCorrect = ReadBool(args, "includeCorrect");
        var limit = Math.Clamp(ReadInt(args, "limit") ?? 10, 1, 50);
        var all = await _store.ListAsync(mistakesOnly: !includeCorrect, topic, ct);
        var shown = all.Take(limit).ToList();

        if (all.Count == 0)
            return includeCorrect ? "暂无答题记录。" : "暂无错题记录。";

        var total = await _store.ListAsync(mistakesOnly: false, topic, ct);
        var correct = total.Count(x => x.IsCorrect);
        var rate = total.Count == 0 ? 0 : correct * 100.0 / total.Count;

        var sb = new StringBuilder();
        sb.AppendLine($"答题统计：{correct}/{total.Count} 正确（{rate:F0}%）");
        sb.AppendLine(includeCorrect ? $"最近 {shown.Count} 条答题记录：" : $"最近 {shown.Count} 条错题：");
        foreach (var item in shown)
        {
            sb.AppendLine($"- [{item.Id}] {item.CreatedAt:yyyy-MM-dd HH:mm} {(item.IsCorrect ? "答对" : "答错")} | 主题：{Empty(item.Topic)}");
            sb.AppendLine($"  题目：{item.Question}");
            sb.AppendLine($"  你的答案：{item.UserAnswer}");
            sb.AppendLine($"  标准答案：{item.CorrectAnswer}");
            if (!string.IsNullOrWhiteSpace(item.Explanation))
                sb.AppendLine($"  解析：{item.Explanation}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string ReadString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";

    private static bool ReadBool(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;

    private static int? ReadInt(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : null;

    private static string Empty(string value) => string.IsNullOrWhiteSpace(value) ? "未指定" : value;
}
