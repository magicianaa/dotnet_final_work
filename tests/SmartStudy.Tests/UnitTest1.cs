using System.Text.Json;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartStudy.Core.Agent;
using SmartStudy.Core.Configuration;
using SmartStudy.Core.Llm;
using SmartStudy.Core.Memory;
using SmartStudy.Core.Rag;
using SmartStudy.Core.Tools;
using SmartStudy.Core.Tools.Builtin;
using SmartStudy.Core.Tracing;
using SmartStudy.Cli;

namespace SmartStudy.Tests;

/// <summary>用一个可脚本化的假 LLM 验证 ReAct 循环、工具调用、Memory、Tracing。</summary>
public sealed class FakeLlmClient : ILlmClient
{
    private readonly Queue<ChatResponse> _responses;
    public List<ChatRequest> Requests { get; } = new();
    public FakeLlmClient(IEnumerable<ChatResponse> scripted) => _responses = new(scripted);

    public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(_responses.Dequeue());
    }

    public async IAsyncEnumerable<ChatStreamChunk> ChatStreamAsync(ChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var r = await ChatAsync(request, ct);
        if (!string.IsNullOrEmpty(r.Content)) yield return new ChatStreamChunk { ContentDelta = r.Content };
        if (r.ToolCalls.Count > 0) yield return new ChatStreamChunk { ToolCallsAccumulated = r.ToolCalls };
        yield return new ChatStreamChunk { FinishReason = r.FinishReason };
    }
}

public class ReActAgentTests
{
    private static IOptions<AgentOptions> Opts(int max = 5) =>
        Options.Create(new AgentOptions { MaxLoopSteps = max });

    [Fact]
    public async Task DirectAnswer_NoToolCall_Returns()
    {
        var llm = new FakeLlmClient(new[]
        {
            new ChatResponse { Content = "你好", FinishReason = "stop" }
        });
        var registry = new ToolRegistry(Array.Empty<ITool>());
        var memory = new ConversationMemory();
        var agent = new ReActAgent(llm, registry, memory, new NullTracer(), Opts(),
            NullLogger<ReActAgent>.Instance);

        var result = await agent.RunAsync("hi");
        Assert.Equal("你好", result.FinalAnswer);
        Assert.Equal(1, result.Steps);
        Assert.False(result.ReachedLimit);
    }

    [Fact]
    public async Task ToolCall_ExecutesAndFeedsObservation()
    {
        var llm = new FakeLlmClient(new[]
        {
            new ChatResponse
            {
                FinishReason = "tool_calls",
                ToolCalls = new()
                {
                    new ToolCall { Id = "c1", Function = new ToolCallFunction { Name = "calculate", Arguments = "{\"expression\":\"2+3\"}" } }
                }
            },
            new ChatResponse { Content = "答案是 5", FinishReason = "stop" }
        });
        var registry = new ToolRegistry(new ITool[] { new CalculatorTool() });
        var memory = new ConversationMemory();
        var agent = new ReActAgent(llm, registry, memory, new NullTracer(), Opts(),
            NullLogger<ReActAgent>.Instance);

        var result = await agent.RunAsync("2+3 等于多少？");
        Assert.Equal("答案是 5", result.FinalAnswer);
        // 应有 4 条消息：system + user + assistant(tool_calls) + tool + assistant(final)
        Assert.Contains(memory.Messages, m => m.Role == "tool" && m.Content!.Contains("= 5"));
    }

    [Fact]
    public async Task ReachesLimit_WhenLlmKeepsCallingTools()
    {
        var loop = new ChatResponse
        {
            FinishReason = "tool_calls",
            ToolCalls = new() { new ToolCall { Id = "x", Function = new() { Name = "calculate", Arguments = "{\"expression\":\"1+1\"}" } } }
        };
        var llm = new FakeLlmClient(Enumerable.Repeat(loop, 3));
        var registry = new ToolRegistry(new ITool[] { new CalculatorTool() });
        var memory = new ConversationMemory();
        var agent = new ReActAgent(llm, registry, memory, new NullTracer(), Opts(max: 3),
            NullLogger<ReActAgent>.Instance);
        var r = await agent.RunAsync("never end");
        Assert.True(r.ReachedLimit);
    }
}

