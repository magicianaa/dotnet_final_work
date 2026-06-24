using SmartStudy.Core.Llm;

namespace SmartStudy.Core.Memory;

/// <summary>对话短期记忆。维护 system + 历史对话，支持滑动窗口防止上下文爆炸。</summary>
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
        // system 始终保留在最前，且只保留一条最新的
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
        // 滑动窗口：保留 system + 最后 N 条非 system
        var nonSys = _messages.Count(m => m.Role != ChatRoles.System);
        while (nonSys > _maxNonSystem)
        {
            var idx = _messages.FindIndex(m => m.Role != ChatRoles.System);
            if (idx < 0) break;
            _messages.RemoveAt(idx);
            nonSys--;
        }
    }
}
