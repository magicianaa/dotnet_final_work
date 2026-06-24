using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartStudy.Core.Configuration;
using SmartStudy.Core.Llm;
using SmartStudy.Core.Memory;
using SmartStudy.Core.Tools;
using SmartStudy.Core.Tracing;

namespace SmartStudy.Core.Agent;

public sealed record AgentResult(string FinalAnswer, int Steps, bool ReachedLimit);

/// <summary>
/// ReAct 风格的 Agent 主循环。
/// 把 LLM 的 tool_calls 当作 “Action”，把工具返回当作 “Observation”，
/// 把 LLM 不再调用工具时的回答当作 “FinalAnswer”，循环直到完成或达到上限。
/// </summary>
public sealed class ReActAgent
{
    private readonly ILlmClient _llm;
    private readonly ToolRegistry _tools;
    private readonly IConversationMemory _memory;
    private readonly IAgentTracer _tracer;
    private readonly AgentOptions _opts;
    private readonly ILogger<ReActAgent> _logger;

    public ReActAgent(ILlmClient llm, ToolRegistry tools, IConversationMemory memory,
        IAgentTracer tracer, IOptions<AgentOptions> opts, ILogger<ReActAgent> logger)
    {
        _llm = llm; _tools = tools; _memory = memory; _tracer = tracer;
        _opts = opts.Value; _logger = logger;
        // 第一次构造时注入 system prompt
        if (_memory.Messages.All(m => m.Role != ChatRoles.System))
            _memory.AddSystem(_opts.SystemPrompt);
    }

