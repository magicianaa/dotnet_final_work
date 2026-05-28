using System.Text.Json;
using Spectre.Console;
using SmartStudy.Core.Tracing;

namespace SmartStudy.Cli;

/// <summary>把 Agent 事件渲染到 Spectre.Console，让推理过程一目了然。</summary>
public sealed class SpectreConsoleTracer : IAgentTracer
{
    public Task TrackAsync(AgentEvent ev, CancellationToken ct = default)
    {
        switch (ev.Type)
        {
            case AgentEventType.Thought:
                AnsiConsole.MarkupLine($"[grey]┌── step {ev.Step} · Thought[/]  [dim]{Escape(ev.Content)}[/]");
                break;
            case AgentEventType.Action:
                AnsiConsole.MarkupLine($"[yellow]│   Action[/] [bold]{Escape(ev.ToolName)}[/]({Escape(Truncate(ev.Content, 200))})");
                break;
            case AgentEventType.Observation:
                AnsiConsole.MarkupLine($"[green]│   Observation[/] [dim]{Escape(Truncate(ev.Content, 400))}[/]");
                break;
            case AgentEventType.FinalAnswer:
                AnsiConsole.MarkupLine("[grey]└── FinalAnswer ────────[/]");
                AnsiConsole.Write(new Spectre.Console.Panel(Escape(ev.Content ?? ""))
                    .Header("[bold cyan]Agent[/]").Border(BoxBorder.Rounded));
                break;
            case AgentEventType.StreamDelta:
                AnsiConsole.Markup(Escape(ev.Content ?? ""));
                break;
            case AgentEventType.Error:
                AnsiConsole.MarkupLine($"[red]│   Error[/] {Escape(ev.Content)}");
                break;
        }
        return Task.CompletedTask;
    }

    private static string Escape(string? s) => Markup.Escape(s ?? "");
    private static string Truncate(string? s, int max)
        => s is null ? "" : (s.Length <= max ? s : s[..max] + "…");
}
