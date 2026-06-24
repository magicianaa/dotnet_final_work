using SmartStudy.Core.Llm;

namespace SmartStudy.Core.Memory;

internal static class ConversationHistory
{
    public static void TrimInPlace(
        List<ChatMessage> messages,
        int maxNonSystemMessages,
        bool keepTrailingToolCall = false)
    {
        maxNonSystemMessages = Math.Max(1, maxNonSystemMessages);

        var system = messages.LastOrDefault(m => m.Role == ChatRoles.System);
        var nonSystem = messages.Where(m => m.Role != ChatRoles.System).ToList();
        var blocks = BuildProtocolSafeBlocks(nonSystem, keepTrailingToolCall);

        var count = blocks.Sum(block => block.Count);
        while (count > maxNonSystemMessages && blocks.Count > 1)
        {
            count -= blocks[0].Count;
            blocks.RemoveAt(0);
        }

        messages.Clear();
        if (system is not null)
            messages.Add(system);

        foreach (var block in blocks)
            messages.AddRange(block);
    }

    private static List<List<ChatMessage>> BuildProtocolSafeBlocks(
        List<ChatMessage> messages,
        bool keepTrailingToolCall)
    {
        var blocks = new List<List<ChatMessage>>();

        for (var i = 0; i < messages.Count;)
        {
            var message = messages[i];

            if (message.Role == ChatRoles.Tool)
            {
                i++;
                continue;
            }

            if (!HasToolCalls(message))
            {
                blocks.Add(new List<ChatMessage> { message });
                i++;
                continue;
            }

            var block = new List<ChatMessage> { message };
            var expectedIds = message.ToolCalls!
                .Select(call => call.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);
            var seenIds = new HashSet<string>(StringComparer.Ordinal);

            var j = i + 1;
            while (j < messages.Count && messages[j].Role == ChatRoles.Tool)
            {
                var tool = messages[j];
                if (!string.IsNullOrWhiteSpace(tool.ToolCallId) &&
                    expectedIds.Contains(tool.ToolCallId) &&
                    seenIds.Add(tool.ToolCallId))
                {
                    block.Add(tool);
                }

                j++;
            }

            var isComplete = expectedIds.Count > 0 && seenIds.Count == expectedIds.Count;
            var isTrailingInProgressCall = keepTrailingToolCall && j >= messages.Count;
            if (isComplete || isTrailingInProgressCall)
                blocks.Add(block);

            i = j;
        }

        return blocks;
    }

    private static bool HasToolCalls(ChatMessage message) =>
        message.Role == ChatRoles.Assistant && message.ToolCalls is { Count: > 0 };
}
