using Microsoft.EntityFrameworkCore;
using SharpClaw.Runtime.BLL.Services.Triggers;
using SharpClaw.Contracts.Entities.Core.Tasks;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Core.Tasks.Administration;
using SharpClaw.Core.Tasks.Models;
using SharpClaw.Core.Tasks.Preflight;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.BLL.Services;

public sealed class EfTaskAdministrationHost(
    SharpClawDbContext db,
    IPersistenceEntityResolver entities,
    TaskPreflightChecker preflight,
    TaskTriggerRegistrar? triggerRegistrar = null,
    TaskTriggerHostService? triggerHostService = null,
    ITaskTriggerSourceRegistry? triggerSourceRegistry = null)
    : ITaskAdministrationHost
{
    public async Task<bool> DefinitionNameExistsAsync(
        string name,
        CancellationToken ct)
    {
        return await db.TaskDefinitions.AnyAsync(
            definition => definition.Name == name,
            ct);
    }

    public async Task<TaskDefinitionDB?> LoadDefinitionAsync(
        Guid id,
        CancellationToken ct)
    {
        return await db.TaskDefinitions.FindAsync([id], ct);
    }

    public async Task<IReadOnlyList<TaskDefinitionDB>> ListDefinitionsAsync(
        CancellationToken ct)
    {
        return await db.TaskDefinitions.ToListAsync(ct);
    }

    public void TrackDefinition(TaskDefinitionDB definition)
    {
        db.TaskDefinitions.Add(definition);
    }

    public void RemoveDefinition(TaskDefinitionDB definition)
    {
        db.TaskDefinitions.Remove(definition);
    }

    public async Task<IReadOnlyList<TaskTriggerBindingDB>> LoadTriggerBindingsAsync(
        Guid taskDefinitionId,
        CancellationToken ct)
    {
        return await db.TaskTriggerBindings
            .Where(binding => binding.TaskDefinitionId == taskDefinitionId)
            .ToListAsync(ct);
    }

    public async Task<bool> SyncTriggersAsync(
        TaskDefinitionDB definition,
        IReadOnlyList<TaskTriggerDefinition> triggers,
        CancellationToken ct)
    {
        return triggerRegistrar is not null
            && await triggerRegistrar.SyncTriggersAsync(definition, triggers, ct);
    }

    public async Task<bool> RemoveTriggersAsync(
        Guid definitionId,
        CancellationToken ct)
    {
        if (triggerRegistrar is null)
            return false;

        await triggerRegistrar.RemoveTriggersAsync(definitionId, ct);
        return true;
    }

    public async Task NotifyTriggerBindingsChangedAsync(CancellationToken ct)
    {
        if (triggerHostService is not null)
            await triggerHostService.NotifyBindingsChangedAsync();
    }

    public string? ResolveTriggerValue(TaskTriggerDefinition trigger)
    {
        return triggerSourceRegistry
            ?.ResolveByKey(trigger.TriggerKey)
            ?.GetBindingValue(trigger);
    }

    public string? ResolveTriggerFilter(TaskTriggerDefinition trigger)
    {
        return triggerSourceRegistry
            ?.ResolveByKey(trigger.TriggerKey)
            ?.GetBindingFilter(trigger);
    }

    public async Task<TaskPreflightResult> CheckRuntimePreflightAsync(
        IReadOnlyList<TaskRequirementDefinition> requirements,
        IReadOnlyDictionary<string, object?> parameterValues,
        Guid? callerAgentId,
        CancellationToken ct)
    {
        return await preflight.CheckRuntimeAsync(
            requirements,
            parameterValues,
            callerAgentId,
            ct);
    }

    public void TrackInstance(TaskInstanceDB instance)
    {
        db.TaskInstances.Add(instance);
    }

    public async Task<TaskInstanceDB?> LoadInstanceAsync(
        Guid id,
        CancellationToken ct)
    {
        return await entities.FindAsync<TaskInstanceDB>(db, id, ct);
    }

    public async Task<TaskInstanceDB?> LoadInstanceWithLogsAsync(
        Guid id,
        CancellationToken ct)
    {
        var instance = await entities.FindAsync<TaskInstanceDB>(db, id, ct);
        if (instance is null)
            return null;

        instance.LogEntries = (await entities.QueryAsync<TaskExecutionLogDB>(
            db,
            log => log.TaskInstanceId == id,
            hint: new PersistenceQueryHint("TaskInstanceId", id),
            ct: ct)).OrderBy(log => log.CreatedAt).ToList();
        return instance;
    }

    public async Task<IReadOnlyList<TaskInstanceDB>> ListInstancesAsync(
        Guid? taskDefinitionId,
        CancellationToken ct)
    {
        return await entities.QueryAsync<TaskInstanceDB>(
            db,
            taskDefinitionId is not null
                ? instance => instance.TaskDefinitionId == taskDefinitionId.Value
                : _ => true,
            hint: taskDefinitionId is not null
                ? new PersistenceQueryHint(
                    "TaskDefinitionId",
                    taskDefinitionId.Value)
                : null,
            ct: ct);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> LoadDefinitionNamesAsync(
        IReadOnlyCollection<Guid> definitionIds,
        CancellationToken ct)
    {
        return await db.TaskDefinitions
            .Where(definition => definitionIds.Contains(definition.Id))
            .ToDictionaryAsync(
                definition => definition.Id,
                definition => definition.Name,
                ct);
    }

    public void TrackLog(TaskExecutionLogDB log)
    {
        db.TaskExecutionLogs.Add(log);
    }

    public async Task<IReadOnlyList<TaskOutputEntryDB>> ListOutputsAsync(
        Guid instanceId,
        DateTimeOffset? since,
        CancellationToken ct)
    {
        return await entities.QueryAsync<TaskOutputEntryDB>(
            db,
            since is not null
                ? output => output.TaskInstanceId == instanceId
                    && output.CreatedAt > since.Value
                : output => output.TaskInstanceId == instanceId,
            hint: new PersistenceQueryHint("TaskInstanceId", instanceId),
            ct: ct);
    }

    public async Task SaveAsync(CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
    }
}