public class CalculatorToolTests
{
    [Theory]
    [InlineData("1+2", "3")]
    [InlineData("(3+4)*2", "14")]
    public async Task ComputesBasicExpressions(string expr, string expected)
    {
        var t = new CalculatorTool();
        var arg = JsonDocument.Parse($"{{\"expression\":\"{expr}\"}}").RootElement;
        var r = await t.InvokeAsync(arg);
        Assert.Contains($"= {expected}", r);
    }

    [Fact]
    public async Task ReturnsErrorMessageOnInvalid()
    {
        var t = new CalculatorTool();
        var arg = JsonDocument.Parse("{\"expression\":\"oops\"}").RootElement;
        var r = await t.InvokeAsync(arg);
        Assert.StartsWith("无法计算", r);
    }
}

public class MakeQuizToolTests
{
    [Fact]
    public async Task NormalizesFencedJsonQuizOutput()
    {
        var llm = new FakeLlmClient(new[]
        {
            new ChatResponse
            {
                Content = """
                ```json
                [
                  {
                    "question": "ReAct 的 Action 是什么？",
                    "options": ["思考", "调用工具"],
                    "answer": "调用工具",
                    "explanation": "Action 阶段会调用外部工具。"
                  }
                ]
                ```
                """
            }
        });
        var tool = new MakeQuizTool(llm);
        var args = JsonDocument.Parse("""{"material":"ReAct 包含 Thought、Action、Observation。","count":1}""").RootElement;

        var result = await tool.InvokeAsync(args);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal("ReAct 的 Action 是什么？", doc.RootElement[0].GetProperty("question").GetString());
    }

    [Fact]
    public async Task RepairsInvalidQuizOutputOnce()
    {
        var llm = new FakeLlmClient(new[]
        {
            new ChatResponse { Content = "这是一段解释，不是 JSON。" },
            new ChatResponse
            {
                Content = """
                [
                  {
                    "question": "Observation 表示什么？",
                    "options": [],
                    "answer": "工具返回结果",
                    "explanation": "Agent 把工具结果写回上下文继续推理。"
                  }
                ]
                """
            }
        });
        var tool = new MakeQuizTool(llm);
        var args = JsonDocument.Parse("""{"material":"Observation 是工具返回结果。","count":1}""").RootElement;

        var result = await tool.InvokeAsync(args);

        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains("Observation 表示什么？", result);
    }

    [Fact]
    public async Task ReturnsReadableErrorWhenRepairFails()
    {
        var llm = new FakeLlmClient(new[]
        {
            new ChatResponse { Content = "bad" },
            new ChatResponse { Content = """[{"question":"缺字段"}]""" }
        });
        var tool = new MakeQuizTool(llm);
        var args = JsonDocument.Parse("""{"material":"任意材料","count":1}""").RootElement;

        var result = await tool.InvokeAsync(args);

        Assert.StartsWith("出题失败：LLM 未返回合法的练习题 JSON", result);
        Assert.Equal(2, llm.Requests.Count);
    }
}

public class ConversationMemoryTests
{
    [Fact]
    public void SystemPromptIsAlwaysKeptFirstAndUnique()
    {
        var m = new ConversationMemory();
        m.AddSystem("A");
        m.AddUser("hi");
        m.AddSystem("B");
        Assert.Equal(2, m.Messages.Count);
        Assert.Equal("system", m.Messages[0].Role);
        Assert.Equal("B", m.Messages[0].Content);
    }

    [Fact]
    public void SlidingWindowDropsOldNonSystemMessages()
    {
        var m = new ConversationMemory(maxNonSystemMessages: 2);
        m.AddSystem("S");
        m.AddUser("1"); m.AddUser("2"); m.AddUser("3");
        Assert.Equal(3, m.Messages.Count); // system + 2
        Assert.Equal("2", m.Messages[1].Content);
    }
}

