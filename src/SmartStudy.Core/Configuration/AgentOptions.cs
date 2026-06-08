namespace SmartStudy.Core.Configuration;

public sealed class AgentOptions
{
    public LlmOptions Llm { get; set; } = new();
    public string ActiveLlmProfile { get; set; } = "";
    public Dictionary<string, LlmOptions> LlmProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public EmbeddingOptions Embedding { get; set; } = new();
    public RagOptions Rag { get; set; } = new();
    public int MaxLoopSteps { get; set; } = 8;
    public string SystemPrompt { get; set; } =
        "你是 SmartStudy —— 一个面向大学生的智能学习助手 Agent。" +
        "你拥有：knowledge_search（检索课程资料）、read_course_material（读取指定课件或文件的连续内容）、import_course_materials（读取并导入本地课程资料文件夹）、add_note / list_notes（管理学习笔记）、" +
        "update_learning_profile / show_learning_profile（管理长期学习画像）、study_plan（生成复习计划）、make_quiz（生成练习题）、calculate（数学计算）这些工具。\n" +
        "工作方式（ReAct）：每一步先在心里思考(Thought)再决定 Action：调用合适的工具或直接回答。" +
        "观察(Observation)工具返回结果后继续思考，直到任务完成。" +
        "原则：① 涉及学习资料的事实问题先 knowledge_search；② 用户要求 \"记下来\" 时调用 add_note；" +
        "③ 用户要基于资料/知识库/某个主题出题时，必须先调用 knowledge_search 或 read_course_material 取得真实材料，再把工具返回材料传给 make_quiz，禁止凭常识把 ReAct 误解为响应式编程；" +
        "④ 用户给出本地文件夹路径并要求阅读/导入资料时，调用 import_course_materials，而不是声称无法访问；" +
        "⑤ 用户表达薄弱点、已掌握内容、学习目标或偏好时，调用 update_learning_profile；其中“不懂/薄弱/需要加强/不会”写入 weakTopics，“已掌握/会/熟悉/擅长”写入 strongTopics，“目标/准备/想完成”写入 goals；用户要求查看个人学习情况时，调用 show_learning_profile；用户要求复习计划、备考计划、学习路线时，必须调用 study_plan，即使用户没有说明课程名也要基于已有学习画像生成计划；" +
        "⑥ 用户点名某个具体 PDF/PPT/课件/文件并要求讲解、概括或阅读其具体内容时，优先调用 read_course_material，必要时指定页码或提高 maxChars，不能只展示第一页后用“此处省略”糊弄；" +
        "⑦ 最终回答用简体中文，结构清晰。讲解文件时要按页码或章节展开，覆盖主题、页面结构、核心知识点、课程要求/任务、可用于复习或项目的要点；" +
        "如果资料页数不多，应逐页说明每页讲了什么；如果内容过长，只讲已读取部分并明确建议继续读取后续页，禁止使用“此处省略了某页内容”这种占位回答。" +
        "对工具返回内容中的学校名、作者、日期、页码、数字、教材名等事实必须忠实原文，不能凭常识改写；例如原文是 Tongji University/同济大学，就不能改成其他学校。" +
        "当页面中有 Discussion、Exercise、Requirements 等信息时，要说明其教学目的或对课程项目的意义，不要说“具体内容没有显示”。" +
        "不要在最终回答中显式输出 Thought、Action、Observation 标签；需要行动时使用工具调用。";
}

public sealed class LlmOptions
{
    public string BaseUrl { get; set; } = "https://open.bigmodel.cn/api/coding/paas/v4";
    public string ApiKey { get; set; } = "";
    public string ApiKeyEnvironmentVariable { get; set; } = "";
    public string Model { get; set; } = "glm-4-flash";
    public double Temperature { get; set; } = 0.3;
    public int MaxTokens { get; set; } = 4096;

    public LlmOptions Clone() => new()
    {
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        ApiKeyEnvironmentVariable = ApiKeyEnvironmentVariable,
        Model = Model,
        Temperature = Temperature,
        MaxTokens = MaxTokens
    };
}

public sealed class EmbeddingOptions
{
    public string Provider { get; set; } = "zhipu";
    public string BaseUrl { get; set; } = "https://open.bigmodel.cn/api/paas/v4";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "embedding-3";
    public int LocalDimensions { get; set; } = 512;
}

public sealed class RagOptions
{
    public string KnowledgeDirectory { get; set; } = "knowledge";
    public string IndexFile { get; set; } = "data/index.json";
    public int ChunkSize { get; set; } = 600;
    public int ChunkOverlap { get; set; } = 80;
    public int TopK { get; set; } = 4;
}

public sealed class LlmProfileManager
{
    private readonly AgentOptions _options;
    private readonly Dictionary<string, LlmOptions> _profiles;
    private LlmOptions _current;
    private string _currentName;

    public LlmProfileManager(Microsoft.Extensions.Options.IOptions<AgentOptions> options)
    {
        _options = options.Value;
        _profiles = new Dictionary<string, LlmOptions>(_options.LlmProfiles, StringComparer.OrdinalIgnoreCase);
        if (!_profiles.ContainsKey("default")) _profiles["default"] = _options.Llm.Clone();

        var initial = string.IsNullOrWhiteSpace(_options.ActiveLlmProfile) ? "default" : _options.ActiveLlmProfile;
        if (!_profiles.TryGetValue(initial, out var selected))
        {
            initial = "default";
            selected = _options.Llm;
        }

        _currentName = initial;
        _current = Resolve(selected);
    }

    public string CurrentName => _currentName;
    public LlmOptions Current => _current.Clone();
    public IReadOnlyDictionary<string, LlmOptions> Profiles => _profiles;

    public bool TrySwitch(string profileName, out string message)
    {
        if (!_profiles.TryGetValue(profileName, out var selected))
        {
            message = $"找不到 LLM profile：{profileName}";
            return false;
        }

        _currentName = profileName;
        _current = Resolve(selected);
        message = $"已切换到 {profileName} ({_current.Model})";
        return true;
    }

    private LlmOptions Resolve(LlmOptions source)
    {
        var resolved = source.Clone();
        if (!string.IsNullOrWhiteSpace(resolved.ApiKeyEnvironmentVariable))
        {
            var env = Environment.GetEnvironmentVariable(resolved.ApiKeyEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(env)) resolved.ApiKey = env;
        }

        if (string.IsNullOrWhiteSpace(resolved.ApiKey) &&
            Uri.TryCreate(resolved.BaseUrl, UriKind.Absolute, out var profileUri) &&
            Uri.TryCreate(_options.Llm.BaseUrl, UriKind.Absolute, out var defaultUri) &&
            string.Equals(profileUri.Host, defaultUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            resolved.ApiKey = _options.Llm.ApiKey;
        }

        return resolved;
    }
}
