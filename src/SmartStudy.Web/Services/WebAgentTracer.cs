using SmartStudy.Core.Tracing;

namespace SmartStudy.Web.Services;

public sealed class WebAgentTraceStore
{
    private readonly List<AgentEvent> _events = new();
    private readonly object _gate = new();

    public IReadOnlyList<AgentEvent> Events
    {
        get
        {
            lock (_gate) return _events.ToList();
        }
    }

    public event Action? Changed;

    public void Clear()
    {
        lock (_gate) _events.Clear();
        Changed?.Invoke();
    }

    public void Add(AgentEvent ev)
    {
        lock (_gate) _events.Add(ev);
        Changed?.Invoke();
    }
}

public sealed class WebAgentTracer : IAgentTracer
{
    private readonly WebAgentTraceStore _store;

    public WebAgentTracer(WebAgentTraceStore store)
    {
        _store = store;
    }

    public Task TrackAsync(AgentEvent ev, CancellationToken ct = default)
    {
        _store.Add(ev);
        return Task.CompletedTask;
    }
}
