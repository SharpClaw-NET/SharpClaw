using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Entities.Core.Tasks;
using SharpClaw.Core.Tasks.Administration;
using SharpClaw.Core.Tasks.Models;
using SharpClaw.Application.Core.Services.Triggers;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

/// <summary>
/// Manages task script definitions and their execution instances.
/// Definitions are parsed on creation so validation errors surface
/// immediately rather than at execution time.
/// </summary>
public sealed class TaskService(
    SharpClawDbContext db,
    IPersistenceEntityResolver entities,
    TaskPreflightChecker preflight,
    TaskTriggerRegistrar? triggerRegistrar = null,
    TaskTriggerHostService? triggerHostService = null,
    ITaskTriggerSourceRegistry? triggerSourceRegistry = null) : ITaskAuthoring
{
    private readonly TaskAdministrationEngine _tasks = new();

    /// <summary>
    /// Parse and validate a task definition without persisting it.
    /// </summary>
    public TaskValidationResponse ValidateDefinition(string sourceText)
        => _tasks.ValidateDefinition(sourceText);

    // ═══════════════════════════════════════════════════════════════
    // Definitions
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Parse and persist a new task definition from raw C# source.
    /// Returns errors if the script is invalid.
    /// </summary>
    public async Task<TaskDefinitionResponse> CreateDefinitionAsync(
        CreateTaskDefinitionRequest request,
        CancellationToken ct = default)
    {
        var prepared = _tasks.PrepareDefinition(request);

        var existing = await db.TaskDefinitions
            .AnyAsync(d => d.Name == prepared.Entity.Name, ct);
        _tasks.EnsureDefinitionNameAvailable(prepared.Entity.Name, existing);

        var entity = prepared.Entity;

        db.TaskDefinitions.Add(entity);
        await db.SaveChangesAsync(ct);

        if (triggerRegistrar is not null)
        {
            var bindingsChanged = await triggerRegistrar.SyncTriggersAsync(entity, prepared.Definition.TriggerDefinitions, ct);
            if (bindingsChanged)
            {
                await db.SaveChangesAsync(ct);
                if (triggerHostService is not null)
                    await triggerHostService.NotifyBindingsChangedAsync();
            }
        }

        return ToDefinitionResponse(entity,
            prepared.Definition.Parameters,
            prepared.Definition.Requirements,
            prepared.Definition.TriggerDefinitions);
    }

    public async Task<TaskDefinitionResponse?> GetDefinitionAsync(
        Guid id, CancellationToken ct = default)
    {
        var entity = await db.TaskDefinitions.FindAsync([id], ct);
        if (entity is null) return null;
        return ToDefinitionResponse(entity,
            _tasks.DeserializeParameters(entity.ParametersJson),
            _tasks.DeserializeRequirements(entity.RequirementsJson),
            _tasks.DeserializeTriggers(entity.TriggersJson));
    }

    /// <summary>
    /// Returns the deserialized requirement list
    /// </summary>
    public async Task<IReadOnlyList<TaskRequirementDefinition>?> GetRequirementsAsync(
        Guid id, CancellationToken ct = default)
    {
        var entity = await db.TaskDefinitions.FindAsync([id], ct);
        return entity is null ? null : _tasks.DeserializeRequirements(entity.RequirementsJson);
    }

    /// <summary>
    /// Returns the deserialized trigger definition list for a task.
    /// </summary>
    public async Task<IReadOnlyList<TaskTriggerDefinition>?> GetTriggersAsync(
        Guid id, CancellationToken ct = default)
    {
        var entity = await db.TaskDefinitions.FindAsync([id], ct);
        return entity is null ? null : _tasks.DeserializeTriggers(entity.TriggersJson);
    }

    public async Task<IReadOnlyList<TaskDefinitionResponse>> ListDefinitionsAsync(
        CancellationToken ct = default)
    {
        var entities = await db.TaskDefinitions
            .OrderByDescending(d => d.UpdatedAt)
            .ToListAsync(ct);

        return entities
            .Select(e => ToDefinitionResponse(e,
                _tasks.DeserializeParameters(e.ParametersJson),
                _tasks.DeserializeRequirements(e.RequirementsJson),
                _tasks.DeserializeTriggers(e.TriggersJson)))
            .ToList();
    }

    public async Task<TaskDefinitionResponse?> UpdateDefinitionAsync(
        Guid id,
        UpdateTaskDefinitionRequest request,
        CancellationToken ct = default)
    {
        var entity = await db.TaskDefinitions.FindAsync([id], ct);
        if (entity is null) return null;

        var updated = _tasks.ApplyDefinitionUpdate(entity, request);

        await db.SaveChangesAsync(ct);

        if (triggerRegistrar is not null && updated.SourceWasUpdated)
        {
            var bindingsChanged = await triggerRegistrar.SyncTriggersAsync(entity, updated.Triggers, ct);
            if (bindingsChanged)
            {
                await db.SaveChangesAsync(ct);
                if (triggerHostService is not null)
                    await triggerHostService.NotifyBindingsChangedAsync();
            }
        }

        return ToDefinitionResponse(entity,
            updated.Parameters,
            updated.Requirements,
            updated.Triggers);
    }

    public async Task<bool> DeleteDefinitionAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await db.TaskDefinitions.FindAsync([id], ct);
        if (entity is null) return false;

        if (triggerRegistrar is not null)
        {
            await triggerRegistrar.RemoveTriggersAsync(id, ct);
            await db.SaveChangesAsync(ct);
            if (triggerHostService is not null)
                await triggerHostService.NotifyBindingsChangedAsync();
        }

        db.TaskDefinitions.Remove(entity);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Enable or disable all trigger bindings for a task definition.
    /// Returns the number of bindings affected.
    /// </summary>
    public async Task<int> SetTriggersEnabledAsync(
        Guid taskDefinitionId,
        bool enabled,
        CancellationToken ct = default)
    {
        var bindings = await db.TaskTriggerBindings
            .Where(b => b.TaskDefinitionId == taskDefinitionId)
            .ToListAsync(ct);

        foreach (var binding in bindings)
            binding.IsEnabled = enabled;

        await db.SaveChangesAsync(ct);
        return bindings.Count;
    }

    // ═══════════════════════════════════════════════════════════════
    // Instances
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Create a new task instance (queued).  The orchestrator picks it up
    /// and begins execution.
    /// </summary>
    public async Task<TaskInstanceResponse> CreateInstanceAsync(
        StartTaskInstanceRequest request,
        Guid? callerUserId = null,
        Guid? callerAgentId = null,
        CancellationToken ct = default)
    {
        var definition = await db.TaskDefinitions.FindAsync([request.TaskDefinitionId], ct)
            ?? throw new InvalidOperationException(
                $"Task definition {request.TaskDefinitionId} not found.");

        var requirements = _tasks.DeserializeRequirements(definition.RequirementsJson);
        if (requirements.Count > 0)
        {
            var paramMap = _tasks.ToPreflightParameterMap(request.ParameterValues);

            var preflightResult = await preflight.CheckRuntimeAsync(
                requirements, paramMap, callerAgentId, ct);
            if (preflightResult.IsBlocked)
                throw new PreflightBlockedException(preflightResult);
        }

        var instance = _tasks.CreateInstance(definition, request, callerUserId, callerAgentId);

        db.TaskInstances.Add(instance);
        await db.SaveChangesAsync(ct);

        return _tasks.ToInstanceResponse(instance, definition.Name);
    }

    /// <summary>
    /// Move a queued instance into the paused state.
    /// </summary>
    public async Task<bool> PauseInstanceAsync(Guid id, CancellationToken ct = default)
    {
        var instance = await FindTrackedOrColdInstanceAsync(id, ct);
        if (instance is null || !_tasks.TryPauseInstance(instance))
        {
            return false;
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Move a paused instance back into the running state.
    /// </summary>
    public async Task<bool> ResumeInstanceAsync(Guid id, CancellationToken ct = default)
    {
        var instance = await FindTrackedOrColdInstanceAsync(id, ct);
        if (instance is null || !_tasks.TryResumeInstance(instance))
        {
            return false;
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Mark a queued instance as running before orchestration begins.
    /// </summary>
    public async Task<bool> TryMarkInstanceRunningAsync(Guid id, CancellationToken ct = default)
    {
        var instance = await FindTrackedOrColdInstanceAsync(id, ct);
        if (instance is null || !_tasks.TryMarkInstanceRunning(instance))
        {
            return false;
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Mark an instance as stopped through a graceful runtime stop request.
    /// </summary>
    public async Task<bool> StopInstanceAsync(Guid id, CancellationToken ct = default)
    {
        var instance = await FindTrackedOrColdInstanceAsync(id, ct);
        if (instance is null || !_tasks.TryStopInstance(instance))
        {
            return false;
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<TaskInstanceResponse?> GetInstanceAsync(
        Guid id, CancellationToken ct = default)
    {
        var instance = await entities.FindAsync<TaskInstanceDB>(db, id, ct);
        if (instance is null)
            return null;

        var definition = await db.TaskDefinitions.FindAsync([instance.TaskDefinitionId], ct);
        var defName = definition?.Name ?? "(unknown)";

        instance.LogEntries = (await entities.QueryAsync<TaskExecutionLogDB>(
            db,
            l => l.TaskInstanceId == id,
            hint: new PersistenceQueryHint("TaskInstanceId", id),
            ct: ct)).OrderBy(l => l.CreatedAt).ToList();

        return _tasks.ToInstanceResponse(instance, defName);
    }

    public async Task<IReadOnlyList<TaskInstanceSummaryResponse>> ListInstancesAsync(
        Guid? taskDefinitionId = null,
        CancellationToken ct = default)
    {
        var hint = taskDefinitionId is not null
            ? new PersistenceQueryHint("TaskDefinitionId", taskDefinitionId.Value)
            : null;

        var instances = await entities.QueryAsync<TaskInstanceDB>(
            db,
            taskDefinitionId is not null
                ? i => i.TaskDefinitionId == taskDefinitionId.Value
                : _ => true,
            hint: hint,
            ct: ct);

        // Build a lookup for task definition names (hot entities in EF)
        var defIds = instances.Select(i => i.TaskDefinitionId).Distinct().ToList();
        var defNames = await db.TaskDefinitions
            .Where(d => defIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => d.Name, ct);

        return instances
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => _tasks.ToSummaryResponse(
                i,
                defNames.GetValueOrDefault(i.TaskDefinitionId, "(unknown)")))
            .ToList();
    }

    /// <summary>
    /// Cancel a running or queued instance.
    /// </summary>
    public async Task<bool> CancelInstanceAsync(Guid id, CancellationToken ct = default)
    {
        var instance = await FindTrackedOrColdInstanceAsync(id, ct);
        if (instance is null) return false;

        if (!_tasks.TryCancelInstance(instance))
            return false;

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Append a log entry to an instance.  Used by the orchestrator during execution.
    /// </summary>
    public async Task AppendLogAsync(
        Guid instanceId,
        string message,
        string level = "Info",
        CancellationToken ct = default)
    {
        db.TaskExecutionLogs.Add(_tasks.AddLog(null, instanceId, message, level));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Return persisted output entries for an instance.
    /// When <paramref name="since"/> is provided, only entries created after
    /// that timestamp are returned.
    /// </summary>
    public async Task<IReadOnlyList<TaskOutputEntryResponse>> GetOutputsAsync(
        Guid instanceId,
        DateTimeOffset? since = null,
        CancellationToken ct = default)
    {
        var entries = await entities.QueryAsync<TaskOutputEntryDB>(
            db,
            since is not null
                ? o => o.TaskInstanceId == instanceId && o.CreatedAt > since.Value
                : o => o.TaskInstanceId == instanceId,
            hint: new PersistenceQueryHint("TaskInstanceId", instanceId),
            ct: ct);

        return entries
            .OrderBy(o => o.Sequence)
            .Select(_tasks.ToOutputResponse)
            .ToList();
    }

    // ═══════════════════════════════════════════════════════════════
    // Mapping
    // ═══════════════════════════════════════════════════════════════

    private TaskDefinitionResponse ToDefinitionResponse(
        TaskDefinitionDB entity,
        IReadOnlyList<TaskParameterDefinition> parameters,
        IReadOnlyList<TaskRequirementDefinition> requirements,
        IReadOnlyList<TaskTriggerDefinition> triggers)
        => _tasks.ToDefinitionResponse(
            entity,
            parameters,
            requirements,
            triggers,
            TriggerValueFor,
            TriggerFilterFor);

    /// <summary>
    /// Computes the response-shaped <c>TriggerValue</c> by delegating to the
    /// owning <see cref="ITaskTriggerSource"/> via
    /// <see cref="ITaskTriggerSourceRegistry"/>. Mirrors the path used by
    /// <c>TaskTriggerRegistrar</c> so binding rows and API responses agree.
    /// Returns <see langword="null"/> when no registry is wired (test
    /// scenarios) or no source claims the key.
    /// </summary>
    private string? TriggerValueFor(TaskTriggerDefinition t) =>
        triggerSourceRegistry?.ResolveByKey(t.TriggerKey)?.GetBindingValue(t);

    private string? TriggerFilterFor(TaskTriggerDefinition t) =>
        triggerSourceRegistry?.ResolveByKey(t.TriggerKey)?.GetBindingFilter(t);

    private async Task<TaskInstanceDB?> FindTrackedOrColdInstanceAsync(Guid id, CancellationToken ct)
        => await entities.FindAsync<TaskInstanceDB>(db, id, ct);
}
