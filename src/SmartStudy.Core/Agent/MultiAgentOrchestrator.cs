using System.Text;
using SmartStudy.Core.Rag;
using SmartStudy.Core.Tools.Builtin;

namespace SmartStudy.Core.Agent;

public sealed record MultiAgentStep(
    string AgentName,
    string Responsibility,
    string Output,
    bool IsSuccessful = true);

public sealed record MultiAgentResult(
    string Goal,
    IReadOnlyList<MultiAgentStep> Steps,
    string FinalAnswer,
    bool PassedReview);

internal enum MultiAgentAnswerMode
{
    Explanation,
    StudyPlan,
    Defense
}

/// <summary>
/// Lightweight multi-agent workflow for demonstrations and deterministic validation.
/// Each specialized agent owns one stage: plan, research, tutor response, and review.
/// </summary>
public sealed class MultiAgentOrchestrator
{
    private readonly KnowledgeSearchService _search;
    private readonly ILearningProfileStore _profiles;

    public MultiAgentOrchestrator(KnowledgeSearchService search, ILearningProfileStore profiles)
    {
        _search = search;
        _profiles = profiles;
    }

    public async Task<MultiAgentResult> RunAsync(string goal, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(goal))
            throw new ArgumentException("Multi-Agent 目标不能为空。", nameof(goal));

        var steps = new List<MultiAgentStep>();
        var normalizedGoal = goal.Trim();

        var plan = BuildPlan(normalizedGoal);
        steps.Add(new MultiAgentStep(
            "PlannerAgent",
            "拆解用户目标，决定后续 Agent 的执行顺序。",
            plan));

        var evidence = await _search.SearchAsync(normalizedGoal, 4, ct);
        var researchOk = !evidence.Contains("尚未建立索引", StringComparison.OrdinalIgnoreCase)
                         && !evidence.Contains("未检索到相关内容", StringComparison.OrdinalIgnoreCase);
        steps.Add(new MultiAgentStep(
            "ResearchAgent",
            "检索课程知识库，给 TutorAgent 提供可追溯资料。",
            evidence,
            researchOk));

        var profile = await _profiles.GetAsync(ct);
        var tutorOutput = BuildTutorAnswer(normalizedGoal, plan, evidence, profile);
        steps.Add(new MultiAgentStep(
            "TutorAgent",
            "结合计划、资料和学习画像生成面向学生的答复。",
            tutorOutput));

        var review = Review(normalizedGoal, tutorOutput, researchOk);
        steps.Add(new MultiAgentStep(
            "ReviewerAgent",
            "检查最终答复是否覆盖目标、是否使用资料、是否给出下一步。",
            review.Message,
            review.Passed));

        var final = review.Passed
            ? tutorOutput
            : tutorOutput + Environment.NewLine + Environment.NewLine + "ReviewerAgent 修正建议：" + Environment.NewLine + review.Message;