public class KnowledgeIndexerTests
{
    [Fact]
    public void Chunk_SplitsLongTextWithOverlap()
    {
        var text = string.Join("\n\n", Enumerable.Range(0, 20).Select(i => $"段落{i} " + new string('x', 50)));
        var chunks = KnowledgeIndexer.Chunk(text, chunkSize: 200, overlap: 30).ToList();
        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, c => Assert.True(c.Length > 0));
    }
}

public class InMemoryVectorStoreTests
{
    [Fact]
    public void Search_ReturnsTopKByCosineSimilarity()
    {
        var s = new InMemoryVectorStore();
        s.Replace(new[]
        {
            new KnowledgeChunk { Id="a", Text="A", Vector = new float[]{1,0,0} },
            new KnowledgeChunk { Id="b", Text="B", Vector = new float[]{0,1,0} },
            new KnowledgeChunk { Id="c", Text="C", Vector = new float[]{1,1,0} }
        });
        var r = s.Search(new float[] { 1, 0, 0 }, 2);
        Assert.Equal("a", r[0].Chunk.Id);
        Assert.True(r[0].Score > r[1].Score);
    }
}

public class LocalHashEmbeddingClientTests
{
    [Fact]
    public async Task LocalEmbedding_IsDeterministicAndUsesConfiguredDimensions()
    {
        var client = new LocalHashEmbeddingClient(Options.Create(new AgentOptions
        {
            Embedding = new EmbeddingOptions { Provider = "local", LocalDimensions = 128 }
        }));

        var a = await client.EmbedAsync("ReAct Agent 工具调用");
        var b = await client.EmbedAsync("ReAct Agent 工具调用");

        Assert.Equal(128, a.Length);
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task LocalEmbedding_GivesHigherScoreToRelatedText()
    {
        var client = new LocalHashEmbeddingClient(Options.Create(new AgentOptions
        {
            Embedding = new EmbeddingOptions { Provider = "local", LocalDimensions = 256 }
        }));
        var store = new InMemoryVectorStore();
        store.Replace(new[]
        {
            new KnowledgeChunk { Id = "react", Text = "ReAct Agent 使用工具调用和观察结果进行多步推理。", Vector = await client.EmbedAsync("ReAct Agent 使用工具调用和观察结果进行多步推理。") },
            new KnowledgeChunk { Id = "other", Text = "数据库索引通常用于加速表查询。", Vector = await client.EmbedAsync("数据库索引通常用于加速表查询。") }
        });

        var results = store.Search(await client.EmbedAsync("Agent 如何调用工具并观察结果"), 2);

        Assert.Equal("react", results[0].Chunk.Id);
    }
}

public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage _response;
    public StubHttpMessageHandler(HttpResponseMessage response) => _response = response;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(_response);
}

public class OpenAiLlmClientStreamTests
{
    [Fact]
    public async Task StreamToolCallArguments_StartEmptyAndDoNotPrefixDefaultJson()
    {
        var sse = string.Join("\n\n", new[]
        {
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"knowledge_search","arguments":"{\"query\""}}]}}]}""",
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":":\"ReAct\"}"}}]}}]}""",
            """data: [DONE]"""
        });
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse)
        };
        var http = new HttpClient(new StubHttpMessageHandler(resp))
        {
            BaseAddress = new Uri("https://example.test/")
        };
        var client = new OpenAiLlmClient(http, new LlmProfileManager(Options.Create(new AgentOptions())), NullLogger<OpenAiLlmClient>.Instance);

        List<ToolCall>? calls = null;
        await foreach (var chunk in client.ChatStreamAsync(new ChatRequest { Messages = new() }))
            if (chunk.ToolCallsAccumulated != null) calls = chunk.ToolCallsAccumulated;

        Assert.NotNull(calls);
        Assert.Equal("{\"query\":\"ReAct\"}", calls![0].Function.Arguments);
    }
}

