namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// A single trigger binding declared on a task class via a self-registration
/// attribute. Produced by the parser; persisted as JSON on TaskDefinitionDB.
/// </summary>
public sealed record TaskTriggerDefinition
{
    /// <summary>
    /// String key identifying the trigger source that owns this definition.
    /// Matches <see cref="ITaskTriggerSource.TriggerKey"/> (or one of
    /// <see cref="ITaskTriggerSource.TriggerKeys"/>). Set for both core and
    /// module-owned triggers; replaces the former <c>Kind</c> enum.
    /// </summary>
    public string? TriggerKey { get; init; }

    /// <summary>Source line number in the task script for diagnostic purposes.</summary>
    public int Line { get; init; }

    /// <summary>
    /// Opaque parameter map populated by the parser. Each owning
    /// <see cref="ITaskTriggerSource"/> reads its inputs from this dictionary
    /// using parameter-key constants owned by the module that owns the trigger.
    /// Keys are case-sensitive; values must remain wire-compatible across
    /// releases so persisted task scripts continue to round-trip.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Parameters { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);
}
