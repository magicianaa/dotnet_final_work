using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using SmartStudy.Cli;
using SmartStudy.Core.Agent;
using SmartStudy.Core.Configuration;
using SmartStudy.Core.Llm;
using SmartStudy.Core.Memory;
using SmartStudy.Core.Rag;
using SmartStudy.Core.Tools;
using SmartStudy.Core.Tools.Builtin;
using SmartStudy.Core.Tracing;
using SmartStudy.Core.Workspace;
using Spectre.Console;

// 固定运行目录到可执行文件目录，确保 appsettings.json、knowledge/ 和 data/ 的相对路径
// 在 dotnet run、发布运行、从任意目录启动时都一致。
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

// ----- 1) 配置 + DI -----
var host = Host.CreateApplicationBuilder(args);
host.Configuration
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables(prefix: "SMARTSTUDY_");

host.Services.Configure<AgentOptions>(host.Configuration.GetSection("Agent"));
host.Services.AddSingleton<LlmProfileManager>();
host.Services.AddSingleton<LearningProjectService>();
host.Services.AddSingleton<IRagRuntimeContext>(sp => sp.GetRequiredService<LearningProjectService>());

host.Services.AddHttpClient<ILlmClient, OpenAiLlmClient>();
host.Services.AddHttpClient<ZhipuEmbeddingClient>();
host.Services.AddSingleton<LocalHashEmbeddingClient>();
host.Services.AddSingleton<IEmbeddingClient>(sp =>
{
    var provider = sp.GetRequiredService<IOptions<AgentOptions>>().Value.Embedding.Provider;
    return provider.Equals("local", StringComparison.OrdinalIgnoreCase)
        ? sp.GetRequiredService<LocalHashEmbeddingClient>()
        : sp.GetRequiredService<ZhipuEmbeddingClient>();
});

host.Services.AddSingleton<IVectorStore, ProjectVectorStore>();
host.Services.AddSingleton(sp => new KnowledgeIndexer(
    sp.GetRequiredService<IEmbeddingClient>(),
    sp.GetRequiredService<IVectorStore>(),
    sp.GetRequiredService<IRagRuntimeContext>(),
    sp.GetRequiredService<ILogger<KnowledgeIndexer>>()));
host.Services.AddSingleton(sp => new KnowledgeSearchService(
    sp.GetRequiredService<IEmbeddingClient>(),
    sp.GetRequiredService<IVectorStore>(),
    sp.GetRequiredService<IRagRuntimeContext>()));
host.Services.AddSingleton(sp => new CourseMaterialCatalog(
    sp.GetRequiredService<IRagRuntimeContext>()));
host.Services.AddSingleton(sp => new CourseMaterialImporter(
    sp.GetRequiredService<KnowledgeIndexer>(),
    sp.GetRequiredService<IRagRuntimeContext>(),
    sp.GetRequiredService<ILogger<CourseMaterialImporter>>()));
host.Services.AddSingleton<INoteStore, ProjectNoteStore>();
host.Services.AddSingleton<ILearningProfileStore, ProjectLearningProfileStore>();
host.Services.AddSingleton<IStudyProgressStore, ProjectStudyProgressStore>();
host.Services.AddSingleton<IQuizResultStore, ProjectQuizResultStore>();
host.Services.AddSingleton<IQuizSessionStore, ProjectQuizSessionStore>();
host.Services.AddSingleton<IConversationMemory, ProjectConversationMemory>();

// Agent 工具
host.Services.AddSingleton<ITool, KnowledgeSearchTool>();
host.Services.AddSingleton<ITool>(sp => new ReadCourseMaterialTool(
    sp.GetRequiredService<IVectorStore>(),
    sp.GetRequiredService<IRagRuntimeContext>()));
host.Services.AddSingleton<ITool, ImportCourseMaterialsTool>();
host.Services.AddSingleton<ITool, AddNoteTool>();
host.Services.AddSingleton<ITool, ListNotesTool>();
host.Services.AddSingleton<ITool, UpdateLearningProfileTool>();
host.Services.AddSingleton<ITool, ShowLearningProfileTool>();
host.Services.AddSingleton<ITool, StudyPlanTool>();
host.Services.AddSingleton<ITool, AddStudyTaskTool>();
host.Services.AddSingleton<ITool, MarkTaskDoneTool>();
host.Services.AddSingleton<ITool, ShowProgressTool>();
host.Services.AddSingleton<ITool, ReviewHistoryTool>();
host.Services.AddSingleton<ITool, RecordQuizResultTool>();
host.Services.AddSingleton<ITool, SubmitQuizAnswerTool>();
host.Services.AddSingleton<ITool, ShowMistakesTool>();
host.Services.AddSingleton<ITool, CalculatorTool>();
host.Services.AddSingleton<ITool, MakeQuizTool>();
host.Services.AddSingleton<ToolRegistry>();
host.Services.AddSingleton<SmartStudyDoctor>();
host.Services.AddSingleton<MultiAgentOrchestrator>();
host.Services.AddSingleton<AnswerQualityReviewer>();
host.Services.AddSingleton<PlanExecuteAgent>();

// 追踪：Spectre 控制台 + 文件 JSONL
host.Services.AddSingleton<IAgentTracer>(sp =>
{
    var path = Path.Combine("data", $"trace-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.jsonl");
    return new CompositeAgentTracer(new IAgentTracer[]
    {
        new SpectreConsoleTracer(),
        new JsonlFileTracer(path)
    });
});

host.Services.AddSingleton<ReActAgent>();

host.Logging.ClearProviders();
host.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });

using var app = host.Build();

ApplyCommandLineModelSelection(app.Services, args);

// ----- 2) 子命令分发 -----
var cmd = args.FirstOrDefault() ?? "chat";
switch (cmd)
{
    case "index":
        await RunIndex(app.Services);
        break;
    case "doctor":
    case "status":
        await RunDoctor(app.Services);
        break;
    case "tools":
        await RunTools(app.Services);
        break;
    case "project":
    case "projects":
        await RunProjectCommand(app.Services, args.Skip(1).ToArray());
        break;
    case "conversation":
    case "conversations":
    case "conv":
        await RunConversationCommand(app.Services, args.Skip(1).ToArray());
        break;
    case "multi":
    case "multi-agent":
        var goal = string.Join(' ', args.Skip(1));
        if (string.IsNullOrWhiteSpace(goal))
        {
            AnsiConsole.MarkupLine("[red]用法：dotnet run -- multi \"你的协作目标\"[/]");
            return;
        }
        await RunMultiAgent(app.Services, goal);
        break;
    case "plan":
    case "plan-execute":
    case "plan-and-execute":
        var planGoal = string.Join(' ', args.Skip(1));
        if (string.IsNullOrWhiteSpace(planGoal))
        {
            AnsiConsole.MarkupLine("[red]用法：dotnet run -- plan-execute \"你的目标\"[/]");
            return;
        }
        await RunPlanExecute(app.Services, planGoal);
        break;
    case "reset":
        var mem = app.Services.GetRequiredService<IConversationMemory>();
        mem.Reset();
        AnsiConsole.MarkupLine("[green]已清空对话记忆[/]");
        break;
    case "ask":
        var question = string.Join(' ', StripOptionWithValue(args.Skip(1), "--llm", "--model").Where(a => a != "--stream"));
        if (string.IsNullOrWhiteSpace(question))
        {
            AnsiConsole.MarkupLine("[red]用法：dotnet run -- ask \"你的问题\" [--stream][/]");
            return;
        }
        await RunOneShot(app.Services, question, useStreaming: args.Contains("--stream"));
        break;
    case "chat":
    default:
        await RunChat(app.Services, useStreaming: args.Contains("--stream"));
        break;
}

// ----- 3) 命令实现 -----
static async Task RunIndex(IServiceProvider sp)
{
    var indexer = sp.GetRequiredService<KnowledgeIndexer>();
    var embedding = sp.GetRequiredService<IOptions<AgentOptions>>().Value.Embedding;
    var project = sp.GetRequiredService<LearningProjectService>().CurrentProject;
    AnsiConsole.MarkupLine($"[dim]Project: {Markup.Escape(project.Name)} ({Markup.Escape(project.Id)})[/]");
    AnsiConsole.MarkupLine($"[dim]Embedding Provider: {Markup.Escape(embedding.Provider)}[/]");
    try
    {
        await AnsiConsole.Status().StartAsync("构建知识库索引…", async _ => await indexer.BuildAsync());
        AnsiConsole.MarkupLine("[green]知识库索引构建完成[/]");
    }
    catch (HttpRequestException ex)
    {
        AnsiConsole.MarkupLine("[red]知识库索引构建失败：Embedding API 请求未成功。[/]");
        AnsiConsole.MarkupLine($"[dim]{Markup.Escape(ex.Message)}[/]");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine("[red]知识库索引构建失败。[/]");
        AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
    }
}