public class EditableLineBufferTests
{
    [Fact]
    public void SupportsLeftRightInsertion()
    {
        var line = new EditableLineBuffer();
        foreach (var ch in "helo") line.Apply(LineEditKey.Character, ch);
        line.Apply(LineEditKey.Left);
        line.Apply(LineEditKey.Left);
        line.Apply(LineEditKey.Character, 'l');

        Assert.Equal("hello", line.Text);
        Assert.Equal(3, line.Cursor);
    }

    [Fact]
    public void SupportsHomeEndBackspaceAndDelete()
    {
        var line = new EditableLineBuffer();
        foreach (var ch in "abcd") line.Apply(LineEditKey.Character, ch);
        line.Apply(LineEditKey.Home);
        line.Apply(LineEditKey.Delete);
        line.Apply(LineEditKey.End);
        line.Apply(LineEditKey.Backspace);

        Assert.Equal("bc", line.Text);
        Assert.Equal(2, line.Cursor);
    }

    [Fact]
    public void MeasuresChineseCharactersAsWideCells()
    {
        Assert.Equal(4, ConsoleLineEditor.MeasureCellWidth("中文"));
        Assert.Equal(5, ConsoleLineEditor.MeasureCellWidth("A中文"));
    }

    [Fact]
    public void CursorIndexStillMovesByLogicalCharacter()
    {
        var line = new EditableLineBuffer();
        foreach (var ch in "中文a") line.Apply(LineEditKey.Character, ch);

        line.Apply(LineEditKey.Left);
        line.Apply(LineEditKey.Left);

        Assert.Equal(1, line.Cursor);
        Assert.Equal(2, ConsoleLineEditor.MeasureCellWidth(line.Text[..line.Cursor]));
    }
}