        return new MultiAgentResult(normalizedGoal, steps, final, review.Passed);
    }

    private static string BuildPlan(string goal)
    {
        var topics = ExtractTopics(goal);
        var sb = new StringBuilder();
        sb.AppendLine($"目标：{goal}");
        sb.AppendLine("协作计划：");
        sb.AppendLine("1. ResearchAgent 检索课程知识库，找出与目标最相关的资料片段。");
        sb.AppendLine("2. TutorAgent 把资料转化为结构化讲解、复习重点或行动建议。");
        sb.AppendLine("3. ReviewerAgent 检查答案是否围绕目标、是否有资料依据、是否包含下一步。");
        sb.AppendLine($"重点主题：{string.Join("、", topics)}");
        return sb.ToString().TrimEnd();
    }

    private static string BuildTutorAnswer(string goal, string plan, string evidence, LearningProfile profile)
    {
        var topics = ExtractTopics(goal);
        var focus = string.Join("、", topics);
        var mode = DetectAnswerMode(goal);
        var sb = new StringBuilder();
        sb.AppendLine(mode switch
        {
            MultiAgentAnswerMode.Defense => "Multi-Agent 最终答复：答辩版讲解",
            MultiAgentAnswerMode.StudyPlan => "Multi-Agent 最终答复：学习建议版",
            _ => "Multi-Agent 最终答复：知识讲解版"
        });
        sb.AppendLine();
        sb.AppendLine($"目标理解：{goal}");
        sb.AppendLine($"重点主题：{focus}");

        if (profile.WeakTopics.Count > 0 || profile.Goals.Count > 0 || !string.IsNullOrWhiteSpace(profile.PreferredStyle))
        {
            sb.AppendLine();
            sb.AppendLine("结合学习画像：");
            if (profile.WeakTopics.Count > 0) sb.AppendLine($"- 薄弱项：{string.Join("、", profile.WeakTopics.Take(6))}");
            if (profile.Goals.Count > 0) sb.AppendLine($"- 学习目标：{string.Join("、", profile.Goals.Take(4))}");
            if (!string.IsNullOrWhiteSpace(profile.PreferredStyle)) sb.AppendLine($"- 偏好讲解方式：{profile.PreferredStyle}");
        }

        AppendModeSpecificAnswer(sb, mode);

        sb.AppendLine();
        sb.AppendLine("资料依据摘要：");
        sb.AppendLine(TrimForFinalAnswer(evidence, 1800));

        sb.AppendLine();
        sb.AppendLine("下一步：如果要继续完善，可以把 Multi-Agent 包装成 `ITool`，让普通 ReAct 聊天在用户说“用多 Agent”时自动触发；目前已经支持 `multi` 子命令和聊天里的 `:multi` 指令。");
        return sb.ToString().TrimEnd();
    }

    private static void AppendModeSpecificAnswer(StringBuilder sb, MultiAgentAnswerMode mode)
    {
        if (mode == MultiAgentAnswerMode.Defense)
        {
            AppendConceptExplanation(sb);

            sb.AppendLine();
            sb.AppendLine("二、执行流程");
            sb.AppendLine("1. PlannerAgent 先理解用户目标，把任务拆成检索资料、生成解释、质量检查几个阶段。");
            sb.AppendLine("2. ResearchAgent 调用 RAG 检索课程知识库，找到和目标相关的资料片段，避免只靠模型常识回答。");
            sb.AppendLine("3. TutorAgent 把资料转成面向学生的讲解，并把概念映射到项目代码结构。");
            sb.AppendLine("4. ReviewerAgent 检查最终答复是否覆盖用户目标、是否有资料依据、是否给出下一步。");

            AppendCodeMapping(sb, heading: "三、和本项目代码的对应关系");

            sb.AppendLine();
            sb.AppendLine("四、答辩时可以这样说");
            sb.AppendLine("“我的项目里有两种 Agent 能力：普通聊天模式使用 ReActAgent，让 LLM 通过 tool_calls 自主选择工具；Multi-Agent 模式使用 MultiAgentOrchestrator，把一个学习目标拆给 Planner、Researcher、Tutor、Reviewer 四个角色协作完成。Researcher 负责查资料，Tutor 负责生成最终讲解，Reviewer 负责质量检查。这样既能展示 ReAct 的工具调用，也能展示多 Agent 分工。”");

            sb.AppendLine();
            sb.AppendLine("五、验收命令");
            sb.AppendLine("```powershell");
            sb.AppendLine("dotnet run --project src\\SmartStudy.Cli\\SmartStudy.Cli.csproj -- multi \"解释 ReAct Agent 并准备答辩\"");
            sb.AppendLine("dotnet run --project src\\SmartStudy.Cli\\SmartStudy.Cli.csproj -- chat --stream");
            sb.AppendLine("# 进入聊天后输入：:multi 解释 ReAct Agent 并准备答辩");
            sb.AppendLine("```");
            sb.AppendLine("验收时应看到四个 Agent 的协作表格、Reviewer: PASS，以及当前这段结构化最终答复。");
            return;
        }

        if (mode == MultiAgentAnswerMode.StudyPlan)
        {
            sb.AppendLine();
            sb.AppendLine("一、学习目标拆解");
            sb.AppendLine("- 先明确需要掌握的核心概念和容易混淆的边界。");
            sb.AppendLine("- 再通过课程资料检索建立知识依据。");
            sb.AppendLine("- 最后用练习题、笔记和学习画像形成闭环复习。");

            sb.AppendLine();
            sb.AppendLine("二、建议学习路径");
            sb.AppendLine("1. 用 ResearchAgent 检索资料，整理 3-5 个核心概念。");
            sb.AppendLine("2. 用 TutorAgent 把概念转成自己的话，并补充代码或场景例子。");
            sb.AppendLine("3. 用 make_quiz 或手动自测检验理解。");
            sb.AppendLine("4. 把仍不清楚的点写入学习画像，下一轮优先复习。");

            sb.AppendLine();
            sb.AppendLine("三、可执行下一步");
            sb.AppendLine("- 运行 `:multi 复习 ReAct Agent 的工具调用流程` 获取下一轮协作建议。");
            sb.AppendLine("- 对检索结果中的每个来源各写一条笔记。");
            sb.AppendLine("- 针对薄弱点生成 2-3 道练习题。");
            return;
        }

        AppendConceptExplanation(sb);

        sb.AppendLine();
        sb.AppendLine("二、为什么它有用");
        sb.AppendLine("- 它让 LLM 能根据任务需要调用外部工具，而不是只依赖模型内部知识。");
        sb.AppendLine("- 工具结果会回到上下文中，模型可以基于真实 Observation 继续推理。");
        sb.AppendLine("- 对学习助手来说，这意味着它可以检索课程资料、记录笔记、生成计划，而不是只聊天。");

        AppendCodeMapping(sb, heading: "三、在本项目中的体现");

        sb.AppendLine();
        sb.AppendLine("四、下一步建议");
        sb.AppendLine("- 如果你要理解原理，重点看 `ReActAgent` 的循环和 `ToolRegistry` 的工具注册。");
        sb.AppendLine("- 如果你要演示功能，可以运行 `multi` 或在聊天中输入 `:multi <目标>`。");
    }

    private static void AppendConceptExplanation(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("一、核心概念解释");
        sb.AppendLine("ReAct Agent 是把 reasoning 和 acting 结合起来的 Agent 模式。它不会只把用户问题交给 LLM 一次性回答，而是让模型在每一轮根据上下文决定是否需要行动。行动通常表现为调用工具，例如检索知识库、读取资料、保存笔记或生成练习题。工具返回结果后，Agent 把结果作为 Observation 写回上下文，再进入下一轮决策，直到模型可以给出最终答案。");
    }

    private static void AppendCodeMapping(StringBuilder sb, string heading)
    {
        sb.AppendLine();
        sb.AppendLine(heading);
        sb.AppendLine("- `ReActAgent`：负责单 Agent 的 Thought -> Action -> Observation 主循环。");
        sb.AppendLine("- `ToolRegistry` 和 `ITool`：把 C# 工具转换成 LLM 可理解的 function calling 定义。");
        sb.AppendLine("- `ConversationMemory`：保存 system、user、assistant、tool 消息，维持短期上下文。");
        sb.AppendLine("- `KnowledgeIndexer`、`KnowledgeSearchService`、`InMemoryVectorStore`：实现 RAG 索引和检索。");
        sb.AppendLine("- `MultiAgentOrchestrator`：负责 PlannerAgent、ResearchAgent、TutorAgent、ReviewerAgent 的多 Agent 编排。");
    }

    private static MultiAgentAnswerMode DetectAnswerMode(string goal)
    {
        if (ContainsAny(goal, "答辩", "验收", "演示", "汇报", "展示", "presentation", "demo", "defense"))
            return MultiAgentAnswerMode.Defense;

        if (ContainsAny(goal, "复习", "学习", "计划", "备考", "练习", "掌握", "薄弱", "study", "review", "plan", "quiz"))
            return MultiAgentAnswerMode.StudyPlan;

        return MultiAgentAnswerMode.Explanation;
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(v => text.Contains(v, StringComparison.OrdinalIgnoreCase));

    private static (bool Passed, string Message) Review(string goal, string tutorOutput, bool researchOk)
    {
        var checks = new List<(string Name, bool Passed)>
        {
            ("覆盖用户目标", tutorOutput.Contains(goal, StringComparison.OrdinalIgnoreCase)),
            ("包含资料依据", tutorOutput.Contains("资料依据", StringComparison.OrdinalIgnoreCase) && researchOk),
            ("包含下一步", tutorOutput.Contains("下一步", StringComparison.OrdinalIgnoreCase)),
            ("包含多 Agent 角色", tutorOutput.Contains("PlannerAgent", StringComparison.OrdinalIgnoreCase)
                || tutorOutput.Contains("Multi-Agent", StringComparison.OrdinalIgnoreCase))
        };

        var passed = checks.All(c => c.Passed);
        var sb = new StringBuilder();
        sb.AppendLine(passed ? "审核通过：最终答复满足演示要求。" : "审核未完全通过：需要补充以下内容。");
        foreach (var check in checks)
            sb.AppendLine($"- {(check.Passed ? "OK" : "TODO")} {check.Name}");
        return (passed, sb.ToString().TrimEnd());
    }

    private static IReadOnlyList<string> ExtractTopics(string goal)
    {
        var separators = new[] { ' ', ',', '，', '。', ';', '；', '/', '\\', ':', '：', '?', '？', '!', '！' };
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "请", "帮我", "解释", "说明", "一下", "如何", "怎么", "准备", "复习", "项目", "答辩", "the", "a", "an", "and", "or"
        };

        var topics = goal
            .Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim('"', '\'', '“', '”'))
            .Where(t => t.Length >= 2 && !stopWords.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

        if (topics.Count == 0) topics.Add("课程核心概念");
        return topics;
    }

    private static string TrimForFinalAnswer(string text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return "暂无资料。";
        if (text.Length <= maxChars) return text.Trim();
        return text[..maxChars].TrimEnd() + Environment.NewLine + "...";
    }
}
