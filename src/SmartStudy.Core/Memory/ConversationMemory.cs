using SmartStudy.Core.Llm;

namespace SmartStudy.Core.Memory;

/// <summary>Conversation short-term memory with a protocol-safe sliding window.</summary>
public interface IConversationMemory
{
    IReadOnlyList<ChatMessage> Messages { get; }
    void AddSystem(string content);
    void AddUser(string content);
    void AddAssistant(ChatMessage message);
    void AddToolResult(string toolCallId, string toolName, string content);
    void Reload();
    void Reset();
}

public sealed class ConversationMemory : IConversationMemory
{
    private readonly List<ChatMessage> _messages = new();
    private readonly int _maxNonSystem;

    public ConversationMemory(int maxNonSystemMessages = 40)
    {
        _maxNonSystem = maxNonSystemMessages;
    }

    public IReadOnlyList<ChatMessage> Messages => _messages;

    public void AddSystem(string content)
    {
        _messages.RemoveAll(m => m.Role == ChatRoles.System);
        _messages.Insert(0, new ChatMessage { Role = ChatRoles.System, Content = content });
    }

    public void AddUser(string content) => Append(new ChatMessage { Role = ChatRoles.User, Content = content });

    public void AddAssistant(ChatMessage message) => Append(message);

    public void AddToolResult(string toolCallId, string toolName, string content) =>
        Append(new ChatMessage
        {
            Role = ChatRoles.Tool,
            ToolCallId = toolCallId,
            Name = toolName,
            Content = content
        });

    public void Reload()
    {
    }

    public void Reset()
    {
        var sys = _messages.FirstOrDefault(m => m.Role == ChatRoles.System);
        _messages.Clear();
        if (sys != null) _messages.Add(sys);
    }

    private void Append(ChatMessage msg)
    {
        _messages.Add(msg);
        ConversationHistory.TrimInPlace(_messages, _maxNonSystem, keepTrailingToolCall: true);
    }
}
