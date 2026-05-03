using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Application.Services;

/// <summary>
/// Host implementation of <see cref="ITaskInstanceLauncher"/>. Delegates
/// to <see cref="TaskService"/> for instance creation and to
/// <see cref="TaskOrchestrator"/> for execution.
/// </summary>
public sealed class TaskInstanceLauncher(
    TaskService taskService,
    TaskOrchestrator orchestrator) : ITaskInstanceLauncher
{
    public async Task<Guid> LaunchAsync(
        Guid taskDefinitionId,
        IReadOnlyDictionary<string, string>? parameterValues,
        Guid? callerAgentId,
        CancellationToken ct)
    {
        Dictionary<string, string>? values = parameterValues is null
            ? null
            : new Dictionary<string, string>(parameterValues, StringComparer.Ordinal);

        var instance = await taskService.CreateInstanceAsync(
            new StartTaskInstanceRequest(
                TaskDefinitionId: taskDefinitionId,
                ParameterValues: values),
            callerAgentId: callerAgentId,
            ct: ct);

        await orchestrator.StartAsync(instance.Id, ct);
        return instance.Id;
    }
}