    /// <summary>非流式运行：返回最终答案。</summary>
    public async Task<AgentResult> RunAsync(string userInput, CancellationToken ct = default)
    {
        _memory.AddUser(userInput);
        var toolDefs = _tools.ToOpenAiDefinitions();

        for (int step = 1; step <= _opts.MaxLoopSteps; step++)
        {
            await _tracer.TrackAsync(new AgentEvent(step, AgentEventType.Thought,
                Content: $"调用 LLM 决策（已有消息 {_memory.Messages.Count} 条）"), ct);

            ChatResponse response;
            try
            {
                response = await _llm.ChatAsync(new ChatRequest
                {
                    Messages = _memory.Messages.ToList(),
                    Tools = toolDefs,
                    Temperature = _opts.Llm.Temperature,
                    MaxTokens = _opts.Llm.MaxTokens
                }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LLM 调用失败");
                await _tracer.TrackAsync(new AgentEvent(step, AgentEventType.Error, Content: ex.Message), ct);
                return new AgentResult($"出错：{ex.Message}", step, false);
            }

            // 把 assistant 这一轮的输出（可能含 tool_calls）放回历史
            var assistantMsg = new ChatMessage
            {
                Role = ChatRoles.Assistant,
                Content = response.Content,
                ToolCalls = response.ToolCalls.Count > 0 ? response.ToolCalls : null
            };
            _memory.AddAssistant(assistantMsg);

            // 1) 没有工具调用 → LLM 给出最终回答
            if (response.ToolCalls.Count == 0)
            {
                var answer = response.Content ?? string.Empty;
                await _tracer.TrackAsync(new AgentEvent(step, AgentEventType.FinalAnswer, Content: answer), ct);
                return new AgentResult(answer, step, false);
            }

            // 2) 有工具调用 → 顺序执行每个 tool call 并把结果回灌
            foreach (var call in response.ToolCalls)
            {
                call.Function.Arguments = ToolCallArgumentRepair.Repair(
                    call.Function.Name, call.Function.Arguments, userInput);

                await _tracer.TrackAsync(new AgentEvent(step, AgentEventType.Action,
                    ToolName: call.Function.Name, ToolCallId: call.Id, Content: call.Function.Arguments), ct);

                string observation;
                if (!_tools.TryGet(call.Function.Name, out var tool))
                {
                    observation = $"错误：找不到工具 {call.Function.Name}";
                }
                else
                {
                    try
                    {
                        using var argDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Function.Arguments) ? "{}" : call.Function.Arguments);
                        observation = await tool.InvokeAsync(argDoc.RootElement, ct);
                    }
                    catch (JsonException jex)
                    {
                        observation = $"工具参数 JSON 非法：{jex.Message}";
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "工具 {Tool} 抛出异常", call.Function.Name);
                        observation = $"工具 {call.Function.Name} 抛异常：{ex.Message}";
                    }
                }

                _memory.AddToolResult(call.Id, call.Function.Name, observation);
                await _tracer.TrackAsync(new AgentEvent(step, AgentEventType.Observation,
                    ToolName: call.Function.Name, ToolCallId: call.Id, Content: observation), ct);
            }
        }

        await _tracer.TrackAsync(new AgentEvent(_opts.MaxLoopSteps, AgentEventType.Error,
            Content: "达到最大循环步数"), ct);
        return new AgentResult("已达到最大推理步数仍未完成任务，请尝试拆解后再问。", _opts.MaxLoopSteps, true);
    }

    /// <summary>流式运行：实时把最终回答的 token / 事件流式给到调用方。</summary>
    public async IAsyncEnumerable<AgentEvent> RunStreamingAsync(string userInput,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        _memory.AddUser(userInput);
        var toolDefs = _tools.ToOpenAiDefinitions();

        for (int step = 1; step <= _opts.MaxLoopSteps; step++)
        {
            yield return new AgentEvent(step, AgentEventType.Thought, Content: "LLM 流式决策中…");

            var contentSb = new System.Text.StringBuilder();
            List<ToolCall>? toolCalls = null;
            string? finishReason = null;

            await foreach (var chunk in _llm.ChatStreamAsync(new ChatRequest
            {
                Messages = _memory.Messages.ToList(),
                Tools = toolDefs,
                Temperature = _opts.Llm.Temperature,
                MaxTokens = _opts.Llm.MaxTokens
            }, ct))
            {
                if (!string.IsNullOrEmpty(chunk.ContentDelta))
                {
                    contentSb.Append(chunk.ContentDelta);
                    yield return new AgentEvent(step, AgentEventType.StreamDelta, Content: chunk.ContentDelta);
                }
                if (chunk.ToolCallsAccumulated != null) toolCalls = chunk.ToolCallsAccumulated;
                if (chunk.FinishReason != null) finishReason = chunk.FinishReason;
            }

            var assistantMsg = new ChatMessage
            {
                Role = ChatRoles.Assistant,
                Content = contentSb.Length > 0 ? contentSb.ToString() : null,
                ToolCalls = (toolCalls != null && toolCalls.Count > 0) ? toolCalls : null
            };
            _memory.AddAssistant(assistantMsg);

            if (assistantMsg.ToolCalls is null || assistantMsg.ToolCalls.Count == 0)
            {
                yield return new AgentEvent(step, AgentEventType.FinalAnswer, Content: assistantMsg.Content ?? "");
                yield break;
            }

            foreach (var call in assistantMsg.ToolCalls)
            {
                call.Function.Arguments = ToolCallArgumentRepair.Repair(
                    call.Function.Name, call.Function.Arguments, userInput);

                yield return new AgentEvent(step, AgentEventType.Action,
                    ToolName: call.Function.Name, ToolCallId: call.Id, Content: call.Function.Arguments);

                string observation;
                if (!_tools.TryGet(call.Function.Name, out var tool))
                    observation = $"错误：找不到工具 {call.Function.Name}";
                else
                {
                    try
                    {
                        using var argDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Function.Arguments) ? "{}" : call.Function.Arguments);
                        observation = await tool.InvokeAsync(argDoc.RootElement, ct);
                    }
                    catch (Exception ex)
                    {
                        observation = $"工具 {call.Function.Name} 异常：{ex.Message}";
                    }
                }
                _memory.AddToolResult(call.Id, call.Function.Name, observation);
                yield return new AgentEvent(step, AgentEventType.Observation,
                    ToolName: call.Function.Name, ToolCallId: call.Id, Content: observation);
            }
        }
        yield return new AgentEvent(_opts.MaxLoopSteps, AgentEventType.Error, Content: "达到最大循环步数");
    }
}
