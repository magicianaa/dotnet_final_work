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
