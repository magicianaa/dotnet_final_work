using Microsoft.Extensions.Options;
using SmartStudy.Core.Agent;
using SmartStudy.Core.Configuration;
using SmartStudy.Core.Llm;
using SmartStudy.Core.Memory;
using SmartStudy.Core.Rag;
using SmartStudy.Core.Tools;
using SmartStudy.Core.Tools.Builtin;
using SmartStudy.Core.Tracing;

namespace SmartStudy.Web.Services;

public static class SmartStudyWebHostExtensions
{
    public static IServiceCollection AddSmartStudyAgent(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AgentOptions>(configuration.GetSection("Agent"));
        services.AddSingleton<LlmProfileManager>();

        services.AddHttpClient<ILlmClient, OpenAiLlmClient>();
        services.AddHttpClient<ZhipuEmbeddingClient>();
        services.AddSingleton<LocalHashEmbeddingClient>();
        services.AddSingleton<IEmbeddingClient>(sp =>
        {
            var provider = sp.GetRequiredService<IOptions<AgentOptions>>().Value.Embedding.Provider;
            return provider.Equals("local", StringComparison.OrdinalIgnoreCase)
                ? sp.GetRequiredService<LocalHashEmbeddingClient>()
                : sp.GetRequiredService<ZhipuEmbeddingClient>();
        });

        services.AddSingleton<IVectorStore, InMemoryVectorStore>();
        services.AddSingleton<KnowledgeIndexer>();
        services.AddSingleton<KnowledgeSearchService>();
        services.AddSingleton<CourseMaterialCatalog>();
        services.AddSingleton<CourseMaterialImporter>();
        services.AddSingleton<INoteStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
            var notePath = Path.Combine(Path.GetDirectoryName(opts.Rag.IndexFile) ?? "data", "notes.json");
            return new JsonNoteStore(notePath);
        });
        services.AddSingleton<ILearningProfileStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
            var profilePath = Path.Combine(Path.GetDirectoryName(opts.Rag.IndexFile) ?? "data", "learning-profile.json");
            return new JsonLearningProfileStore(profilePath);
        });
        services.AddSingleton<IStudyProgressStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
            var progressPath = Path.Combine(Path.GetDirectoryName(opts.Rag.IndexFile) ?? "data", "study-progress.json");
            return new JsonStudyProgressStore(progressPath);
        });
        services.AddSingleton<IQuizResultStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
            var quizPath = Path.Combine(Path.GetDirectoryName(opts.Rag.IndexFile) ?? "data", "quiz-results.json");
            return new JsonQuizResultStore(quizPath);
        });
        services.AddSingleton<IQuizSessionStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
            var quizPath = Path.Combine(Path.GetDirectoryName(opts.Rag.IndexFile) ?? "data", "quiz-sessions.json");
            return new JsonQuizSessionStore(quizPath);
        });
        services.AddScoped<IConversationMemory>(_ => new ConversationMemory(maxNonSystemMessages: 40));

        services.AddScoped<ITool, KnowledgeSearchTool>();
        services.AddScoped<ITool, ReadCourseMaterialTool>();
        services.AddScoped<ITool, ImportCourseMaterialsTool>();
        services.AddScoped<ITool, AddNoteTool>();
        services.AddScoped<ITool, ListNotesTool>();
        services.AddScoped<ITool, UpdateLearningProfileTool>();
        services.AddScoped<ITool, ShowLearningProfileTool>();
        services.AddScoped<ITool, StudyPlanTool>();
        services.AddScoped<ITool, AddStudyTaskTool>();
        services.AddScoped<ITool, MarkTaskDoneTool>();
        services.AddScoped<ITool, ShowProgressTool>();
        services.AddScoped<ITool, ReviewHistoryTool>();
        services.AddScoped<ITool, RecordQuizResultTool>();
        services.AddScoped<ITool, SubmitQuizAnswerTool>();
        services.AddScoped<ITool, ShowMistakesTool>();
        services.AddScoped<ITool, CalculatorTool>();
        services.AddScoped<ITool, MakeQuizTool>();
        services.AddScoped<ToolRegistry>();
        services.AddScoped<AnswerQualityReviewer>();
        services.AddScoped<PlanExecuteAgent>();

        services.AddScoped<WebAgentTraceStore>();
        services.AddScoped<IAgentTracer, WebAgentTracer>();
        services.AddScoped<ReActAgent>();
        services.AddScoped<DashboardStateService>();
        services.AddHostedService<RagWarmupService>();

        return services;
    }
}

public sealed class RagWarmupService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly IOptions<AgentOptions> _options;
    private readonly ILogger<RagWarmupService> _logger;

    public RagWarmupService(IServiceProvider services, IOptions<AgentOptions> options, ILogger<RagWarmupService> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var indexer = scope.ServiceProvider.GetRequiredService<KnowledgeIndexer>();
        if (await indexer.LoadIfExistsAsync(cancellationToken))
        {
            _logger.LogInformation("Loaded SmartStudy RAG index, chunks={Count}", indexer.Count);
            return;
        }

        if (!_options.Value.Embedding.Provider.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("SmartStudy RAG index was not found. Build it from the web dashboard or CLI before knowledge search.");
            return;
        }

        try
        {
            _logger.LogInformation("SmartStudy RAG index was not found. Building a local index for the web dashboard.");
            await indexer.BuildAsync(cancellationToken);
            await indexer.LoadIfExistsAsync(cancellationToken);
            _logger.LogInformation("Built SmartStudy RAG index, chunks={Count}", indexer.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build the SmartStudy RAG index during web startup.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
