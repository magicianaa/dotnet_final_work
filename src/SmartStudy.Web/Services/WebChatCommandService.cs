using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartStudy.Core.Agent;
using SmartStudy.Core.Configuration;
using SmartStudy.Core.Memory;
using SmartStudy.Core.Rag;
using SmartStudy.Core.Tools;

namespace SmartStudy.Web.Services;

public sealed record WebChatCommandResult(
    bool Handled,
    string Response = "",
    string StatusMessage = "",
    bool? UseStreaming = null,
    bool RefreshDashboard = false)
{
    public static WebChatCommandResult NotHandled { get; } = new(false);
}

public sealed class WebChatCommandService
{
    private readonly LlmProfileManager _profiles;
    private readonly IConversationMemory _memory;
    private readonly ToolRegistry _tools;
    private readonly IServiceProvider _services;

    public WebChatCommandService(
        LlmProfileManager profiles,
        IConversationMemory memory,
        ToolRegistry tools,
        IServiceProvider services)
    {
        _profiles = profiles;
        _memory = memory;
        _tools = tools;
        _services = services;
    }

    public async Task<WebChatCommandResult> TryExecuteAsync(string input, bool currentUseStreaming, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input) || !input.TrimStart().StartsWith(':'))
            return WebChatCommandResult.NotHandled;

        input = input.Trim();
        var space = input.IndexOf(' ');
        var command = (space < 0 ? input : input[..space]).ToLowerInvariant();
        var rest = space < 0 ? "" : input[(space + 1)..].Trim();

        switch (command)
        {
            case ":q":
            case ":quit":
            case ":exit":
                return Handled("Web 端无需退出命令；关闭页面或继续输入问题即可。");

            case ":reset":
                _memory.Reset();
                return Handled("对话记忆已清空。下一次提问会以新的上下文开始。", refreshDashboard: true);

            case ":stream":
                var next = !currentUseStreaming;
                return Handled($"流式输出 = {next}", useStreaming: next);

            case ":models":
                return Handled(FormatModelProfiles(), refreshDashboard: true);

            case ":model":
                return SwitchModel(rest);

            case ":help":
            case ":commands":
                return Handled(FormatCommandHelp());
        }

        if (TryReadPlanExecuteCommand(input, out var planGoal))
            return await RunPlanExecuteAsync(planGoal, ct);

        if (TryReadMultiAgentCommand(input, out var multiGoal))
            return await RunMultiAgentAsync(multiGoal, ct);

        var toolCommand = ParseToolCommand(command, rest);
        return toolCommand is null
            ? Handled($"未识别的 CLI 指令：`{command}`。输入 `:help` 查看支持的命令。")
            : await RunToolCommandAsync(command, rest, toolCommand.Value, ct);
    }

    private WebChatCommandResult SwitchModel(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            return Handled("用法：`:model <name>`。可以先输入 `:models` 查看可用 profile。");

        var ok = _profiles.TrySwitch(profileName, out var message);
        if (ok) _memory.Reset();

        var suffix = ok ? "，已清空对话上下文以避免跨模型污染。" : "";
        return Handled(message + suffix, refreshDashboard: true);
    }

    private async Task<WebChatCommandResult> RunPlanExecuteAsync(string goal, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(goal))
            return Handled("用法：`:plan-execute <goal>`，例如 `:plan-execute 解释 ReAct Agent`。");

        await LoadIndexIfAvailableAsync(ct);
        var result = await _services.GetRequiredService<PlanExecuteAgent>().RunAsync(goal, ct);
        var sb = new StringBuilder();
        sb.AppendLine("### Plan-and-Execute");
        sb.AppendLine();
        sb.AppendLine($"目标：{result.Goal}");
        sb.AppendLine();
        sb.AppendLine("| Step | Status | Output |");
        sb.AppendLine("|---|---|---|");
        foreach (var step in result.Steps)
            sb.AppendLine($"| {EscapeCell(step.Name)} | {(step.IsSuccessful ? "OK" : "WARN")} | {EscapeCell(Summarize(step.Output, 360))} |");
        sb.AppendLine();
        sb.AppendLine(result.Review.Passed ? "**Quality Review: PASS**" : "**Quality Review: NEEDS ATTENTION**");
        sb.AppendLine();
        sb.AppendLine(result.FinalAnswer);

        return Handled(sb.ToString().TrimEnd());
    }

    private async Task<WebChatCommandResult> RunMultiAgentAsync(string goal, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(goal))
            return Handled("用法：`:multi <goal>`，例如 `:multi 帮我准备 Agent 项目答辩`。");

        await LoadIndexIfAvailableAsync(ct);
        var result = await _services.GetRequiredService<MultiAgentOrchestrator>().RunAsync(goal, ct);
        var sb = new StringBuilder();
        sb.AppendLine("### SmartStudy Multi-Agent 协作");
        sb.AppendLine();
        sb.AppendLine($"目标：{result.Goal}");
        sb.AppendLine();
        sb.AppendLine("| Agent | 职责 | 状态 | 输出摘要 |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var step in result.Steps)
            sb.AppendLine($"| {EscapeCell(step.AgentName)} | {EscapeCell(step.Responsibility)} | {(step.IsSuccessful ? "OK" : "WARN")} | {EscapeCell(Summarize(step.Output, 360))} |");
        sb.AppendLine();
        sb.AppendLine(result.PassedReview ? "**Reviewer: PASS**" : "**Reviewer: NEEDS ATTENTION**");
        sb.AppendLine();
        sb.AppendLine(result.FinalAnswer);

        return Handled(sb.ToString().TrimEnd());
    }

    private async Task<WebChatCommandResult> RunToolCommandAsync(
        string command,
        string rest,
        (string ToolName, string ArgumentsJson) parsed,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rest) && command is not ":profile" and not ":notes" and not ":progress" and not ":history" and not ":mistakes")
            return Handled("缺少参数。输入 `:help` 查看用法。");

        if (!_tools.TryGet(parsed.ToolName, out var tool))
            return Handled($"未找到工具：`{parsed.ToolName}`。");

        try
        {
            using var doc = JsonDocument.Parse(parsed.ArgumentsJson);
            var result = await tool.InvokeAsync(doc.RootElement, ct);
            return Handled($"### {parsed.ToolName}\n\n{result}", refreshDashboard: true);
        }
        catch (Exception ex)
        {
            return Handled($"工具指令执行失败：{ex.Message}", refreshDashboard: true);
        }
    }

    private async Task LoadIndexIfAvailableAsync(CancellationToken ct)
    {
        var indexer = _services.GetService<KnowledgeIndexer>();
        if (indexer is not null)
            await indexer.LoadIfExistsAsync(ct);
    }

    private string FormatModelProfiles()
    {
        var sb = new StringBuilder();
        sb.AppendLine("当前可用的 LLM profiles：");
        sb.AppendLine();
        sb.AppendLine("| Profile | Model | BaseUrl | MaxTokens |");
        sb.AppendLine("|---|---|---|---:|");

        foreach (var kv in _profiles.Profiles.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var marker = kv.Key.Equals(_profiles.CurrentName, StringComparison.OrdinalIgnoreCase) ? " *" : "";
            sb.AppendLine($"| `{EscapeCell(kv.Key + marker)}` | `{EscapeCell(kv.Value.Model)}` | `{EscapeCell(kv.Value.BaseUrl)}` | {kv.Value.MaxTokens} |");
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatCommandHelp() => """
        ### CLI 指令

        | Command | Effect |
        |---|---|
        | `:q` | Web 端提示退出方式 |
        | `:help` / `:commands` | 显示全部冒号指令 |
        | `:reset` | 清空对话记忆 |
        | `:stream` | 切换流式输出 |
        | `:models` | 查看模型列表 |
        | `:model <name>` | 切换模型 |
        | `:multi <goal>` | 启动 Multi-Agent 协作 |
        | `:plan-execute <goal>` | 启动 Plan-and-Execute |
        | `:search <query>` | 调用 `knowledge_search` |
        | `:read <file> [start-end]` | 调用 `read_course_material` |
        | `:note <title> \| <content> \| <tag1,tag2>` | 调用 `add_note` |
        | `:notes [tag-or-keyword]` | 调用 `list_notes` |
        | `:profile` | 调用 `show_learning_profile` |
        | `:plan <goal>` | 调用 `study_plan` |
        | `:task <title> \| <topic> \| <minutes>` | 调用 `add_study_task` |
        | `:done <task> \| <reflection> \| <actualMinutes>` | 调用 `mark_task_done` |
        | `:progress` | 调用 `show_progress` |
        | `:history [limit]` | 调用 `review_history` |
        | `:quiz <material> \| <count>` | 调用 `make_quiz` |
        | `:answer <quizId> \| <number> \| <answer> \| <topic>` | 调用 `submit_quiz_answer` |
        | `:mistake <question> \| <topic> \| <your> \| <correct> \| <explanation>` | 调用 `record_quiz_result` |
        | `:mistakes [topic]` | 调用 `show_mistakes` |
        | `:calc <expression>` | 调用 `calculate` |
        | `:import <directory> \| <glob>` | 调用 `import_course_materials` |
        """;

    private static (string ToolName, string ArgumentsJson)? ParseToolCommand(string command, string rest) =>
        command switch
        {
            ":search" => ToolCommand("knowledge_search", JsonSerializer.Serialize(new { query = rest })),
            ":read" => ToolCommand("read_course_material", BuildReadArgs(rest)),
            ":note" => ToolCommand("add_note", BuildNoteArgs(rest)),
            ":notes" => ToolCommand("list_notes", BuildNotesArgs(rest)),
            ":profile" => ToolCommand("show_learning_profile", "{}"),
            ":plan" => ToolCommand("study_plan", JsonSerializer.Serialize(new { goal = rest })),
            ":task" => ToolCommand("add_study_task", BuildTaskArgs(rest)),
            ":done" => ToolCommand("mark_task_done", BuildDoneArgs(rest)),
            ":progress" => ToolCommand("show_progress", "{}"),
            ":history" => ToolCommand("review_history", BuildHistoryArgs(rest)),
            ":quiz" => ToolCommand("make_quiz", BuildQuizArgs(rest)),
            ":answer" => ToolCommand("submit_quiz_answer", BuildAnswerArgs(rest)),
            ":mistake" => ToolCommand("record_quiz_result", BuildMistakeArgs(rest)),
            ":mistakes" => ToolCommand("show_mistakes", BuildMistakesArgs(rest)),
            ":calc" => ToolCommand("calculate", JsonSerializer.Serialize(new { expression = rest })),
            ":import" => ToolCommand("import_course_materials", BuildImportArgs(rest)),
            _ => null
        };

    private static (string ToolName, string ArgumentsJson) ToolCommand(string toolName, string argumentsJson) =>
        (toolName, argumentsJson);

    private static string BuildReadArgs(string rest)
    {
        var fileName = rest;
        int? startPage = null;
        int? endPage = null;

        var lastSpace = rest.LastIndexOf(' ');
        if (lastSpace > 0 && TryParsePageRange(rest[(lastSpace + 1)..].Trim(), out startPage, out endPage))
            fileName = rest[..lastSpace].Trim();

        return JsonSerializer.Serialize(new { fileName, startPage, endPage });
    }

    private static bool TryParsePageRange(string value, out int? startPage, out int? endPage)
    {
        startPage = null;
        endPage = null;

        var rangeParts = value.Split('-', 2, StringSplitOptions.TrimEntries);
        if (!int.TryParse(rangeParts[0], out var start)) return false;

        startPage = start;
        if (rangeParts.Length > 1 && int.TryParse(rangeParts[1], out var end)) endPage = end;
        return true;
    }

    private static string BuildNoteArgs(string rest)
    {
        var parts = rest.Split('|', StringSplitOptions.TrimEntries);
        var title = parts.Length > 0 ? parts[0] : "未命名笔记";
        var content = parts.Length > 1 ? parts[1] : rest;
        var tags = parts.Length > 2
            ? parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();
        return JsonSerializer.Serialize(new { title, content, tags });
    }

    private static string BuildNotesArgs(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest)) return "{}";
        return rest.StartsWith("#", StringComparison.Ordinal)
            ? JsonSerializer.Serialize(new { tag = rest[1..] })
            : JsonSerializer.Serialize(new { keyword = rest });
    }

    private static string BuildQuizArgs(string rest)
    {
        var parts = rest.Split('|', StringSplitOptions.TrimEntries);
        var material = parts.Length > 0 ? parts[0] : "";
        var count = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 3;
        return JsonSerializer.Serialize(new { material, count });
    }

    private static string BuildTaskArgs(string rest)
    {
        var parts = rest.Split('|', StringSplitOptions.TrimEntries);
        var title = parts.Length > 0 ? parts[0] : "";
        var topic = parts.Length > 1 ? parts[1] : "";
        var minutes = parts.Length > 2 && int.TryParse(parts[2], out var n) ? n : 30;
        return JsonSerializer.Serialize(new { title, topic, minutes });
    }

    private static string BuildDoneArgs(string rest)
    {
        var parts = rest.Split('|', StringSplitOptions.TrimEntries);
        var task = parts.Length > 0 ? parts[0] : "";
        var reflection = parts.Length > 1 ? parts[1] : "";
        int? actualMinutes = parts.Length > 2 && int.TryParse(parts[2], out var n) ? n : null;
        return JsonSerializer.Serialize(new { task, reflection, actualMinutes });
    }

    private static string BuildHistoryArgs(string rest)
    {
        var limit = int.TryParse(rest, out var n) ? n : 10;
        return JsonSerializer.Serialize(new { limit });
    }

    private static string BuildMistakeArgs(string rest)
    {
        var parts = rest.Split('|', StringSplitOptions.TrimEntries);
        var question = parts.Length > 0 ? parts[0] : "";
        var topic = parts.Length > 1 ? parts[1] : "";
        var userAnswer = parts.Length > 2 ? parts[2] : "";
        var correctAnswer = parts.Length > 3 ? parts[3] : "";
        var explanation = parts.Length > 4 ? parts[4] : "";
        return JsonSerializer.Serialize(new
        {
            question,
            topic,
            userAnswer,
            correctAnswer,
            isCorrect = string.Equals(userAnswer.Trim(), correctAnswer.Trim(), StringComparison.OrdinalIgnoreCase),
            explanation
        });
    }

    private static string BuildAnswerArgs(string rest)
    {
        var parts = rest.Split('|', StringSplitOptions.TrimEntries);
        var quizId = parts.Length > 0 ? parts[0] : "latest";
        var questionNumber = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 1;
        var answer = parts.Length > 2 ? parts[2] : "";
        var topic = parts.Length > 3 ? parts[3] : "";
        return JsonSerializer.Serialize(new { quizId, questionNumber, answer, topic });
    }

    private static string BuildMistakesArgs(string rest) =>
        string.IsNullOrWhiteSpace(rest)
            ? "{}"
            : JsonSerializer.Serialize(new { topic = rest });

    private static string BuildImportArgs(string rest)
    {
        var parts = rest.Split('|', StringSplitOptions.TrimEntries);
        var directory = parts.Length > 0 ? parts[0] : "";
        var glob = parts.Length > 1 ? parts[1] : null;
        return JsonSerializer.Serialize(new { directory, glob });
    }

    private static bool TryReadMultiAgentCommand(string input, out string goal)
    {
        const string multi = ":multi ";
        const string multiAgent = ":multi-agent ";

        if (input.Equals(":multi", StringComparison.OrdinalIgnoreCase)
            || input.Equals(":multi-agent", StringComparison.OrdinalIgnoreCase))
        {
            goal = "";
            return true;
        }

        if (input.StartsWith(multi, StringComparison.OrdinalIgnoreCase))
        {
            goal = input[multi.Length..].Trim();
            return true;
        }

        if (input.StartsWith(multiAgent, StringComparison.OrdinalIgnoreCase))
        {
            goal = input[multiAgent.Length..].Trim();
            return true;
        }

        goal = "";
        return false;
    }

    private static bool TryReadPlanExecuteCommand(string input, out string goal)
    {
        var prefixes = new[] { ":plan-execute ", ":plan-and-execute ", ":pe " };
        if (input.Equals(":plan-execute", StringComparison.OrdinalIgnoreCase)
            || input.Equals(":plan-and-execute", StringComparison.OrdinalIgnoreCase)
            || input.Equals(":pe", StringComparison.OrdinalIgnoreCase))
        {
            goal = "";
            return true;
        }

        foreach (var prefix in prefixes)
        {
            if (input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                goal = input[prefix.Length..].Trim();
                return true;
            }
        }

        goal = "";
        return false;
    }

    private static WebChatCommandResult Handled(
        string response,
        string status = "CLI 指令已执行。",
        bool? useStreaming = null,
        bool refreshDashboard = false) =>
        new(true, response, status, useStreaming, refreshDashboard);

    private static string Summarize(string text, int maxChars)
    {
        var normalized = string.Join(' ', text.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= maxChars ? normalized : normalized[..maxChars].TrimEnd() + "...";
    }

    private static string EscapeCell(string value) =>
        value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
