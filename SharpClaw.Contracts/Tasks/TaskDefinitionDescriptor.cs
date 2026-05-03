namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Lightweight, contract-layer view of a persisted task definition exposed
/// to <see cref="ITaskTriggerSource"/> implementations that opt into owning
/// their own binding persistence. The host populates this descriptor from
/// its EF entity (<c>TaskDefinitionDB</c>) before invoking the source so
/// modules never need a project reference back to the host infrastructure
/// project.
/// </summary>
/// <param name="Id">Stable database identity of the task definition.</param>
/// <param name="Name">Value from the <c>[Task("name")]</c> attribute. Used
/// by some sources (e.g. OS shortcut, cron) as a stable customId for
/// per-definition side effects.</param>
public sealed record TaskDefinitionDescriptor(Guid Id, string Name);
