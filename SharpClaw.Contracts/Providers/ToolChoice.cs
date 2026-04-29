namespace SharpClaw.Contracts.Providers;

/// <summary>
/// Declares how the model should select a tool for a tool-calling turn.
/// Mirrors OpenAI's <c>tool_choice</c> field: <c>"auto"</c>, <c>"none"</c>,
/// <c>"required"</c>, or a named function.
/// </summary>
public enum ToolChoiceMode
{
    /// <summary>Default — model chooses whether and which tool(s) to call.</summary>
    Auto = 0,
    /// <summary>Model must not call any tool this turn.</summary>
    None,
    /// <summary>Model must call at least one tool this turn.</summary>
    Required,
    /// <summary>Model must call the single named function.</summary>
    Named,
}

/// <summary>
/// Tool-selection policy attached to a completion call.
/// </summary>
public sealed record ToolChoice(
    ToolChoiceMode Mode,
    string? NamedFunction = null)
{
    public static ToolChoice Auto { get; } = new(ToolChoiceMode.Auto);
    public static ToolChoice None { get; } = new(ToolChoiceMode.None);
    public static ToolChoice Required { get; } = new(ToolChoiceMode.Required);
    public static ToolChoice ForFunction(string name) => new(ToolChoiceMode.Named, name);
}
