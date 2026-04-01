using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.Infrastructure.Models.Tasks;
using SharpClaw.Application.Infrastructure.Tasks;
using SharpClaw.Application.Infrastructure.Tasks.Models;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Enums;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

/// <summary>
/// Manages task script definitions and their execution instances.
/// Definitions are parsed on creation so validation errors surface
/// immediately rather than at execution time.
/// </summary>
public sealed class TaskService(SharpClawDbContext db)
{
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
            ParametersJson = SerializeParameters(parseResult.Definition.Parameters),
        };

        db.TaskDefinitions.Add(entity);
        await db.SaveChangesAsync(ct);

        return ToDefinitionResponse(entity, parseResult.Definition.Parameters);
    }

    public async Task<TaskDefinitionResponse?> GetDefinitionAsync(
        Guid id, CancellationToken ct = default)
    {
        var entity = await db.TaskDefinitions.FindAsync([id], ct);
        if (entity is null) return null;
        return ToDefinitionResponse(entity, DeserializeParameters(entity.ParametersJson));
    }

    public async Task<IReadOnlyList<TaskDefinitionResponse>> ListDefinitionsAsync(
        CancellationToken ct = default)
    {
        var entities = await db.TaskDefinitions
            .OrderByDescending(d => d.UpdatedAt)
            .ToListAsync(ct);

        return entities
            .Select(e => ToDefinitionResponse(e, DeserializeParameters(e.ParametersJson)))
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
            entity.ParametersJson = SerializeParameters(parseResult.Definition.Parameters);
            parameters = parseResult.Definition.Parameters;
        }

        if (request.IsActive is not null)
            entity.IsActive = request.IsActive.Value;

        await db.SaveChangesAsync(ct);
        return ToDefinitionResponse(entity, parameters ?? DeserializeParameters(entity.ParametersJson));
    }

    public async Task<bool> DeleteDefinitionAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await db.TaskDefinitions.FindAsync([id], ct);
        if (entity is null) return false;

        db.TaskDefinitions.Remove(entity);
        await db.SaveChangesAsync(ct);
        return true;
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

        var instance = new TaskInstanceDB
        {
            TaskDefinitionId = definition.Id,
            Status = TaskInstanceStatus.Queued,
            ParameterValuesJson = request.ParameterValues is not null
                ? JsonSerializer.Serialize(request.ParameterValues)
                : null,
            ChannelId = request.ChannelId,
            CallerUserId = callerUserId,
            CallerAgentId = callerAgentId,
        };

        db.TaskInstances.Add(instance);
        await db.SaveChangesAsync(ct);

        return ToInstanceResponse(instance, definition.Name);
    }

    public async Task<TaskInstanceResponse?> GetInstanceAsync(
        Guid id, CancellationToken ct = default)
    {
        var instance = await db.TaskInstances
            .Include(i => i.TaskDefinition)
            .Include(i => i.LogEntries.OrderBy(l => l.CreatedAt))
            .FirstOrDefaultAsync(i => i.Id == id, ct);

        if (instance is null) return null;
        return ToInstanceResponse(instance, instance.TaskDefinition.Name);
    }

    public async Task<IReadOnlyList<TaskInstanceSummaryResponse>> ListInstancesAsync(
        Guid? taskDefinitionId = null,
        CancellationToken ct = default)
    {
        var query = db.TaskInstances
            .Include(i => i.TaskDefinition)
            .AsQueryable();

        if (taskDefinitionId is not null)
            query = query.Where(i => i.TaskDefinitionId == taskDefinitionId.Value);

        return await query
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new TaskInstanceSummaryResponse(
                i.Id,
                i.TaskDefinitionId,
                i.TaskDefinition.Name,
                i.Status,
                i.CreatedAt,
                i.StartedAt,
                i.CompletedAt))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Cancel a running or queued instance.
    /// </summary>
    public async Task<bool> CancelInstanceAsync(Guid id, CancellationToken ct = default)
    {
        var instance = await db.TaskInstances.FindAsync([id], ct);
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
        var query = db.TaskOutputEntries
            .Where(o => o.TaskInstanceId == instanceId);

        if (since is not null)
            query = query.Where(o => o.CreatedAt > since.Value);

        return await query
            .OrderBy(o => o.Sequence)
            .Select(o => new TaskOutputEntryResponse(o.Id, o.Sequence, o.Data, o.CreatedAt))
            .ToListAsync(ct);
    }

    // ═══════════════════════════════════════════════════════════════
    // Mapping
    // ═══════════════════════════════════════════════════════════════

    private static string FormatDiagnostic(TaskDiagnostic d)
        => d.Line > 0 ? $"[Line {d.Line}] {d.Message}" : d.Message;

    private static TaskDefinitionResponse ToDefinitionResponse(
        TaskDefinitionDB entity,
        IReadOnlyList<TaskParameterDefinition> parameters)
    {
        return new TaskDefinitionResponse(
            entity.Id,
            entity.Name,
            entity.Description,
            entity.OutputTypeName,
            entity.IsActive,
            parameters.Select(p => new TaskParameterResponse(
                p.Name, p.TypeName, p.Description, p.DefaultValue, p.IsRequired)).ToList(),
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.CustomId);
    }

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
            instance.ChannelId);
    }

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
}
