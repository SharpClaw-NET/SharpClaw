using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Entities.Core.Tasks;
using SharpClaw.Application.Infrastructure.Tasks.Models;
using SharpClaw.Application.Infrastructure.Tasks;
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
    ITaskTriggerSourceRegistry? triggerSourceRegistry = null)
{
    /// <summary>
    /// Parse and validate a task definition without persisting it.
    /// </summary>
    public TaskValidationResponse ValidateDefinition(string sourceText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceText);

        var parseResult = TaskScriptEngine.Parse(sourceText);
        if (!parseResult.Success || parseResult.Definition is null)
        {
            return new TaskValidationResponse(
                false,
                parseResult.Diagnostics.Select(ToDiagnosticResponse).ToList());
        }

        var validation = TaskScriptEngine.Validate(parseResult.Definition);
        var diagnostics = parseResult.Diagnostics
            .Concat(validation.Diagnostics)
            .Select(ToDiagnosticResponse)
            .ToList();

        return new TaskValidationResponse(validation.IsValid, diagnostics);
    }

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
        var parseResult = TaskScriptEngine.Parse(request.SourceText);
        if (!parseResult.Success || parseResult.Definition is null)
        {
            var errors = string.Join("; ", parseResult.Diagnostics.Select(FormatDiagnostic));
            throw new InvalidOperationException($"Task script parse failed: {errors}");
        }

        var validation = TaskScriptEngine.Validate(parseResult.Definition);
        if (!validation.IsValid)
        {
            var errors = string.Join("; ", validation.Diagnostics.Select(FormatDiagnostic));
            throw new InvalidOperationException($"Task script validation failed: {errors}");
        }

        var existing = await db.TaskDefinitions
            .AnyAsync(d => d.Name == parseResult.Definition.Name, ct);
        if (existing)
            throw new InvalidOperationException(
                $"Task definition '{parseResult.Definition.Name}' already exists.");

        var entity = new TaskDefinitionDB
        {
            Name = parseResult.Definition.Name,
            Description = parseResult.Definition.Description,
            SourceText = request.SourceText,
            OutputTypeName = parseResult.Definition.OutputType?.Name,
            ParametersJson  = SerializeParameters(parseResult.Definition.Parameters),
            RequirementsJson = SerializeRequirements(parseResult.Definition.Requirements),
            TriggersJson     = SerializeTriggers(parseResult.Definition.TriggerDefinitions),
        };

        db.TaskDefinitions.Add(entity);
        await db.SaveChangesAsync(ct);

        if (triggerRegistrar is not null)
        {
            var bindingsChanged = await triggerRegistrar.SyncTriggersAsync(entity, parseResult.Definition.TriggerDefinitions, ct);
            if (bindingsChanged)
            {
                await db.SaveChangesAsync(ct);
                if (triggerHostService is not null)
                    await triggerHostService.NotifyBindingsChangedAsync();
            }
        }

        return ToDefinitionResponse(entity,
            parseResult.Definition.Parameters,
            parseResult.Definition.Requirements,
            parseResult.Definition.TriggerDefinitions);
    }

    public async Task<TaskDefinitionResponse?> GetDefinitionAsync(
        Guid id, CancellationToken ct = default)
    {
        var entity = await db.TaskDefinitions.FindAsync([id], ct);
        if (entity is null) return null;
        return ToDefinitionResponse(entity,
            DeserializeParameters(entity.ParametersJson),
            DeserializeRequirements(entity.RequirementsJson),
            DeserializeTriggers(entity.TriggersJson));
    }

    /// <summary>
    /// Returns the deserialized requirement list
    /// </summary>
    public async Task<IReadOnlyList<TaskRequirementDefinition>?> GetRequirementsAsync(
        Guid id, CancellationToken ct = default)
    {
        var entity = await db.TaskDefinitions.FindAsync([id], ct);
        return entity is null ? null : DeserializeRequirements(entity.RequirementsJson);
    }

    /// <summary>
    /// Returns the deserialized trigger definition list for a task.
    /// </summary>
    public async Task<IReadOnlyList<TaskTriggerDefinition>?> GetTriggersAsync(
        Guid id, CancellationToken ct = default)
    {
        var entity = await db.TaskDefinitions.FindAsync([id], ct);
        return entity is null ? null : DeserializeTriggers(entity.TriggersJson);
    }

    public async Task<IReadOnlyList<TaskDefinitionResponse>> ListDefinitionsAsync(
        CancellationToken ct = default)
    {
        var entities = await db.TaskDefinitions
            .OrderByDescending(d => d.UpdatedAt)
            .ToListAsync(ct);

        return entities
            .Select(e => ToDefinitionResponse(e,
                DeserializeParameters(e.ParametersJson),
                DeserializeRequirements(e.RequirementsJson),
                DeserializeTriggers(e.TriggersJson)))
            .ToList();
    }

    public async Task<TaskDefinitionResponse?> UpdateDefinitionAsync(
        Guid id,
        UpdateTaskDefinitionRequest request,
        CancellationToken ct = default)
    {
        var entity = await db.TaskDefinitions.FindAsync([id], ct);
        if (entity is null) return null;

        IReadOnlyList<TaskParameterDefinition>? parameters = null;
        IReadOnlyList<TaskTriggerDefinition>? parsedTriggers = null;

        if (request.SourceText is not null)
        {
            var parseResult = TaskScriptEngine.Parse(request.SourceText);
            if (!parseResult.Success || parseResult.Definition is null)
            {
                var errors = string.Join("; ", parseResult.Diagnostics.Select(FormatDiagnostic));
                throw new InvalidOperationException($"Task script parse failed: {errors}");
            }

            var validation = TaskScriptEngine.Validate(parseResult.Definition);
            if (!validation.IsValid)
            {
                var errors = string.Join("; ", validation.Diagnostics.Select(FormatDiagnostic));
                throw new InvalidOperationException($"Task script validation failed: {errors}");
            }

            entity.Name = parseResult.Definition.Name;
            entity.Description = parseResult.Definition.Description;
            entity.SourceText = request.SourceText;
            entity.OutputTypeName = parseResult.Definition.OutputType?.Name;
            entity.ParametersJson  = SerializeParameters(parseResult.Definition.Parameters);
            entity.RequirementsJson = SerializeRequirements(parseResult.Definition.Requirements);
            entity.TriggersJson     = SerializeTriggers(parseResult.Definition.TriggerDefinitions);
            parameters = parseResult.Definition.Parameters;
            parsedTriggers = parseResult.Definition.TriggerDefinitions;
        }

        if (request.IsActive is not null)
            entity.IsActive = request.IsActive.Value;

        await db.SaveChangesAsync(ct);

        if (triggerRegistrar is not null && parsedTriggers is not null)
        {
            var bindingsChanged = await triggerRegistrar.SyncTriggersAsync(entity, parsedTriggers, ct);
            if (bindingsChanged)
            {
                await db.SaveChangesAsync(ct);
                if (triggerHostService is not null)
                    await triggerHostService.NotifyBindingsChangedAsync();
            }
        }

        return ToDefinitionResponse(entity,
            parameters ?? DeserializeParameters(entity.ParametersJson),
            DeserializeRequirements(entity.RequirementsJson),
            DeserializeTriggers(entity.TriggersJson));
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

        if (!definition.IsActive)
            throw new InvalidOperationException(
                $"Task definition '{definition.Name}' is not active.");

        var requirements = DeserializeRequirements(definition.RequirementsJson);
        if (requirements.Count > 0)
        {
            var paramMap = request.ParameterValues is not null
                ? request.ParameterValues.ToDictionary(
                    kv => kv.Key,
                    kv => (object?)kv.Value,
                    StringComparer.Ordinal)
                : (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>();

            var preflightResult = await preflight.CheckRuntimeAsync(
                requirements, paramMap, callerAgentId, ct);
            if (preflightResult.IsBlocked)
                throw new PreflightBlockedException(preflightResult);
        }

        var instance = new TaskInstanceDB
        {
            TaskDefinitionId = definition.Id,
            Status = TaskInstanceStatus.Queued,
            ParameterValuesJson = request.ParameterValues is not null
                ? JsonSerializer.Serialize(request.ParameterValues)
                : null,
            ChannelId = request.ChannelId,
            ContextId = request.ContextId,
            CallerUserId = callerUserId,
            CallerAgentId = callerAgentId,
        };

        db.TaskInstances.Add(instance);
        await db.SaveChangesAsync(ct);

        return ToInstanceResponse(instance, definition.Name);
    }

    /// <summary>
    /// Move a queued instance into the paused state.
    /// </summary>
    public async Task<bool> PauseInstanceAsync(Guid id, CancellationToken ct = default)
    {
        var instance = await FindTrackedOrColdInstanceAsync(id, ct);
        if (instance is null || instance.Status != TaskInstanceStatus.Running)
        {
            return false;
        }

        instance.Status = TaskInstanceStatus.Paused;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Move a paused instance back into the running state.
    /// </summary>
    public async Task<bool> ResumeInstanceAsync(Guid id, CancellationToken ct = default)
    {
        var instance = await FindTrackedOrColdInstanceAsync(id, ct);
        if (instance is null || instance.Status != TaskInstanceStatus.Paused)
        {
            return false;
        }

        instance.Status = TaskInstanceStatus.Running;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Mark a queued instance as running before orchestration begins.
    /// </summary>
    public async Task<bool> TryMarkInstanceRunningAsync(Guid id, CancellationToken ct = default)
    {
        var instance = await FindTrackedOrColdInstanceAsync(id, ct);
        if (instance is null || instance.Status != TaskInstanceStatus.Queued)
        {
            return false;
        }

        instance.Status = TaskInstanceStatus.Running;
        instance.StartedAt = DateTimeOffset.UtcNow;
        instance.CompletedAt = null;
        instance.ErrorMessage = null;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Mark an instance as stopped through a graceful runtime stop request.
    /// </summary>
    public async Task<bool> StopInstanceAsync(Guid id, CancellationToken ct = default)
    {
        var instance = await FindTrackedOrColdInstanceAsync(id, ct);
        if (instance is null || instance.Status is not (TaskInstanceStatus.Running or TaskInstanceStatus.Paused))
        {
            return false;
        }

        instance.Status = TaskInstanceStatus.Cancelled;
        instance.CompletedAt = DateTimeOffset.UtcNow;
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

        return ToInstanceResponse(instance, defName);
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
            .Select(i => new TaskInstanceSummaryResponse(
                i.Id,
                i.TaskDefinitionId,
                defNames.GetValueOrDefault(i.TaskDefinitionId, "(unknown)"),
                i.Status,
                i.CreatedAt,
                i.StartedAt,
                i.CompletedAt))
            .ToList();
    }

    /// <summary>
    /// Cancel a running or queued instance.
    /// </summary>
    public async Task<bool> CancelInstanceAsync(Guid id, CancellationToken ct = default)
    {
        var instance = await FindTrackedOrColdInstanceAsync(id, ct);
        if (instance is null) return false;

        if (instance.Status is not (TaskInstanceStatus.Queued or TaskInstanceStatus.Running or TaskInstanceStatus.Paused))
            return false;

        instance.Status = TaskInstanceStatus.Cancelled;
        instance.CompletedAt = DateTimeOffset.UtcNow;
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
        db.TaskExecutionLogs.Add(new TaskExecutionLogDB
        {
            TaskInstanceId = instanceId,
            Message = message,
            Level = level,
        });
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
            .Select(o => new TaskOutputEntryResponse(o.Id, o.Sequence, o.Data, o.CreatedAt))
            .ToList();
    }

    // ═══════════════════════════════════════════════════════════════
    // Mapping
    // ═══════════════════════════════════════════════════════════════

    private static string FormatDiagnostic(TaskDiagnostic d)
        => d.Line > 0 ? $"[Line {d.Line}] {d.Message}" : d.Message;

    private static TaskDiagnosticResponse ToDiagnosticResponse(TaskDiagnostic diagnostic)
        => new(
            diagnostic.Severity.ToString(),
            diagnostic.Code,
            diagnostic.Message,
            diagnostic.Line,
            diagnostic.Column);

    private TaskDefinitionResponse ToDefinitionResponse(
        TaskDefinitionDB entity,
        IReadOnlyList<TaskParameterDefinition> parameters,
        IReadOnlyList<TaskRequirementDefinition> requirements,
        IReadOnlyList<TaskTriggerDefinition> triggers)
    {
        return new TaskDefinitionResponse(
            entity.Id,
            entity.Name,
            entity.Description,
            entity.OutputTypeName,
            entity.IsActive,
            parameters.Select(p => new TaskParameterResponse(
                p.Name, p.TypeName, p.Description, p.DefaultValue, p.IsRequired)).ToList(),
            requirements.Select(r => new TaskRequirementResponse(
                r.Kind.ToString(),
                r.Severity.ToString(),
                r.Value,
                r.CapabilityValue,
                r.ParameterName)).ToList(),
            triggers.Select(t => new TaskTriggerResponse(
                t.TriggerKey ?? string.Empty,
                TriggerValueFor(t),
                TriggerFilterFor(t),
                IsEnabled: true)).ToList(),
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.CustomId);
    }

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

    private static TaskInstanceResponse ToInstanceResponse(
        TaskInstanceDB instance,
        string taskName)
    {
        return new TaskInstanceResponse(
            instance.Id,
            instance.TaskDefinitionId,
            taskName,
            instance.Status,
            instance.OutputSnapshotJson,
            instance.ErrorMessage,
            instance.LogEntries
                .Select(l => new TaskExecutionLogResponse(l.Message, l.Level, l.CreatedAt))
                .ToList(),
            instance.CreatedAt,
            instance.StartedAt,
            instance.CompletedAt,
            instance.ChannelId,
            ContextId: instance.ContextId);
    }

    private async Task<TaskInstanceDB?> FindTrackedOrColdInstanceAsync(Guid id, CancellationToken ct)
        => await entities.FindAsync<TaskInstanceDB>(db, id, ct);

    // ═══════════════════════════════════════════════════════════════
    // Parameter serialisation
    // ═══════════════════════════════════════════════════════════════

    private static string SerializeParameters(IReadOnlyList<TaskParameterDefinition> parameters)
    {
        return JsonSerializer.Serialize(parameters.Select(p => new
        {
            p.Name,
            p.TypeName,
            p.Description,
            p.DefaultValue,
            p.IsRequired,
        }));
    }

    private static IReadOnlyList<TaskParameterDefinition> DeserializeParameters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Select(e => new TaskParameterDefinition(
                Name: e.GetProperty("Name").GetString() ?? "",
                TypeName: e.GetProperty("TypeName").GetString() ?? "string",
                Description: e.TryGetProperty("Description", out var d) ? d.GetString() : null,
                DefaultValue: e.TryGetProperty("DefaultValue", out var dv) ? dv.GetString() : null,
                IsRequired: e.TryGetProperty("IsRequired", out var r) && r.GetBoolean()))
            .ToList();
    }

    // ═══════════════════════════════════════════════════════════════
    // Requirement serialisation
    // ═══════════════════════════════════════════════════════════════

    private static string SerializeRequirements(IReadOnlyList<TaskRequirementDefinition> requirements)
    {
        return JsonSerializer.Serialize(requirements.Select(r => new
        {
            Kind = r.Kind.ToString(),
            Severity = r.Severity.ToString(),
            r.Value,
            r.CapabilityValue,
            r.ParameterName,
            r.Line,
        }));
    }

    private static IReadOnlyList<TaskRequirementDefinition> DeserializeRequirements(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Select(e =>
            {
                var kindStr = e.GetProperty("Kind").GetString() ?? "";
                var sevStr = e.GetProperty("Severity").GetString() ?? "Error";

                Enum.TryParse<TaskRequirementKind>(kindStr, out var kind);
                Enum.TryParse<TaskDiagnosticSeverity>(sevStr, out var severity);

                return new TaskRequirementDefinition
                {
                    Kind           = kind,
                    Severity       = severity,
                    Value          = e.TryGetProperty("Value",           out var v)  ? v.GetString()  : null,
                    CapabilityValue= e.TryGetProperty("CapabilityValue", out var cv) ? cv.GetString() : null,
                    ParameterName  = e.TryGetProperty("ParameterName",  out var pn) ? pn.GetString() : null,
                    Line           = e.TryGetProperty("Line",            out var ln) ? ln.GetInt32()  : 0,
                };
            })
            .ToList();
    }
    // ═══════════════════════════════════════════════════════════════
    // Trigger serialisation
    // ═══════════════════════════════════════════════════════════════

    private static string SerializeTriggers(IReadOnlyList<TaskTriggerDefinition> triggers)
        => JsonSerializer.Serialize(triggers);

    private static IReadOnlyList<TaskTriggerDefinition> DeserializeTriggers(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        return JsonSerializer.Deserialize<List<TaskTriggerDefinition>>(json) ?? [];
    }
}
