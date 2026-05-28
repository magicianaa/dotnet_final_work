using System.Text.Json;

namespace SmartStudy.Core.Tracing;

public enum AgentEventType { Thought, Action, Observation, FinalAnswer, Error, StreamDelta }

public sealed record AgentEvent(
    int Step,
    AgentEventType Type,
    string? ToolName = null,
    string? ToolCallId = null,
    string? Content = null,
    object? Payload = null,
    DateTime TimestampUtc = default
)
{
    public DateTime TimestampUtc { get; init; } = TimestampUtc == default ? DateTime.UtcNow : TimestampUtc;
}

/// <summary>追踪 Agent 推理过程。允许多实现并存（控制台 + 文件 + UI）。</summary>
public interface IAgentTracer
{
    Task TrackAsync(AgentEvent ev, CancellationToken ct = default);
}

public sealed class CompositeAgentTracer : IAgentTracer
{
    private readonly IReadOnlyList<IAgentTracer> _inner;
    public CompositeAgentTracer(IEnumerable<IAgentTracer> inner) => _inner = inner.ToList();

    public async Task TrackAsync(AgentEvent ev, CancellationToken ct = default)
    {
        foreach (var t in _inner) await t.TrackAsync(ev, ct);
    }
}

/// <summary>把每一步事件以 JSONL 格式追加到文件，便于事后审计。</summary>
public sealed class JsonlFileTracer : IAgentTracer, IAsyncDisposable
{
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

    public JsonlFileTracer(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _writer = new StreamWriter(File.Open(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
    }

    public async Task TrackAsync(AgentEvent ev, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { await _writer.WriteLineAsync(JsonSerializer.Serialize(ev, Opts)); }
        finally { _lock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.FlushAsync();
        _writer.Dispose();
    }
}

/// <summary>当不需要追踪时使用。</summary>
public sealed class NullTracer : IAgentTracer
{
    public Task TrackAsync(AgentEvent ev, CancellationToken ct = default) => Task.CompletedTask;
}