public class ReadCourseMaterialToolTests
{
    [Fact]
    public async Task ReadsSpecificMaterialPagesByFileName()
    {
        var root = Path.Combine(Path.GetTempPath(), "smartstudy-material-test-" + Guid.NewGuid().ToString("N"));
        var knowledge = Path.Combine(root, "knowledge");
        Directory.CreateDirectory(Path.Combine(knowledge, "imported"));
        var material = Path.Combine(knowledge, "imported", "2026_Slides Lesson00_Introduction to SEME.md");
        await File.WriteAllTextAsync(material, """
        # 2026_Slides Lesson00_Introduction to SEME

        Source: C:\Course\2026_Slides Lesson00_Introduction to SEME.pdf

        ## Page 1

        Title page

        ## Page 2

        Content and objectives

        ## Page 3

        Course project requirements
        """);

        try
        {
            var store = new InMemoryVectorStore();
            store.Replace(new[]
            {
                new KnowledgeChunk
                {
                    Id = "x",
                    Source = "2026_Slides Lesson00_Introduction to SEME.md",
                    Text = "Course project requirements",
                    Vector = new float[] { 1 }
                }
            });

            var tool = new ReadCourseMaterialTool(store, Options.Create(new AgentOptions
            {
                Rag = new RagOptions { KnowledgeDirectory = knowledge }
            }));
            var args = JsonDocument.Parse("""
            {
              "fileName": "2026_Slides Lesson00_Introduction to SEME.pdf",
              "startPage": 2,
              "endPage": 3
            }
            """).RootElement;

            var result = await tool.InvokeAsync(args);

            Assert.Contains("Page 2", result);
            Assert.Contains("Content and objectives", result);
            Assert.Contains("Course project requirements", result);
            Assert.DoesNotContain("Title page", result);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

public class SmartStudyDoctorTests
{
    [Fact]
    public async Task DoctorReportsCoreProjectState()
    {
        var root = Path.Combine(Path.GetTempPath(), "smartstudy-doctor-test-" + Guid.NewGuid().ToString("N"));
        var knowledge = Path.Combine(root, "knowledge");
        var imported = Path.Combine(knowledge, "imported");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(imported);
        Directory.CreateDirectory(data);
        await File.WriteAllTextAsync(Path.Combine(knowledge, "base.md"), "# Base");
        await File.WriteAllTextAsync(Path.Combine(imported, "lesson.md"), "# Lesson");
        await File.WriteAllTextAsync(Path.Combine(data, "notes.json"), "[]");

        try
        {
            var store = new InMemoryVectorStore();
            store.Replace(new[]
            {
                new KnowledgeChunk { Id = "c1", Source = "base.md", Text = "ReAct", Vector = new float[] { 1 } }
            });
            await store.SaveAsync(Path.Combine(data, "index.json"));

            var options = Options.Create(new AgentOptions
            {
                ActiveLlmProfile = "local-test",
                LlmProfiles = new Dictionary<string, LlmOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["local-test"] = new LlmOptions
                    {
                        BaseUrl = "https://example.test",
                        ApiKey = "test-key",
                        Model = "test-model"
                    }
                },
                Embedding = new EmbeddingOptions { Provider = "local", LocalDimensions = 128, Model = "local-hash" },
                Rag = new RagOptions
                {
                    KnowledgeDirectory = knowledge,
                    IndexFile = Path.Combine(data, "index.json")
                }
            });
            var search = new KnowledgeSearchService(new LocalHashEmbeddingClient(options), store, options);
            var tools = new ToolRegistry(new ITool[] { new CalculatorTool(), new KnowledgeSearchTool(search), new ListNotesTool(new JsonNoteStore(Path.Combine(data, "notes.json"))) });
            var doctor = new SmartStudyDoctor(options, new LlmProfileManager(options), store, tools);

            var snapshot = await doctor.InspectAsync();

            Assert.True(snapshot.IsHealthy);
            Assert.Equal("local-test", snapshot.CurrentLlmProfile);
            Assert.Equal("test-model", snapshot.CurrentLlmModel);
            Assert.Equal(2, snapshot.MarkdownFileCount);
            Assert.Equal(1, snapshot.ImportedMaterialCount);
            Assert.Equal(1, snapshot.LoadedChunkCount);
            Assert.Equal(3, snapshot.Tools.Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

public class KnowledgeSearchServiceTests
{
    [Fact]
    public async Task SearchServiceFormatsRankedResults()
    {
        var options = Options.Create(new AgentOptions
        {
            Embedding = new EmbeddingOptions { Provider = "local", LocalDimensions = 128 },
            Rag = new RagOptions { TopK = 2 }
        });
        var embed = new LocalHashEmbeddingClient(options);
        var store = new InMemoryVectorStore();
        var related = "ReAct Agent 通过 Thought Action Observation 循环调用工具。";
        var unrelated = "数据库事务关注提交和回滚。";
        store.Replace(new[]
        {
            new KnowledgeChunk { Id = "r", Source = "react.md", Text = related, Vector = await embed.EmbedAsync(related) },
            new KnowledgeChunk { Id = "d", Source = "db.md", Text = unrelated, Vector = await embed.EmbedAsync(unrelated) }
        });

        var service = new KnowledgeSearchService(embed, store, options);
        var result = await service.SearchAsync("ReAct 如何调用工具", 1);

        Assert.Contains("检索到 1 段相关内容", result);
        Assert.Contains("react.md", result);
        Assert.Contains("Thought Action Observation", result);
    }
}

public class CourseMaterialCatalogTests
{
    [Fact]
    public async Task ListsImportedMarkdownMaterials()
    {
        var root = Path.Combine(Path.GetTempPath(), "smartstudy-catalog-test-" + Guid.NewGuid().ToString("N"));
        var imported = Path.Combine(root, "knowledge", "imported");
        Directory.CreateDirectory(imported);
        await File.WriteAllTextAsync(Path.Combine(imported, "lesson01.md"), "# Lesson01");

        try
        {
            var catalog = new CourseMaterialCatalog(Options.Create(new AgentOptions
            {
                Rag = new RagOptions { KnowledgeDirectory = Path.Combine(root, "knowledge") }
            }));

            var result = catalog.ListImportedMaterials();

            Assert.Single(result);
            Assert.Equal("lesson01.md", result[0].FileName);
            Assert.Equal(Path.Combine("imported", "lesson01.md"), result[0].RelativePath);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

public class KnowledgeMcpToolsTests
{
    [Fact]
    public async Task MpcKnowledgeToolsSearchAndListMaterials()
    {
        var root = Path.Combine(Path.GetTempPath(), "smartstudy-mcp-tools-test-" + Guid.NewGuid().ToString("N"));
        var knowledge = Path.Combine(root, "knowledge");
        var imported = Path.Combine(knowledge, "imported");
        Directory.CreateDirectory(imported);
        await File.WriteAllTextAsync(Path.Combine(imported, "lesson-react.md"), "# Lesson React");

        try
        {
            var options = Options.Create(new AgentOptions
            {
                Embedding = new EmbeddingOptions { Provider = "local", LocalDimensions = 128 },
                Rag = new RagOptions { KnowledgeDirectory = knowledge, TopK = 2 }
            });
            var embed = new LocalHashEmbeddingClient(options);
            var store = new InMemoryVectorStore();
            var text = "ReAct Agent 会在 Action 阶段调用工具，并观察 Observation。";
            store.Replace(new[]
            {
                new KnowledgeChunk { Id = "react", Source = "lesson-react.md", Text = text, Vector = await embed.EmbedAsync(text) }
            });

            var search = new KnowledgeSearchService(embed, store, options);
            var catalog = new CourseMaterialCatalog(options);
            var tools = new KnowledgeMcpTools(search, catalog);

            var searchResult = await tools.SearchKnowledge("ReAct 工具调用", topK: 1);
            var materials = tools.ListImportedMaterials();

            Assert.Contains("lesson-react.md", searchResult);
            Assert.Contains("Action 阶段调用工具", searchResult);
            Assert.Contains("lesson-react.md", materials);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

public class MultiAgentOrchestratorTests
{
    [Fact]
    public async Task MultiAgentWorkflowProducesPlanResearchTutorAndReviewSteps()
    {
        var root = Path.Combine(Path.GetTempPath(), "smartstudy-multi-agent-test-" + Guid.NewGuid().ToString("N"));
        var profilePath = Path.Combine(root, "learning-profile.json");

        try
        {
            var options = Options.Create(new AgentOptions
            {
                Embedding = new EmbeddingOptions { Provider = "local", LocalDimensions = 128 },
                Rag = new RagOptions { TopK = 3 }
            });
            var embed = new LocalHashEmbeddingClient(options);
            var store = new InMemoryVectorStore();
            var text = "ReAct Agent 由 LLM、Agent Loop、Memory 和 Tools 组成，会通过 Thought Action Observation 循环调用工具。";
            store.Replace(new[]
            {
                new KnowledgeChunk
                {
                    Id = "react",
                    Source = "react-agent.md",
                    Text = text,
                    Vector = await embed.EmbedAsync(text)
                }
            });

            var profileStore = new JsonLearningProfileStore(profilePath);
            await profileStore.UpdateAsync(new LearningProfileUpdate
            {
                WeakTopics = new() { "ReAct" },
                Goals = new() { "准备期末答辩" },
                PreferredStyle = "先讲概念再映射代码"
            });

            var orchestrator = new MultiAgentOrchestrator(
                new KnowledgeSearchService(embed, store, options),
                profileStore);

            var result = await orchestrator.RunAsync("解释 ReAct Agent 并准备答辩");

            Assert.True(result.PassedReview);
            Assert.Equal(new[] { "PlannerAgent", "ResearchAgent", "TutorAgent", "ReviewerAgent" },
                result.Steps.Select(s => s.AgentName).ToArray());
            Assert.Contains("react-agent.md", result.Steps[1].Output);
            Assert.Contains("准备期末答辩", result.FinalAnswer);
            Assert.Contains("核心概念解释", result.FinalAnswer);
            Assert.Contains("和本项目代码的对应关系", result.FinalAnswer);
            Assert.Contains("验收命令", result.FinalAnswer);
            Assert.Contains("下一步", result.FinalAnswer);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MultiAgentReviewWarnsWhenKnowledgeIndexIsMissing()
    {
        var options = Options.Create(new AgentOptions
        {
            Embedding = new EmbeddingOptions { Provider = "local", LocalDimensions = 128 },
            Rag = new RagOptions { TopK = 3 }
        });
        var profilePath = Path.Combine(Path.GetTempPath(), "smartstudy-empty-profile-" + Guid.NewGuid().ToString("N") + ".json");
        var orchestrator = new MultiAgentOrchestrator(
            new KnowledgeSearchService(new LocalHashEmbeddingClient(options), new InMemoryVectorStore(), options),
            new JsonLearningProfileStore(profilePath));

        var result = await orchestrator.RunAsync("解释 MCP");

        Assert.False(result.PassedReview);
        Assert.Contains("ResearchAgent", result.Steps.Select(s => s.AgentName));
        Assert.Contains("ReviewerAgent 修正建议", result.FinalAnswer);
    }

    [Fact]
    public async Task MultiAgentUsesExplanationModeForGeneralQuestion()
    {
        var options = Options.Create(new AgentOptions
        {
            Embedding = new EmbeddingOptions { Provider = "local", LocalDimensions = 128 },
            Rag = new RagOptions { TopK = 2 }
        });
        var embed = new LocalHashEmbeddingClient(options);
        var store = new InMemoryVectorStore();
        var text = "ReAct Agent 会结合 reasoning 和 acting，通过工具调用处理复杂任务。";
        store.Replace(new[]
        {
            new KnowledgeChunk { Id = "react", Source = "react.md", Text = text, Vector = await embed.EmbedAsync(text) }
        });

        var orchestrator = new MultiAgentOrchestrator(
            new KnowledgeSearchService(embed, store, options),
            new JsonLearningProfileStore(Path.Combine(Path.GetTempPath(), "smartstudy-general-profile-" + Guid.NewGuid().ToString("N") + ".json")));

        var result = await orchestrator.RunAsync("解释 ReAct Agent");

        Assert.Contains("知识讲解版", result.FinalAnswer);
        Assert.DoesNotContain("答辩版讲解", result.FinalAnswer);
        Assert.Contains("为什么它有用", result.FinalAnswer);
    }

    [Fact]
    public async Task MultiAgentUsesStudyPlanModeForReviewGoal()
    {
        var options = Options.Create(new AgentOptions
        {
            Embedding = new EmbeddingOptions { Provider = "local", LocalDimensions = 128 },
            Rag = new RagOptions { TopK = 2 }
        });
        var embed = new LocalHashEmbeddingClient(options);
        var store = new InMemoryVectorStore();
        var text = "MCP 可以让外部 Host 通过标准协议调用工具。";
        store.Replace(new[]
        {
            new KnowledgeChunk { Id = "mcp", Source = "mcp.md", Text = text, Vector = await embed.EmbedAsync(text) }
        });

        var orchestrator = new MultiAgentOrchestrator(
            new KnowledgeSearchService(embed, store, options),
            new JsonLearningProfileStore(Path.Combine(Path.GetTempPath(), "smartstudy-study-profile-" + Guid.NewGuid().ToString("N") + ".json")));

        var result = await orchestrator.RunAsync("复习 MCP 和 RAG");

        Assert.Contains("学习建议版", result.FinalAnswer);
        Assert.DoesNotContain("答辩版讲解", result.FinalAnswer);
        Assert.Contains("建议学习路径", result.FinalAnswer);
    }
}

public class LearningProfileTests
{
    [Fact]
    public async Task ProfileStoreMergesAndPersistsLearningProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), "smartstudy-profile-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "data", "learning-profile.json");

        try
        {
            var store = new JsonLearningProfileStore(path);
            await store.UpdateAsync(new LearningProfileUpdate
            {
                WeakTopics = new() { "ReAct", "RAG" },
                Goals = new() { "准备期末答辩" },
                PreferredStyle = "先讲概念再举例"
            });
            await store.UpdateAsync(new LearningProfileUpdate
            {
                WeakTopics = new() { "react", "MCP" },
                StrongTopics = new() { "C#" }
            });

            var profile = await new JsonLearningProfileStore(path).GetAsync();

            Assert.Equal(new[] { "ReAct", "RAG", "MCP" }, profile.WeakTopics);
            Assert.Contains("C#", profile.StrongTopics);
            Assert.Contains("准备期末答辩", profile.Goals);
            Assert.Equal("先讲概念再举例", profile.PreferredStyle);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProfileToolsUpdateAndShowReadableSummary()
    {
        var root = Path.Combine(Path.GetTempPath(), "smartstudy-profile-tool-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "learning-profile.json");

        try
        {
            var store = new JsonLearningProfileStore(path);
            var update = new UpdateLearningProfileTool(store);
            var show = new ShowLearningProfileTool(store);
            var args = JsonDocument.Parse("""
            {
              "weakTopics": ["Agent Loop"],
              "strongTopics": ["C#"],
              "goals": ["完成期末项目"],
              "preferredStyle": "按项目场景解释"
            }
            """).RootElement;

            var updateResult = await update.InvokeAsync(args);
            var showResult = await show.InvokeAsync(JsonDocument.Parse("{}").RootElement);

            Assert.Contains("学习画像已更新", updateResult);
            Assert.Contains("Agent Loop", showResult);
            Assert.Contains("完成期末项目", showResult);
            Assert.Contains("按项目场景解释", showResult);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateProfileToolCompletesStrongTopicsFromLatestUserMessage()
    {
        var root = Path.Combine(Path.GetTempPath(), "smartstudy-profile-enrich-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "learning-profile.json");

        try
        {
            var memory = new ConversationMemory();
            memory.AddUser("我已经掌握 calculate 和 read_course_material，但仍需要加强 make_quiz。请更新我的学习画像。");
            var store = new JsonLearningProfileStore(path);
            var update = new UpdateLearningProfileTool(store, memory);
            var args = JsonDocument.Parse("""{"weakTopics":["make_quiz"]}""").RootElement;

            await update.InvokeAsync(args);
            var profile = await store.GetAsync();

            Assert.Contains("calculate", profile.StrongTopics);
            Assert.Contains("read_course_material", profile.StrongTopics);
            Assert.Contains("make_quiz", profile.WeakTopics);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StudyPlanUsesProfileTopicsAndKnowledgeSearchEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "smartstudy-study-plan-test-" + Guid.NewGuid().ToString("N"));
        var profilePath = Path.Combine(root, "learning-profile.json");

        try
        {
            var options = Options.Create(new AgentOptions
            {
                Embedding = new EmbeddingOptions { Provider = "local", LocalDimensions = 128 },
                Rag = new RagOptions { TopK = 2 }
            });
            var embed = new LocalHashEmbeddingClient(options);
            var store = new InMemoryVectorStore();
            var text = "ReAct Agent 会通过 Thought、Action、Observation 循环完成工具调用。";
            store.Replace(new[]
            {
                new KnowledgeChunk { Id = "react", Source = "react.md", Text = text, Vector = await embed.EmbedAsync(text) }
            });

            var profileStore = new JsonLearningProfileStore(profilePath);
            await profileStore.UpdateAsync(new LearningProfileUpdate
            {
                WeakTopics = new() { "ReAct" },
                PreferredStyle = "按步骤讲解"
            });

            var tool = new StudyPlanTool(profileStore, new KnowledgeSearchService(embed, store, options));
            var args = JsonDocument.Parse("""{"goal":"准备 Agent 项目答辩","days":2,"minutesPerDay":30}""").RootElement;

            var result = await tool.InvokeAsync(args);

            Assert.Contains("准备 Agent 项目答辩", result);
            Assert.Contains("第 1 天", result);
            Assert.Contains("第 2 天", result);
            Assert.Contains("ReAct", result);
            Assert.Contains("react.md", result);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