static async Task RunChat(IServiceProvider sp, bool useStreaming)
{
    var indexer = sp.GetRequiredService<KnowledgeIndexer>();
    if (await indexer.LoadIfExistsAsync())
        AnsiConsole.MarkupLine("[dim]已加载已有知识库索引[/]");
    else
        AnsiConsole.MarkupLine("[yellow]提示：尚未构建知识库索引，knowledge_search 将不可用。运行 `dotnet run -- index` 先建索引。[/]");

    var agent = sp.GetRequiredService<ReActAgent>();
    var profiles = sp.GetRequiredService<LlmProfileManager>();
    var projects = sp.GetRequiredService<LearningProjectService>();
    var workspace = await projects.GetStateAsync();

    AnsiConsole.Write(new Rule("[bold cyan]SmartStudy AI 学习助手[/]").RuleStyle("grey"));
    AnsiConsole.MarkupLine($"[dim]输入问题与 Agent 对话；常用指令：:q 退出，:stream 切换流式，:project new <目录> | <名称> 新建项目，:conversation new <标题> 新建对话，:help 显示全部冒号指令。当前 LLM: {Markup.Escape(profiles.CurrentName)} ({Markup.Escape(profiles.Current.Model)})[/]");
    AnsiConsole.MarkupLine($"[dim]当前项目：{Markup.Escape(workspace.CurrentProject.Name)}；当前对话：{Markup.Escape(workspace.CurrentConversation.Title)}[/]");

    while (true)
    {
        var input = ConsoleLineEditor.ReadLine("> ");
        if (string.IsNullOrWhiteSpace(input)) continue;
        if (input == ":q") break;
        if (input == ":reset") { sp.GetRequiredService<IConversationMemory>().Reset(); AnsiConsole.MarkupLine("[green]记忆已清空[/]"); continue; }
        if (input == ":stream") { useStreaming = !useStreaming; AnsiConsole.MarkupLine($"[green]流式输出 = {useStreaming}[/]"); continue; }
        if (input == ":models") { PrintModelProfiles(profiles); continue; }
        if (input is ":help" or ":commands") { PrintChatCommands(); continue; }
        if (await TryRunWorkspaceCommand(sp, input))
            continue;
        if (TryReadPlanExecuteCommand(input, out var planGoal))
        {
            if (string.IsNullOrWhiteSpace(planGoal))
                AnsiConsole.MarkupLine("[yellow]用法：:plan-execute 你的目标[/]");
            else
                await RunPlanExecute(sp, planGoal);
            continue;
        }
        if (TryReadMultiAgentCommand(input, out var multiGoal))
        {
            if (string.IsNullOrWhiteSpace(multiGoal))
                AnsiConsole.MarkupLine("[yellow]用法：:multi 你的协作目标[/]");
            else
                await RunMultiAgent(sp, multiGoal);
            continue;
        }
        if (await TryRunToolCommand(sp, input))
            continue;
        if (input.StartsWith(":model ", StringComparison.OrdinalIgnoreCase))
        {
            var name = input[7..].Trim();
            if (profiles.TrySwitch(name, out var message))
            {
                sp.GetRequiredService<IConversationMemory>().Reset();
                AnsiConsole.MarkupLine($"[green]{Markup.Escape(message)}，已清空对话上下文以避免跨模型污染。[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
            }
            continue;
        }

        try
        {
            if (useStreaming)
            {
                await foreach (var ev in agent.RunStreamingAsync(input))
                {
                    // 流式时仍然让 tracer 自己渲染（注意 StreamDelta 在 tracer 中直接 Markup）
                    await sp.GetRequiredService<IAgentTracer>().TrackAsync(ev);
                }
                AnsiConsole.WriteLine();
            }
            else
            {
                var result = await agent.RunAsync(input);
                if (result.ReachedLimit)
                    AnsiConsole.MarkupLine("[yellow]⚠ 达到最大循环步数[/]");
            }
            await projects.TouchConversationAsync(input);
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
        }
    }
}

static async Task RunDoctor(IServiceProvider sp)
{
    var indexer = sp.GetRequiredService<KnowledgeIndexer>();
    await indexer.LoadIfExistsAsync();

    var snapshot = await sp.GetRequiredService<SmartStudyDoctor>().InspectAsync();
    AnsiConsole.Write(new Rule("[bold cyan]SmartStudy Doctor[/]").RuleStyle("grey"));

    var summary = new Table().RoundedBorder();
    summary.AddColumn("Item");
    summary.AddColumn("Value");
    summary.AddRow("Base Directory", snapshot.BaseDirectory);
    summary.AddRow("LLM Profile", $"{snapshot.CurrentLlmProfile} ({snapshot.CurrentLlmModel})");
    summary.AddRow("LLM BaseUrl", snapshot.CurrentLlmBaseUrl);
    summary.AddRow("Embedding", $"{snapshot.EmbeddingProvider} ({snapshot.EmbeddingModel})");
    summary.AddRow("Knowledge", $"{snapshot.MarkdownFileCount} markdown, {snapshot.ImportedMaterialCount} imported");
    summary.AddRow("Vector Store", $"{snapshot.VectorStoreKind} ({snapshot.LoadedChunkCount} chunks)");
    summary.AddRow("Index", snapshot.IndexFileExists ? $"{snapshot.IndexFileBytes} bytes" : "missing");
    summary.AddRow("Notes", snapshot.NotesFileExists ? $"{snapshot.NoteCount} notes" : "not created");
    summary.AddRow("Learning Profile", snapshot.LearningProfileFileExists ? "created" : "not created");
    summary.AddRow("Tools", snapshot.Tools.Count.ToString());
    AnsiConsole.Write(summary);

    var checks = new Table().RoundedBorder();
    checks.AddColumn("Status");
    checks.AddColumn("Check");
    checks.AddColumn("Value");
    checks.AddColumn("Detail");
    foreach (var item in snapshot.StatusItems)
    {
        checks.AddRow(
            item.IsHealthy ? "[green]OK[/]" : "[red]FAIL[/]",
            Markup.Escape(item.Name),
            Markup.Escape(item.Value),
            Markup.Escape(item.Detail));
    }
    AnsiConsole.Write(checks);

    AnsiConsole.MarkupLine(snapshot.IsHealthy
        ? "[green]Doctor 检查通过，项目具备演示条件。[/]"
        : "[yellow]Doctor 发现需要处理的配置或索引问题，请查看 FAIL 行。[/]");
}

static async Task RunTools(IServiceProvider sp)
{
    var snapshot = await sp.GetRequiredService<SmartStudyDoctor>().InspectAsync();
    var table = new Table().RoundedBorder();
    table.AddColumn("Tool");
    table.AddColumn("Description");
    foreach (var tool in snapshot.Tools)
        table.AddRow(Markup.Escape(tool.Name), Markup.Escape(tool.Description));
    AnsiConsole.Write(table);
}

static async Task RunProjectCommand(IServiceProvider sp, string[] args)
{
    var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "list";
    var rest = args.Skip(1).ToArray();

    switch (command)
    {
        case "list":
        case "ls":
            await PrintProjects(sp);
            break;
        case "current":
        case "show":
            await PrintCurrentWorkspace(sp);
            break;
        case "new":
        case "create":
        case "add":
            await CreateProjectFromArgs(sp, rest);
            break;
        case "switch":
        case "select":
        case "use":
            await SwitchProjectFromArgs(sp, rest);
            break;
        case "delete":
        case "remove":
        case "rm":
            await DeleteProjectFromArgs(sp, rest);
            break;
        default:
            AnsiConsole.MarkupLine("[yellow]用法：project list | project current | project new <目录> [| 名称] | project switch <项目ID或名称> | project delete <项目ID或名称>[/]");
            break;
    }
}

static async Task RunConversationCommand(IServiceProvider sp, string[] args)
{
    var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "list";
    var rest = args.Skip(1).ToArray();

    switch (command)
    {
        case "list":
        case "ls":
            await PrintConversations(sp);
            break;
        case "current":
        case "show":
            await PrintCurrentWorkspace(sp);
            break;
        case "new":
        case "create":
        case "add":
            await CreateConversationFromArgs(sp, rest);
            break;
        case "switch":
        case "select":
        case "use":
            await SwitchConversationFromArgs(sp, rest);
            break;
        case "delete":
        case "remove":
        case "rm":
            await DeleteConversationFromArgs(sp, rest);
            break;
        default:
            AnsiConsole.MarkupLine("[yellow]用法：conversation list | conversation current | conversation new [标题] | conversation switch <对话ID或标题> | conversation delete <对话ID或标题>[/]");
            break;
    }
}

static async Task RunMultiAgent(IServiceProvider sp, string goal)
{
    var indexer = sp.GetRequiredService<KnowledgeIndexer>();
    if (await indexer.LoadIfExistsAsync())
        AnsiConsole.MarkupLine("[dim]已加载已有知识库索引[/]");
    else
        AnsiConsole.MarkupLine("[yellow]提示：尚未构建知识库索引，ResearchAgent 将无法提供资料依据。运行 `dotnet run -- index` 先建索引。[/]");

    var orchestrator = sp.GetRequiredService<MultiAgentOrchestrator>();
    var result = await orchestrator.RunAsync(goal);

    AnsiConsole.Write(new Rule("[bold cyan]SmartStudy Multi-Agent 协作[/]").RuleStyle("grey"));
    AnsiConsole.MarkupLine($"[bold]目标：[/] {Markup.Escape(result.Goal)}");
    AnsiConsole.WriteLine();

    var table = new Table().RoundedBorder();
    table.AddColumn("Agent");
    table.AddColumn("职责");
    table.AddColumn("状态");
    table.AddColumn("输出摘要");

    foreach (var step in result.Steps)
    {
        table.AddRow(
            Markup.Escape(step.AgentName),
            Markup.Escape(step.Responsibility),
            step.IsSuccessful ? "[green]OK[/]" : "[yellow]WARN[/]",
            Markup.Escape(SummarizeForTable(step.Output, 360)));
    }

    AnsiConsole.Write(table);
    AnsiConsole.Write(new Rule(result.PassedReview ? "[green]Reviewer: PASS[/]" : "[yellow]Reviewer: NEEDS ATTENTION[/]").RuleStyle("grey"));
    AnsiConsole.Write(new Panel(Markup.Escape(result.FinalAnswer))
        .Header("Final Answer")
        .RoundedBorder()
        .BorderColor(result.PassedReview ? Color.Green : Color.Yellow));
}

static async Task RunPlanExecute(IServiceProvider sp, string goal)
{
    var indexer = sp.GetRequiredService<KnowledgeIndexer>();
    if (await indexer.LoadIfExistsAsync())
        AnsiConsole.MarkupLine("[dim]已加载已有知识库索引[/]");
    else
        AnsiConsole.MarkupLine("[yellow]提示：尚未构建知识库索引，Plan-and-Execute 的资料依据会受限。运行 `dotnet run -- index` 先建索引。[/]");

    var agent = sp.GetRequiredService<PlanExecuteAgent>();
    var result = await agent.RunAsync(goal);

    AnsiConsole.Write(new Rule("[bold cyan]SmartStudy Plan-and-Execute[/]").RuleStyle("grey"));
    AnsiConsole.MarkupLine($"[bold]目标：[/] {Markup.Escape(result.Goal)}");
    AnsiConsole.WriteLine();

    var table = new Table().RoundedBorder();
    table.AddColumn("Step");
    table.AddColumn("Status");
    table.AddColumn("Output");

    foreach (var step in result.Steps)
    {
        table.AddRow(
            Markup.Escape(step.Name),
            step.IsSuccessful ? "[green]OK[/]" : "[yellow]WARN[/]",
            Markup.Escape(SummarizeForTable(step.Output, 420)));
    }

    AnsiConsole.Write(table);
    AnsiConsole.Write(new Rule(result.Review.Passed ? "[green]Quality Review: PASS[/]" : "[yellow]Quality Review: NEEDS ATTENTION[/]").RuleStyle("grey"));
    AnsiConsole.Write(new Panel(Markup.Escape(result.FinalAnswer))
        .Header("Final Answer")
        .RoundedBorder()
        .BorderColor(result.Review.Passed ? Color.Green : Color.Yellow));
}

static async Task RunOneShot(IServiceProvider sp, string input, bool useStreaming)
{
    var indexer = sp.GetRequiredService<KnowledgeIndexer>();
    if (await indexer.LoadIfExistsAsync())
        AnsiConsole.MarkupLine("[dim]已加载已有知识库索引[/]");
    else
        AnsiConsole.MarkupLine("[yellow]提示：尚未构建知识库索引，knowledge_search 将不可用。[/]");

    var agent = sp.GetRequiredService<ReActAgent>();
    if (useStreaming)
    {
        await foreach (var ev in agent.RunStreamingAsync(input))
            await sp.GetRequiredService<IAgentTracer>().TrackAsync(ev);
        AnsiConsole.WriteLine();
    }
    else
    {
        var result = await agent.RunAsync(input);
        if (result.ReachedLimit)
            AnsiConsole.MarkupLine("[yellow]⚠ 达到最大循环步数[/]");
    }
}

static void ApplyCommandLineModelSelection(IServiceProvider sp, string[] args)
{
    var llmIndex = Array.FindIndex(args, a => a.Equals("--llm", StringComparison.OrdinalIgnoreCase) || a.Equals("--model", StringComparison.OrdinalIgnoreCase));
    if (llmIndex >= 0 && llmIndex + 1 < args.Length)
    {
        var profiles = sp.GetRequiredService<LlmProfileManager>();
        if (!profiles.TrySwitch(args[llmIndex + 1], out var message))
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        else
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(message)}[/]");
    }
}

