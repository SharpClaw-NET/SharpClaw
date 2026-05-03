namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Read-only view of a single trigger attribute occurrence on a task class,
/// passed to <see cref="ITaskTriggerAttributeHandler.Handle"/>. Exposes only
/// the values a handler needs to emit a <see cref="TaskTriggerDefinition"/>
/// (positional and named argument access plus a diagnostic sink), so
/// modules implementing handlers do not depend on Roslyn or on the parser's
/// internal representation.
/// </summary>
public abstract class TaskTriggerAttributeContext
{
    /// <summary>
    /// The attribute identifier as it appears in source, without the
    /// surrounding brackets and without the trailing <c>Attribute</c> suffix
    /// (e.g. <c>"Schedule"</c> for both <c>[Schedule(...)]</c> and
    /// <c>[ScheduleAttribute(...)]</c>).
    /// </summary>
    public abstract string AttributeName { get; }

    /// <summary>1-based source line of the attribute occurrence.</summary>
    public abstract int Line { get; }

    /// <summary>Number of arguments supplied to the attribute.</summary>
    public abstract int ArgumentCount { get; }

    /// <summary>
    /// Returns the positional argument at <paramref name="index"/> as a string
    /// literal value, or <see langword="null"/> if the argument is missing or
    /// is not a string literal.
    /// </summary>
    public abstract string? GetStringArg(int index);

    /// <summary>
    /// Returns the positional argument at <paramref name="index"/> as an int
    /// literal value, or <see langword="null"/> if the argument is missing or
    /// is not an int literal.
    /// </summary>
    public abstract int? GetIntArg(int index);

    /// <summary>
    /// Returns the named argument <paramref name="name"/> as a string literal
    /// value, or <see langword="null"/> if absent or not a string literal.
    /// </summary>
    public abstract string? GetNamedStringArg(string name);

    /// <summary>
    /// Returns the named argument <paramref name="name"/> as an int literal
    /// value, or <see langword="null"/> if absent or not an int literal.
    /// </summary>
    public abstract int? GetNamedIntArg(string name);

    /// <summary>
    /// Returns the named argument <paramref name="name"/> as a double literal
    /// value (also accepts float and int literals), or <see langword="null"/>
    /// if absent or not a numeric literal.
    /// </summary>
    public abstract double? GetNamedDoubleArg(string name);

    /// <summary>
    /// Returns the named argument <paramref name="name"/> parsed as the
    /// <typeparamref name="T"/> enum (case-insensitive), supporting both
    /// member access (<c>FlagsEnum.A</c>) and bitwise-or composition
    /// (<c>FlagsEnum.A | FlagsEnum.B</c>) for <c>[Flags]</c> enums. Returns
    /// <see langword="null"/> if the argument is absent or unparseable.
    /// </summary>
    public abstract T? GetNamedEnumArg<T>(string name) where T : struct, Enum;

    /// <summary>
    /// Returns the raw source text of the positional argument at
    /// <paramref name="index"/>, exactly as written in the script (useful for
    /// attributes whose argument may be a member access or composite
    /// expression, e.g. <c>[RequiresPlatform(TaskPlatform.Windows)]</c>).
    /// Returns <see langword="null"/> if the argument is missing.
    /// </summary>
    public abstract string? GetRawArgText(int index);

    /// <summary>
    /// Reports a diagnostic against this attribute occurrence. The parser
    /// stamps the attribute's source line on the diagnostic.
    /// </summary>
    public abstract void Report(
        TaskTriggerAttributeDiagnosticSeverity severity,
        string code,
        string message);
}
