namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Lightweight, contract-layer view of a persisted trigger-binding row
/// passed to <see cref="ITaskTriggerBindingSideEffect"/> implementations.
/// Mirrors the host's <c>TaskTriggerBindingDB</c> shape but lives in the
/// contracts assembly so modules can react to binding lifecycle events
/// without referencing the host infrastructure project.
/// </summary>
/// <param name="TaskDefinitionId">Owning task definition identity.</param>
/// <param name="Kind">Persisted trigger kind (e.g. <c>"OsShortcut"</c>).</param>
/// <param name="TriggerValue">Primary discriminator value; may be <see langword="null"/>.</param>
/// <param name="Filter">Secondary discriminator value; may be <see langword="null"/>.</param>
public sealed record TaskTriggerBindingDescriptor(
    Guid TaskDefinitionId,
    string Kind,
    string? TriggerValue,
    string? Filter);