static void PrintModelProfiles(LlmProfileManager profiles)
{
    var table = new Table().RoundedBorder();
    table.AddColumn("Profile");
    table.AddColumn("Model");
    table.AddColumn("BaseUrl");
    table.AddColumn("MaxTokens");
    foreach (var kv in profiles.Profiles.OrderBy(k => k.Key))
    {
        var marker = kv.Key.Equals(profiles.CurrentName, StringComparison.OrdinalIgnoreCase) ? " *" : "";
        table.AddRow(kv.Key + marker, kv.Value.Model, kv.Value.BaseUrl, kv.Value.MaxTokens.ToString());
    }
    AnsiConsole.Write(table);
}

static void PrintChatCommands()
{
    var table = new Table().RoundedBorder();
    table.AddColumn("Command");
    table.AddColumn("Effect");
    AddCommandRow(table, ":q", "退出聊天");
    AddCommandRow(table, ":help / :commands", "显示全部冒号指令");
    AddCommandRow(table, ":reset", "清空对话记忆");
    AddCommandRow(table, ":stream", "切换流式输出");
    AddCommandRow(table, ":models", "查看模型列表");
    AddCommandRow(table, ":model <name>", "切换模型");
    AddCommandRow(table, ":projects", "列出学习项目");
    AddCommandRow(table, ":project current", "查看当前项目和对话");
    AddCommandRow(table, ":project new <目录> | <名称>", "新建学习项目并导入资料");
    AddCommandRow(table, ":project switch <项目ID或名称>", "切换学习项目");
    AddCommandRow(table, ":project delete <项目ID或名称>", "删除学习项目及其资料/索引/记忆");
    AddCommandRow(table, ":conversations", "列出当前项目下的学习对话");
    AddCommandRow(table, ":conversation new <标题>", "新建学习对话");
    AddCommandRow(table, ":conversation switch <对话ID或标题>", "切换学习对话");
    AddCommandRow(table, ":conversation delete <对话ID或标题>", "删除学习对话及其记忆");
    AddCommandRow(table, ":multi <goal>", "启动 Multi-Agent 协作");
    AddCommandRow(table, ":plan-execute <goal>", "启动 Plan-and-Execute 并做答案质量检查");
    AddCommandRow(table, ":search <query>", "调用 knowledge_search");
    AddCommandRow(table, ":read <file> [start-end]", "调用 read_course_material");
    AddCommandRow(table, ":note <title> | <content> | <tag1,tag2>", "调用 add_note");
    AddCommandRow(table, ":notes [tag-or-keyword]", "调用 list_notes");
    AddCommandRow(table, ":profile", "调用 show_learning_profile");
    AddCommandRow(table, ":plan <goal>", "调用 study_plan");
    AddCommandRow(table, ":task <title> | <topic> | <minutes>", "调用 add_study_task");
    AddCommandRow(table, ":done <task> | <reflection> | <actualMinutes>", "调用 mark_task_done");
    AddCommandRow(table, ":progress", "调用 show_progress");
    AddCommandRow(table, ":history [limit]", "调用 review_history");
    AddCommandRow(table, ":quiz <material> | <count>", "调用 make_quiz");
    AddCommandRow(table, ":answer <quizId> | <number> | <answer> | <topic>", "调用 submit_quiz_answer");
    AddCommandRow(table, ":mistake <question> | <topic> | <your> | <correct> | <explanation>", "调用 record_quiz_result");
    AddCommandRow(table, ":mistakes [topic]", "调用 show_mistakes");
    AddCommandRow(table, ":calc <expression>", "调用 calculate");
    AddCommandRow(table, ":import <path> | <glob>", "调用 import_course_materials");
    AnsiConsole.Write(table);
}

