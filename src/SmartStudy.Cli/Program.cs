using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartStudy.Cli;
using SmartStudy.Core.Agent;
using SmartStudy.Core.Configuration;
using SmartStudy.Core.Llm;
using SmartStudy.Core.Memory;
using SmartStudy.Core.Rag;
using SmartStudy.Core.Tools;
using SmartStudy.Core.Tools.Builtin;
using SmartStudy.Core.Tracing;
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

host.Services.AddSingleton<IVectorStore, InMemoryVectorStore>();
host.Services.AddSingleton<KnowledgeIndexer>();
host.Services.AddSingleton<CourseMaterialImporter>();
host.Services.AddSingleton<INoteStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
    var notePath = Path.Combine(Path.GetDirectoryName(opts.Rag.IndexFile) ?? "data", "notes.json");
    return new JsonNoteStore(notePath);
});

// 4 个工具
host.Services.AddSingleton<ITool, KnowledgeSearchTool>();
host.Services.AddSingleton<ITool, ReadCourseMaterialTool>();
host.Services.AddSingleton<ITool, ImportCourseMaterialsTool>();
host.Services.AddSingleton<ITool, AddNoteTool>();
host.Services.AddSingleton<ITool, ListNotesTool>();
host.Services.AddSingleton<ITool, CalculatorTool>();
host.Services.AddSingleton<ITool, MakeQuizTool>();   // 第 5 个，额外加分
host.Services.AddSingleton<ToolRegistry>();

host.Services.AddSingleton<IConversationMemory>(_ => new ConversationMemory(maxNonSystemMessages: 40));

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

    AnsiConsole.Write(new Rule("[bold cyan]SmartStudy AI 学习助手[/]").RuleStyle("grey"));
    AnsiConsole.MarkupLine($"[dim]输入问题与 Agent 对话；输入 :q 退出，:reset 清空记忆，:stream 切换流式，:models 查看模型，:model <name> 切换模型。当前 LLM: {Markup.Escape(profiles.CurrentName)} ({Markup.Escape(profiles.Current.Model)})[/]");

    while (true)
    {
        var input = ConsoleLineEditor.ReadLine("> ");
        if (string.IsNullOrWhiteSpace(input)) continue;
        if (input == ":q") break;
        if (input == ":reset") { sp.GetRequiredService<IConversationMemory>().Reset(); AnsiConsole.MarkupLine("[green]记忆已清空[/]"); continue; }
        if (input == ":stream") { useStreaming = !useStreaming; AnsiConsole.MarkupLine($"[green]流式输出 = {useStreaming}[/]"); continue; }
        if (input == ":models") { PrintModelProfiles(profiles); continue; }
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
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
        }
    }
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
