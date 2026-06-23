namespace SmartStudy.Web.Components;

public sealed record ChatUiMessage(string Role, string Label, string Content);
public sealed record QuickPrompt(string Label, string Text);
public sealed record CommandHint(string Command, string Description, string Prompt);
public sealed record TimelineItem(int Step, string Title, string CssClass, string? ToolName, string? Content, int MaxChars);
