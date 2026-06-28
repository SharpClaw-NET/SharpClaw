using SharpClaw.Core.Tasks.Preflight;

namespace SharpClaw.Application.Services;

/// <summary>
/// Thrown by <see cref="TaskService.CreateInstanceAsync"/> when the preflight
/// check determines that the current environment cannot satisfy one or more
/// <c>Error</c>-severity requirements declared on the task definition.
/// </summary>
public sealed class PreflightBlockedException(TaskPreflightResult result)
    : InvalidOperationException("Task preflight check failed — one or more requirements are not satisfied.")
{
    /// <summary>The detailed preflight outcome including all findings.</summary>
    public TaskPreflightResult PreflightResult { get; } = result;
}
