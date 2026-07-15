using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Entities.Core.Tasks;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Core.State;
using SharpClaw.Core.Tasks.Administration;
using SharpClaw.Core.Tasks.Models;
using SharpClaw.Core.Tasks.Preflight;
using SharpClaw.Runtime.BLL.Services.Triggers;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.BLL.Services;

public sealed class EfTaskAdministrationHost(
    SharpClawDbContext db,
    IPersistenceEntityResolver entities,
    TaskPreflightChecker preflight,
    DurableExecutionPersistence durablePersistence,
    TaskTriggerRegistrar? triggerRegistrar = null,
    TaskTriggerHostService? triggerHostService = null,
    ITaskTriggerSourceRegistry? triggerSourceRegistry = null)
    : ITaskAdministrationHost
{
    private readonly CoreStateSession _states = new(db);

    public async Task<bool> DefinitionNameExistsAsync(
        string name,
        CancellationToken ct)
    {
        return await db.TaskDefinitions.AnyAsync(
            definition => definition.Name == name,
            ct);
    }

    public async Task<TaskDefinitionState?> LoadDefinitionAsync(
        Guid id,
        CancellationToken ct)
    {
        var entity = await db.TaskDefinitions.FindAsync([id], ct);
        return entity is null ? null : _states.Map(entity);
    }

    public async Task<IReadOnlyList<TaskDefinitionState>> ListDefinitionsAsync(
        CancellationToken ct)
    {
        return _states.Map(await db.TaskDefinitions.ToListAsync(ct));
    }

    public void TrackDefinition(TaskDefinitionState definition)
    {
        _states.Track(definition);
    }

    public void RemoveDefinition(TaskDefinitionState definition)
    {
        _states.Remove(definition);
    }

    public async Task<IReadOnlyList<TaskTriggerBindingState>> LoadTriggerBindingsAsync(
        Guid taskDefinitionId,
        CancellationToken ct)
    {
        var records = await db.TaskTriggerBindings
            .Where(binding => binding.TaskDefinitionId == taskDefinitionId)
            .ToListAsync(ct);
        return _states.Map(records);
    }

    public async Task<bool> SyncTriggersAsync(
        TaskDefinitionState definition,
        IReadOnlyList<TaskTriggerDefinition> triggers,
        CancellationToken ct)
    {
        return triggerRegistrar is not null
            && await triggerRegistrar.SyncTriggersAsync(
                _states.Entity<TaskDefinitionDB>(definition),
                triggers,
                ct);
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

    public void TrackInstance(TaskInstanceState instance)
    {
        db.TaskInstances.Add(ExecutionStateMapper.ToEntity(instance));
    }

    public async Task<TaskInstanceState?> LoadInstanceAsync(
        Guid id,
        CancellationToken ct)
    {
        var entity = await entities.FindAsync<TaskInstanceDB>(db, id, ct);
        return entity is null ? null : ExecutionStateMapper.ToCoreState(entity);
    }

    public async Task PersistInstanceAsync(
        TaskInstanceState instance,
        CancellationToken ct)
    {
        var entity = db.TaskInstances.Local
                         .FirstOrDefault(candidate => candidate.Id == instance.Id)
            ?? await entities.FindAsync<TaskInstanceDB>(db, instance.Id, ct)
            ?? throw new InvalidOperationException(
                $"Task instance {instance.Id} was not found while persisting state.");
        ExecutionStateMapper.Apply(instance, entity);
        var terminalInstances = durablePersistence.PrepareTaskState();
        await durablePersistence.SealTaskDiagnosticsAsync(terminalInstances, ct);
        await db.SaveChangesAsync(ct);
    }

    public Task AppendLogAsync(
        TaskExecutionLog log,
        CancellationToken ct) =>
        durablePersistence.AppendTaskLogAsync(log, saveChanges: true, ct);

    public async Task SaveAsync(CancellationToken ct)
    {
        _states.ApplyAll();
        var terminalInstances = durablePersistence.PrepareTaskState();
        await durablePersistence.SealTaskDiagnosticsAsync(terminalInstances, ct);
        await _states.SaveChangesAsync(ct);
    }
}
