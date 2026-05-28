using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartStudy.Core.Llm;

/// <summary>OpenAI 兼容协议的消息角色。</summary>
public static class ChatRoles
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";
}

/// <summary>一条对话消息。assistant 角色可能携带 tool_calls；tool 角色必须携带 tool_call_id。</summary>
public sealed class ChatMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("tool_calls"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolCall>? ToolCalls { get; set; }
    [JsonPropertyName("tool_call_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; set; }
    [JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }
}

public sealed class ToolCall
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "function";
    [JsonPropertyName("function")] public ToolCallFunction Function { get; set; } = new();
}

public sealed class ToolCallFunction
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("arguments")] public string Arguments { get; set; } = "{}";
}

/// <summary>提供给 LLM 的工具描述（function calling）。</summary>
public sealed class ToolDefinition
{
    [JsonPropertyName("type")] public string Type { get; set; } = "function";
    [JsonPropertyName("function")] public FunctionDefinition Function { get; set; } = new();
}

public sealed class FunctionDefinition
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("parameters")] public JsonElement Parameters { get; set; }
}

public sealed class ChatRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; set; } = new();
    [JsonPropertyName("temperature")] public double Temperature { get; set; } = 0.3;
    [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; } = 2048;
    [JsonPropertyName("tools"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolDefinition>? Tools { get; set; }
    [JsonPropertyName("tool_choice"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? ToolChoice { get; set; }
    [JsonPropertyName("stream"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Stream { get; set; }
}

/// <summary>非流式响应的最小化模型。</summary>
public sealed class ChatResponse
{
    public string? Content { get; set; }
    public List<ToolCall> ToolCalls { get; set; } = new();
    public string FinishReason { get; set; } = "";
}

/// <summary>流式增量片段。</summary>
public sealed class ChatStreamChunk
{
    public string? ContentDelta { get; set; }
    public List<ToolCall>? ToolCallsAccumulated { get; set; }
    public string? FinishReason { get; set; }
}
