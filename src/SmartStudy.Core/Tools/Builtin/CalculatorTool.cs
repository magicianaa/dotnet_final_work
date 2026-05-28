using System.Data;
using System.Text.Json;

namespace SmartStudy.Core.Tools.Builtin;

/// <summary>使用 DataTable.Compute 进行安全的算术求值，避免动态代码注入。</summary>
public sealed class CalculatorTool : ITool
{
    public string Name => "calculate";
    public string Description => "对一个数学表达式求值，支持 + - * / ( ) 和小数。例如计算学习时长、复习天数等。";

    public JsonElement ParametersSchema { get; } = JsonSchema.Build("""
    {
      "type": "object",
      "properties": {
        "expression": { "type": "string", "description": "需要求值的数学表达式，例如 (3+4)*2.5" }
      },
      "required": ["expression"]
    }
    """);

    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken ct = default)
    {
        if (!arguments.TryGetProperty("expression", out var exprEl) || exprEl.ValueKind != JsonValueKind.String)
            return Task.FromResult("错误：必须提供 expression 字段。");
        var expr = exprEl.GetString()!;
        try
        {
            using var dt = new DataTable();
            var value = dt.Compute(expr, string.Empty);
            return Task.FromResult($"{expr} = {value}");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"无法计算表达式：{ex.Message}");
        }
    }
}
