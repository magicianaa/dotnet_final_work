using Microsoft.Extensions.Options;
using SmartStudy.Core.Configuration;

namespace SmartStudy.Core.Rag;

public interface IRagRuntimeContext
{
    RagOptions Current { get; }
}

public sealed class DefaultRagRuntimeContext : IRagRuntimeContext
{
    private readonly IOptions<AgentOptions> _options;

    public DefaultRagRuntimeContext(IOptions<AgentOptions> options)
    {
        _options = options;
    }

    public RagOptions Current => _options.Value.Rag;
}
