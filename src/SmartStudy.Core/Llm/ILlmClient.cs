namespace SmartStudy.Core.Llm;

public interface ILlmClient
{
    Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default);

    IAsyncEnumerable<ChatStreamChunk> ChatStreamAsync(ChatRequest request, CancellationToken ct = default);
}
