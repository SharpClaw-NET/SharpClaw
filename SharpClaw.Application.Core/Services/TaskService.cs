using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Core.Tasks.Administration;
using SharpClaw.Core.Tasks.Models;

namespace SharpClaw.Application.Services;

/// <summary>
/// Manages task script definitions and their execution instances.
/// </summary>
public sealed class TaskService(
    TaskAdministrationWorkflowEngine administration,
    EfTaskAdministrationHost administrationHost) : ITaskAuthoring
{
    public TaskValidationResponse ValidateDefinition(string sourceText)
        => administration.ValidateDefinition(sourceText);

    public async Task<TaskDefinitionResponse> CreateDefinitionAsync(
        CreateTaskDefinitionRequest request,
        CancellationToken ct = default)
    {
        return await administration.CreateDefinitionAsync(
            request,
            administrationHost,
            ct);
    }

    public async Task<TaskDefinitionResponse?> GetDefinitionAsync(
        Guid id,
        CancellationToken ct = default)
    {
        return await administration.GetDefinitionAsync(
            id,
            administrationHost,
            ct);
    }

    public async Task<IReadOnlyList<TaskRequirementDefinition>?> GetRequirementsAsync(
        Guid id,
        CancellationToken ct = default)
    {
        return await administration.GetRequirementsAsync(
            id,
            administrationHost,
            ct);
    }

    public async Task<IReadOnlyList<TaskTriggerDefinition>?> GetTriggersAsync(
        Guid id,
        CancellationToken ct = default)
    {
        return await administration.GetTriggersAsync(
            id,
            administrationHost,
            ct);
    }

    public async Task<IReadOnlyList<TaskDefinitionResponse>> ListDefinitionsAsync(
        CancellationToken ct = default)
    {
        return await administration.ListDefinitionsAsync(
            administrationHost,
            ct);
    }

    public async Task<TaskDefinitionResponse?> UpdateDefinitionAsync(
        Guid id,
        UpdateTaskDefinitionRequest request,
        CancellationToken ct = default)
    {
        return await administration.UpdateDefinitionAsync(
            id,
            request,
            administrationHost,
            ct);
    }

    public async Task<bool> DeleteDefinitionAsync(
        Guid id,
        CancellationToken ct = default)
    {
        return await administration.DeleteDefinitionAsync(
            id,
            administrationHost,
            ct);
    }

    public async Task<int> SetTriggersEnabledAsync(
        Guid taskDefinitionId,
        bool enabled,
        CancellationToken ct = default)
    {
        return await administration.SetTriggersEnabledAsync(
            taskDefinitionId,
            enabled,
            administrationHost,
            ct);
    }

    public async Task<TaskInstanceResponse> CreateInstanceAsync(
        StartTaskInstanceRequest request,
        Guid? callerUserId = null,
        Guid? callerAgentId = null,
        CancellationToken ct = default)
    {
        return await administration.CreateInstanceAsync(
            request,
            callerUserId,
            callerAgentId,
            administrationHost,
            ct);
    }

    public async Task<bool> PauseInstanceAsync(
        Guid id,
        CancellationToken ct = default)
    {
        return await administration.PauseInstanceAsync(
            id,
            administrationHost,
            ct);
    }

    public async Task<bool> ResumeInstanceAsync(
        Guid id,
        CancellationToken ct = default)
    {
        return await administration.ResumeInstanceAsync(
            id,
            administrationHost,
            ct);
    }

    public async Task<bool> TryMarkInstanceRunningAsync(
        Guid id,
        CancellationToken ct = default)
    {
        return await administration.TryMarkInstanceRunningAsync(
            id,
            administrationHost,
            ct);
    }

    public async Task<bool> StopInstanceAsync(
        Guid id,
        CancellationToken ct = default)
    {
        return await administration.StopInstanceAsync(
            id,
            administrationHost,
            ct);
    }

    public async Task<TaskInstanceResponse?> GetInstanceAsync(
        Guid id,
        CancellationToken ct = default)
    {
        return await administration.GetInstanceAsync(
            id,
            administrationHost,
            ct);
    }

    public async Task<IReadOnlyList<TaskInstanceSummaryResponse>> ListInstancesAsync(
        Guid? taskDefinitionId = null,
        CancellationToken ct = default)
    {
        return await administration.ListInstancesAsync(
            taskDefinitionId,
            administrationHost,
            ct);
    }

    public async Task<bool> CancelInstanceAsync(
        Guid id,
        CancellationToken ct = default)
    {
        return await administration.CancelInstanceAsync(
            id,
            administrationHost,
            ct);
    }

    public async Task AppendLogAsync(
        Guid instanceId,
        string message,
        string level = "Info",
        CancellationToken ct = default)
    {
        await administration.AppendLogAsync(
            instanceId,
            message,
            level,
            administrationHost,
            ct);
    }

    public async Task<IReadOnlyList<TaskOutputEntryResponse>> GetOutputsAsync(
        Guid instanceId,
        DateTimeOffset? since = null,
        CancellationToken ct = default)
    {
        return await administration.GetOutputsAsync(
            instanceId,
            since,
            administrationHost,
            ct);
    }
}
