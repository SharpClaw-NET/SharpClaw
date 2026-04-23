namespace SharpClaw.Application.Infrastructure.Tasks.Models;

/// <summary>
/// Specific loop shape used by <see cref="TaskStepDefinition"/> when
/// <see cref="TaskStepDefinition.Kind"/> is <see cref="TaskStepKind.Loop"/>.
/// </summary>
public enum TaskLoopKind
{
    While,
    ForEach,
}