static void AddCommandRow(Table table, string command, string effect) =>
    table.AddRow(Markup.Escape(command), Markup.Escape(effect));

static IEnumerable<string> StripOptionWithValue(IEnumerable<string> args, params string[] optionNames)
{
    var skipNext = false;
    foreach (var arg in args)
    {
        if (skipNext)
        {
            skipNext = false;
            continue;
        }
        if (optionNames.Any(o => arg.Equals(o, StringComparison.OrdinalIgnoreCase)))
        {
            skipNext = true;
            continue;
        }
        yield return arg;
    }
}

static string SummarizeForTable(string text, int maxChars)
{
    var normalized = string.Join(' ', text.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    return normalized.Length <= maxChars ? normalized : normalized[..maxChars].TrimEnd() + "...";
}

static async Task<bool> TryRunToolCommand(IServiceProvider sp, string input)
{
    if (!input.StartsWith(':') || input.StartsWith(":model ", StringComparison.OrdinalIgnoreCase))
        return false;

    var space = input.IndexOf(' ');
    var command = space < 0 ? input : input[..space];
    var rest = space < 0 ? "" : input[(space + 1)..].Trim();

    var parsed = command.ToLowerInvariant() switch
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

    if (parsed is null) return false;

    if (string.IsNullOrWhiteSpace(rest) && command is not ":profile" and not ":notes" and not ":progress" and not ":history" and not ":mistakes")
    {
        AnsiConsole.MarkupLine($"[yellow]缺少参数。输入 :help 查看用法。[/]");
        return true;
    }

    var registry = sp.GetRequiredService<ToolRegistry>();
    if (!registry.TryGet(parsed.Value.ToolName, out var tool))
    {
        AnsiConsole.MarkupLine($"[red]未找到工具：{Markup.Escape(parsed.Value.ToolName)}[/]");
        return true;
    }

    try
    {
        using var doc = JsonDocument.Parse(parsed.Value.ArgumentsJson);
        var result = await tool.InvokeAsync(doc.RootElement);
        AnsiConsole.Write(new Panel(Markup.Escape(result))
            .Header(parsed.Value.ToolName)
            .RoundedBorder()
            .BorderColor(Color.Blue));
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]工具指令执行失败：{Markup.Escape(ex.Message)}[/]");
    }

    return true;
}

