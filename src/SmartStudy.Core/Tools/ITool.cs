using System.Text.Json;
using SmartStudy.Core.Llm;

namespace SmartStudy.Core.Tools;

/// <summary>所有 Agent 工具的统一接口。每个工具都暴露 JSON Schema 让 LLM 决定调用方式。</summary>
public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonElement ParametersSchema { get; }
    Task<string> InvokeAsync(JsonElement arguments, CancellationToken ct = default);
}

/// <summary>工具注册中心。</summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.Ordinal);

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        foreach (var t in tools) _tools[t.Name] = t;
    }

    public IReadOnlyCollection<ITool> All => _tools.Values;

    public bool TryGet(string name, out ITool tool) => _tools.TryGetValue(name, out tool!);

    public List<ToolDefinition> ToOpenAiDefinitions() =>
        _tools.Values.Select(t => new ToolDefinition
        {
            Type = "function",
            Function = new FunctionDefinition
            {
                Name = t.Name,
                Description = t.Description,
                Parameters = t.ParametersSchema
            }
        }).ToList();
}

/// <summary>简化的 JSON Schema 构造器。</summary>
public static class JsonSchema
{
    public static JsonElement Build(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
