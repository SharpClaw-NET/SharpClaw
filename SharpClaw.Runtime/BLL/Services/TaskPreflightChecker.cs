using SharpClaw.Core.Tasks.Models;
using SharpClaw.Core.Tasks.Preflight;

namespace SharpClaw.Runtime.BLL.Services;

/// <summary>
/// Application adapter for task requirement checks. Runtime fact assembly and
/// requirement semantics live in SharpClaw.Core; this type only supplies the
/// EF-backed host port.
/// </summary>
public sealed class TaskPreflightChecker(
    TaskPreflightEngine preflight,
    EfTaskPreflightHost preflightHost)
{
    public TaskPreflightResult CheckStatic(
        IReadOnlyList<TaskRequirementDefinition> requirements)
    {
        return preflight.CheckStatic(requirements);
    }

    public async Task<TaskPreflightResult> CheckRuntimeAsync(
        IReadOnlyList<TaskRequirementDefinition> requirements,
        IReadOnlyDictionary<string, object?> paramValues,
        Guid? callerAgentId,
        CancellationToken ct = default)
    {
        return await preflight.CheckRuntimeAsync(
            requirements,
            paramValues,
            callerAgentId,
            preflightHost,
            ct);
    }
}
