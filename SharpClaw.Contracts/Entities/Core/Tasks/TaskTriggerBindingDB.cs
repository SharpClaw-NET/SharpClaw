using SharpClaw.Contracts.Entities;

namespace SharpClaw.Contracts.Entities.Core.Tasks;

/// <summary>
/// Persisted binding for a self-registration trigger defined via a class-level
/// attribute on a task script (e.g. <c>[OnEvent]</c>, <c>[OnFileChanged]</c>).
/// One row per trigger attribute, linked to the owning <see cref="TaskDefinitionDB"/>.
/// </summary>
public class TaskTriggerBindingDB : BaseEntity
{
    public required Guid TaskDefinitionId { get; set; }

    public TaskDefinitionDB? TaskDefinition { get; set; }

    /// <summary>
    /// String representation of <c>TriggerKind</c> (e.g. "Cron", "Event", "FileChanged").
    /// Stored as a string so the table is readable without enum awareness.
    /// </summary>
    public required string Kind { get; set; }

    /// <summary>
    /// Discriminator value used for deduplication — cron expression,
    /// event type string, webhook route, source name, etc.
    /// </summary>
    public string? TriggerValue { get; set; }

    /// <summary>Optional filter string (event filter, custom source filter, etc.).</summary>
    public string? Filter { get; set; }

    /// <summary>Full JSON of the originating <c>TaskTriggerDefinition</c>.</summary>
    public required string DefinitionJson { get; set; }

    public bool IsEnabled { get; set; } = true;
}