static async Task<bool> TryRunWorkspaceCommand(IServiceProvider sp, string input)
{
    if (!input.StartsWith(':')) return false;

    var normalized = input.Trim();
    var space = normalized.IndexOf(' ');
    var command = (space < 0 ? normalized : normalized[..space]).ToLowerInvariant();
    var rest = space < 0 ? "" : normalized[(space + 1)..].Trim();

    switch (command)
    {
        case ":projects":
            await PrintProjects(sp);
            return true;
        case ":project":
            await RunProjectCommand(sp, SplitWorkspaceArgs(rest));
            return true;
        case ":project-current":
            await PrintCurrentWorkspace(sp);
            return true;
        case ":project-new":
            await CreateProjectFromText(sp, rest);
            return true;
        case ":project-switch":
            await SwitchProjectFromText(sp, rest);
            return true;
        case ":project-delete":
        case ":project-remove":
            await DeleteProjectFromText(sp, rest);
            return true;
        case ":conversations":
        case ":convs":
            await PrintConversations(sp);
            return true;
        case ":conversation":
        case ":conv":
            await RunConversationCommand(sp, SplitWorkspaceArgs(rest));
            return true;
        case ":conversation-current":
        case ":conv-current":
            await PrintCurrentWorkspace(sp);
            return true;
        case ":conversation-new":
        case ":conv-new":
            await CreateConversationFromText(sp, rest);
            return true;
        case ":conversation-switch":
        case ":conv-switch":
            await SwitchConversationFromText(sp, rest);
            return true;
        case ":conversation-delete":
        case ":conversation-remove":
        case ":conv-delete":
        case ":conv-remove":
            await DeleteConversationFromText(sp, rest);
            return true;
    }

    return false;
}

static string[] SplitWorkspaceArgs(string rest)
{
    if (string.IsNullOrWhiteSpace(rest)) return Array.Empty<string>();
    var firstSpace = rest.IndexOf(' ');
    if (firstSpace < 0) return new[] { rest };
    return new[] { rest[..firstSpace], rest[(firstSpace + 1)..].Trim() };
}

static async Task PrintCurrentWorkspace(IServiceProvider sp)
{
    var state = await sp.GetRequiredService<LearningProjectService>().GetStateAsync();
    AnsiConsole.MarkupLine($"[green]当前项目[/] {Markup.Escape(state.CurrentProject.Name)} [dim]({Markup.Escape(state.CurrentProject.Id)})[/]");
    AnsiConsole.MarkupLine($"[green]资料目录[/] {Markup.Escape(state.CurrentProject.SourceDirectory)}");
    AnsiConsole.MarkupLine($"[green]当前对话[/] {Markup.Escape(state.CurrentConversation.Title)} [dim]({Markup.Escape(state.CurrentConversation.Id)})[/]");
}

static async Task PrintProjects(IServiceProvider sp)
{
    var state = await sp.GetRequiredService<LearningProjectService>().GetStateAsync();
    var table = new Table().RoundedBorder();
    table.AddColumn("Active");
    table.AddColumn("Id");
    table.AddColumn("Name");
    table.AddColumn("Directory");
    table.AddColumn("Updated");

    foreach (var project in state.Projects)
    {
        table.AddRow(
            project.Id == state.CurrentProject.Id ? "[green]*[/]" : "",
            Markup.Escape(project.Id),
            Markup.Escape(project.Name),
            Markup.Escape(project.SourceDirectory),
            project.UpdatedAt.ToString("yyyy-MM-dd HH:mm"));
    }

    AnsiConsole.Write(table);
}

static async Task PrintConversations(IServiceProvider sp)
{
    var state = await sp.GetRequiredService<LearningProjectService>().GetStateAsync();
    var table = new Table().RoundedBorder();
    table.AddColumn("Active");
    table.AddColumn("Id");
    table.AddColumn("Title");
    table.AddColumn("Updated");

    foreach (var conversation in state.Conversations)
    {
        table.AddRow(
            conversation.Id == state.CurrentConversation.Id ? "[green]*[/]" : "",
            Markup.Escape(conversation.Id),
            Markup.Escape(conversation.Title),
            conversation.UpdatedAt.ToString("yyyy-MM-dd HH:mm"));
    }

    AnsiConsole.Write(new Rule($"[bold cyan]项目：{Markup.Escape(state.CurrentProject.Name)}[/]").RuleStyle("grey"));
    AnsiConsole.Write(table);
}

