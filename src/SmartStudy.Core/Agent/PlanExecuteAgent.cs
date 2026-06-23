using System.Text;
using SmartStudy.Core.Rag;
using SmartStudy.Core.Tools.Builtin;

namespace SmartStudy.Core.Agent;

public sealed record PlanExecuteStep(string Name, string Output, bool IsSuccessful = true);

public sealed record PlanExecuteResult(
    string Goal,
    IReadOnlyList<PlanExecuteStep> Steps,
    AnswerQualityReview Review,
    string FinalAnswer);

/// <summary>Simple plan-and-execute workflow: plan, retrieve evidence, compose, review.</summary>
public sealed class PlanExecuteAgent
{
    private readonly KnowledgeSearchService _search;
    private readonly ILearningProfileStore _profiles;
    private readonly AnswerQualityReviewer _reviewer;

    public PlanExecuteAgent(
        KnowledgeSearchService search,
        ILearningProfileStore profiles,
        AnswerQualityReviewer reviewer)
    {
        _search = search;
        _profiles = profiles;
        _reviewer = reviewer;
    }

    public async Task<PlanExecuteResult> RunAsync(string goal, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(goal))
            throw new ArgumentException("Plan-and-Execute 目标不能为空。", nameof(goal));

        var normalizedGoal = goal.Trim();
        var steps = new List<PlanExecuteStep>();

        var plan = BuildPlan(normalizedGoal);
        steps.Add(new PlanExecuteStep("Plan", plan));

        var evidence = await SearchSafelyAsync(normalizedGoal, ct);
        var evidenceOk = IsEvidenceUsable(evidence);
        steps.Add(new PlanExecuteStep("Execute: RAG 检索", evidence, evidenceOk));

        var profile = await _profiles.GetAsync(ct);
        var answer = BuildAnswer(normalizedGoal, plan, evidence, profile);
        steps.Add(new PlanExecuteStep("Execute: 生成答复", answer));

        var review = _reviewer.Review(normalizedGoal, answer, evidenceExpected: evidenceOk);
        steps.Add(new PlanExecuteStep("Review: 答案质量检查", review.Summary, review.Passed));

        var final = review.Passed
            ? answer
            : answer + Environment.NewLine + Environment.NewLine + "质量检查修正建议：" + Environment.NewLine + review.Summary;

        return new PlanExecuteResult(normalizedGoal, steps, review, final);
    }

    private static string BuildPlan(string goal)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"目标：{goal}");
        sb.AppendLine("执行计划：");
        sb.AppendLine("1. 明确目标关键词和预期输出。");
        sb.AppendLine("2. 检索课程知识库，收集可追溯资料依据。");
        sb.AppendLine("3. 根据学习画像组织答复。");
        sb.AppendLine("4. 使用 Reviewer 检查覆盖目标、资料依据、下一步和占位文本。");
        return sb.ToString().TrimEnd();
    }

    private static string BuildAnswer(string goal, string plan, string evidence, LearningProfile profile)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Plan-and-Execute 最终答复");
        sb.AppendLine();
        sb.AppendLine($"目标：{goal}");
        sb.AppendLine();
        sb.AppendLine("计划摘要：");
        sb.AppendLine(plan);

        if (profile.WeakTopics.Count > 0 || profile.Goals.Count > 0 || !string.IsNullOrWhiteSpace(profile.PreferredStyle))
        {
            sb.AppendLine();
            sb.AppendLine("学习画像上下文：");
            if (profile.WeakTopics.Count > 0) sb.AppendLine($"- 薄弱项：{string.Join("、", profile.WeakTopics.Take(6))}");
            if (profile.Goals.Count > 0) sb.AppendLine($"- 学习目标：{string.Join("、", profile.Goals.Take(4))}");
            if (!string.IsNullOrWhiteSpace(profile.PreferredStyle)) sb.AppendLine($"- 讲解偏好：{profile.PreferredStyle}");
        }

        sb.AppendLine();
        sb.AppendLine("执行结果：");
        sb.AppendLine("- 已将任务拆成计划、检索、生成、审查四步。");
        sb.AppendLine("- 已优先使用课程知识库作为资料依据。");
        sb.AppendLine("- 最终回答经过确定性质量检查，避免遗漏来源和下一步。");

        sb.AppendLine();
        sb.AppendLine("资料依据：");
        sb.AppendLine(Trim(evidence, 1800));

        sb.AppendLine();
        sb.AppendLine("下一步建议：");
        sb.AppendLine("- 如果用于答辩，展示控制台中的 Plan / Execute / Review 表格。");
        sb.AppendLine("- 如果要继续细化，针对资料依据中的来源编号继续追问或使用 `read_course_material` 精读。");
        sb.AppendLine("- 验收命令：`dotnet run --no-build --project src\\SmartStudy.Cli\\SmartStudy.Cli.csproj -- plan-execute \"解释 ReAct Agent\"`。");

        return sb.ToString().TrimEnd();
    }

    private static bool IsEvidenceUsable(string evidence) =>
        !evidence.Contains("尚未建立索引", StringComparison.OrdinalIgnoreCase)
        && !evidence.Contains("未检索到相关内容", StringComparison.OrdinalIgnoreCase)
        && !evidence.Contains("检索失败", StringComparison.OrdinalIgnoreCase);

    private async Task<string> SearchSafelyAsync(string goal, CancellationToken ct)
    {
        try
        {
            return await _search.SearchAsync(goal, 4, ct);
        }
        catch (Exception ex)
        {
            return $"检索失败：{ex.Message}";
        }
    }

    private static string Trim(string text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return "暂无资料。";
        return text.Length <= maxChars ? text.Trim() : text[..maxChars].TrimEnd() + Environment.NewLine + "...";
    }
}
