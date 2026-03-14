using SharpClaw.Contracts.Entities;

namespace SharpClaw.Application.Infrastructure.Models.Tasks;

/// <summary>
/// Persisted task script definition.  Stores the raw C# source together
/// with extracted metadata so the script does not have to be re-parsed on
/// every execution.
/// </summary>
public class TaskDefinitionDB : BaseEntity
{
    /// <summary>Value from the <c>[Task("name")]</c> attribute.</summary>
    public required string Name { get; set; }

    /// <summary>Optional description from the <c>[Description("...")]</c> attribute.</summary>
    public string? Description { get; set; }

    /// <summary>The raw <c>.cs</c> task script source text.</summary>
    public required string SourceText { get; set; }

    /// <summary>
    /// Name of the nested class marked <c>[Output]</c>, if any.
    /// Used by the orchestrator to determine the output schema.
    /// </summary>
    public string? OutputTypeName { get; set; }

    /// <summary>
    /// JSON-serialised array of <see cref="Infrastructure.Tasks.Models.TaskParameterDefinition"/>
    /// extracted at parse time.  Kept so the UI / CLI can display parameter
    /// metadata without re-parsing the source.
    /// </summary>
    public string? ParametersJson { get; set; }

    /// <summary>Whether this definition is available for execution.</summary>
    public bool IsActive { get; set; } = true;

    // ── Navigation ────────────────────────────────────────────────

    /// <summary>All instances ever created from this definition.</summary>
    public ICollection<TaskInstanceDB> Instances { get; set; } = [];
}