static async Task CreateProjectFromArgs(IServiceProvider sp, string[] args)
{
    await CreateProjectFromText(sp, string.Join(' ', args));
}

static async Task CreateProjectFromText(IServiceProvider sp, string text)
{
    var (directory, name) = ParseProjectCreateText(text);
    if (string.IsNullOrWhiteSpace(directory))
    {
        AnsiConsole.MarkupLine("[yellow]用法：project new <目录> [| 项目名][/]\n[dim]示例：project new \"C:\\\\Users\\\\21125\\\\Desktop\\\\SEM & SEP\\\\ppts\" | SEM 课程[/]");
        return;
    }

    var projects = sp.GetRequiredService<LearningProjectService>();
    var importer = sp.GetRequiredService<CourseMaterialImporter>();
    try
    {
        var project = await projects.CreateProjectAsync(directory, name);
        AnsiConsole.MarkupLine($"[green]已创建并切换到项目：{Markup.Escape(project.Name)} ({Markup.Escape(project.Id)})[/]");
        var result = await AnsiConsole.Status().StartAsync("导入项目资料并构建项目索引…", async _ => await importer.ImportAsync(directory));
        AnsiConsole.MarkupLine($"[green]导入完成：{result.FilesRead} 个文件，跳过 {result.FilesSkipped} 个，索引 {result.ChunksIndexed} 个片段。[/]");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]创建项目失败：{Markup.Escape(ex.Message)}[/]");
    }
}

static async Task SwitchProjectFromArgs(IServiceProvider sp, string[] args)
{
    await SwitchProjectFromText(sp, string.Join(' ', args));
}

static async Task DeleteProjectFromArgs(IServiceProvider sp, string[] args)
{
    await DeleteProjectFromText(sp, string.Join(' ', args));
}

static async Task SwitchProjectFromText(IServiceProvider sp, string text)
{
    var project = text.Trim();
    if (string.IsNullOrWhiteSpace(project))
    {
        AnsiConsole.MarkupLine("[yellow]用法：project switch <项目ID或名称>[/]");
        return;
    }

    try
    {
        var projects = sp.GetRequiredService<LearningProjectService>();
        await projects.SelectProjectAsync(project);
        sp.GetRequiredService<IConversationMemory>().Reload();
        var indexer = sp.GetRequiredService<KnowledgeIndexer>();
        var loaded = await indexer.LoadIfExistsAsync();
        var state = await projects.GetStateAsync();
        AnsiConsole.MarkupLine($"[green]已切换到项目：{Markup.Escape(state.CurrentProject.Name)}；当前对话：{Markup.Escape(state.CurrentConversation.Title)}[/]");
        AnsiConsole.MarkupLine(loaded
            ? $"[dim]已加载项目索引，chunks={indexer.Count}[/]"
            : "[yellow]当前项目尚未建立索引，可运行 `index` 或 `:import <目录>`。[/]");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]切换项目失败：{Markup.Escape(ex.Message)}[/]");
    }
}

static async Task DeleteProjectFromText(IServiceProvider sp, string text)
{
    var project = text.Trim();
    if (string.IsNullOrWhiteSpace(project))
    {
        AnsiConsole.MarkupLine("[yellow]用法：project delete <项目ID或名称>[/]");
        return;
    }

    try
    {
        var projects = sp.GetRequiredService<LearningProjectService>();
        var deleted = await projects.DeleteProjectAsync(project);
        sp.GetRequiredService<IConversationMemory>().Reload();
        var state = await projects.GetStateAsync();
        AnsiConsole.MarkupLine($"[green]已删除项目：{Markup.Escape(deleted.Name)} ({Markup.Escape(deleted.Id)})[/]");
        AnsiConsole.MarkupLine($"[dim]当前项目：{Markup.Escape(state.CurrentProject.Name)}；当前对话：{Markup.Escape(state.CurrentConversation.Title)}[/]");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]删除项目失败：{Markup.Escape(ex.Message)}[/]");
    }
}

static async Task CreateConversationFromArgs(IServiceProvider sp, string[] args)
{
    await CreateConversationFromText(sp, string.Join(' ', args));
}

static async Task CreateConversationFromText(IServiceProvider sp, string text)
{
    var title = text.Trim();
    var projects = sp.GetRequiredService<LearningProjectService>();
    var conversation = await projects.CreateConversationAsync(string.IsNullOrWhiteSpace(title) ? null : title);
    sp.GetRequiredService<IConversationMemory>().Reload();
    AnsiConsole.MarkupLine($"[green]已新建并切换到对话：{Markup.Escape(conversation.Title)} ({Markup.Escape(conversation.Id)})[/]");
}

static async Task SwitchConversationFromArgs(IServiceProvider sp, string[] args)
{
    await SwitchConversationFromText(sp, string.Join(' ', args));
}

static async Task DeleteConversationFromArgs(IServiceProvider sp, string[] args)
{
    await DeleteConversationFromText(sp, string.Join(' ', args));
}

static async Task SwitchConversationFromText(IServiceProvider sp, string text)
{
    var conversation = text.Trim();
    if (string.IsNullOrWhiteSpace(conversation))
    {
        AnsiConsole.MarkupLine("[yellow]用法：conversation switch <对话ID或标题>[/]");
        return;
    }

    try
    {
        var projects = sp.GetRequiredService<LearningProjectService>();
        await projects.SelectConversationAsync(conversation);
        sp.GetRequiredService<IConversationMemory>().Reload();
        var state = await projects.GetStateAsync();
        AnsiConsole.MarkupLine($"[green]已切换到对话：{Markup.Escape(state.CurrentConversation.Title)}[/]");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]切换对话失败：{Markup.Escape(ex.Message)}[/]");
    }
}

static async Task DeleteConversationFromText(IServiceProvider sp, string text)
{
    var conversation = text.Trim();
    if (string.IsNullOrWhiteSpace(conversation))
    {
        AnsiConsole.MarkupLine("[yellow]用法：conversation delete <对话ID或标题>[/]");
        return;
    }

    try
    {
        var projects = sp.GetRequiredService<LearningProjectService>();
        var deleted = await projects.DeleteConversationAsync(conversation);
        sp.GetRequiredService<IConversationMemory>().Reload();
        var state = await projects.GetStateAsync();
        AnsiConsole.MarkupLine($"[green]已删除对话：{Markup.Escape(deleted.Title)} ({Markup.Escape(deleted.Id)})[/]");
        AnsiConsole.MarkupLine($"[dim]当前对话：{Markup.Escape(state.CurrentConversation.Title)}[/]");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]删除对话失败：{Markup.Escape(ex.Message)}[/]");
    }
}

static (string Directory, string? Name) ParseProjectCreateText(string text)
{
    var parts = text.Split('|', 2, StringSplitOptions.TrimEntries);
    var directory = parts.Length > 0 ? parts[0].Trim().Trim('"') : "";
    var name = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1].Trim() : null;
    return (directory, name);
}

static (string ToolName, string ArgumentsJson)? ToolCommand(string toolName, string argumentsJson) =>
    (toolName, argumentsJson);

static string BuildReadArgs(string rest)
{
    var fileName = rest;
    int? startPage = null;
    int? endPage = null;

    var lastSpace = rest.LastIndexOf(' ');
    if (lastSpace > 0 && TryParsePageRange(rest[(lastSpace + 1)..].Trim(), out startPage, out endPage))
    {
        fileName = rest[..lastSpace].Trim();
    }

    return JsonSerializer.Serialize(new { fileName, startPage, endPage });
}

static bool TryParsePageRange(string value, out int? startPage, out int? endPage)
{
    startPage = null;
    endPage = null;

    var rangeParts = value.Split('-', 2, StringSplitOptions.TrimEntries);
    if (!int.TryParse(rangeParts[0], out var start)) return false;

    startPage = start;
    if (rangeParts.Length > 1 && int.TryParse(rangeParts[1], out var end)) endPage = end;
    return true;
}

static string BuildNoteArgs(string rest)
{
    var parts = rest.Split('|', StringSplitOptions.TrimEntries);
    var title = parts.Length > 0 ? parts[0] : "未命名笔记";
    var content = parts.Length > 1 ? parts[1] : rest;
    var tags = parts.Length > 2
        ? parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : Array.Empty<string>();
    return JsonSerializer.Serialize(new { title, content, tags });
}

static string BuildNotesArgs(string rest)
{
    if (string.IsNullOrWhiteSpace(rest)) return "{}";
    return rest.StartsWith("#", StringComparison.Ordinal)
        ? JsonSerializer.Serialize(new { tag = rest[1..] })
        : JsonSerializer.Serialize(new { keyword = rest });
}

static string BuildQuizArgs(string rest)
{
    var parts = rest.Split('|', StringSplitOptions.TrimEntries);
    var material = parts.Length > 0 ? parts[0] : "";
    var count = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 3;
    return JsonSerializer.Serialize(new { material, count });
}

static string BuildTaskArgs(string rest)
{
    var parts = rest.Split('|', StringSplitOptions.TrimEntries);
    var title = parts.Length > 0 ? parts[0] : "";
    var topic = parts.Length > 1 ? parts[1] : "";
    var minutes = parts.Length > 2 && int.TryParse(parts[2], out var n) ? n : 30;
    return JsonSerializer.Serialize(new { title, topic, minutes });
}

static string BuildDoneArgs(string rest)
{
    var parts = rest.Split('|', StringSplitOptions.TrimEntries);
    var task = parts.Length > 0 ? parts[0] : "";
    var reflection = parts.Length > 1 ? parts[1] : "";
    int? actualMinutes = parts.Length > 2 && int.TryParse(parts[2], out var n) ? n : null;
    return JsonSerializer.Serialize(new { task, reflection, actualMinutes });
}

static string BuildHistoryArgs(string rest)
{
    var limit = int.TryParse(rest, out var n) ? n : 10;
    return JsonSerializer.Serialize(new { limit });
}

static string BuildMistakeArgs(string rest)
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

static string BuildAnswerArgs(string rest)
{
    var parts = rest.Split('|', StringSplitOptions.TrimEntries);
    var quizId = parts.Length > 0 ? parts[0] : "latest";
    var questionNumber = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 1;
    var answer = parts.Length > 2 ? parts[2] : "";
    var topic = parts.Length > 3 ? parts[3] : "";
    return JsonSerializer.Serialize(new { quizId, questionNumber, answer, topic });
}

static string BuildMistakesArgs(string rest)
{
    return string.IsNullOrWhiteSpace(rest)
        ? "{}"
        : JsonSerializer.Serialize(new { topic = rest });
}

static string BuildImportArgs(string rest)
{
    var parts = rest.Split('|', StringSplitOptions.TrimEntries);
    var path = parts.Length > 0 ? parts[0] : "";
    var glob = parts.Length > 1 ? parts[1] : null;
    return JsonSerializer.Serialize(new { path, glob });
}

static bool TryReadMultiAgentCommand(string input, out string goal)
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

static bool TryReadPlanExecuteCommand(string input, out string goal)
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
