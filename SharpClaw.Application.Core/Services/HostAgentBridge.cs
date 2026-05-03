using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Entities.Core.Tasks;
using SharpClaw.Application.Infrastructure.Tasks;
using SharpClaw.Application.Infrastructure.Tasks.Models;
using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.DTOs.Roles;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

/// <summary>
/// Default <see cref="IHostAgentBridge"/> implementation. Owns the chat,
/// parsing, and provisioning flows that previously lived in
/// <see cref="TaskOrchestrator"/>. Any module (currently the Agent
/// Orchestration module) calls these methods through the contracts
/// interface so it stays free of <c>SharpClawDbContext</c> and other
/// Core/Infrastructure types.
/// </summary>
public sealed class HostAgentBridge(
    SharpClawDbContext db,
    TaskService taskService,
    ChatService chatService,
    IServiceScopeFactory scopeFactory) : IHostAgentBridge
{
    public async Task<string?> ChatAsync(
        Guid instanceId, string taskName, string message, Guid? agentId, CancellationToken ct)
    {
        var channelId = await GetInstanceChannelIdAsync(instanceId, ct);
        var request = new ChatRequest(message, agentId, WellKnownClientKeys.Api,
            TaskContext: new TaskChatContext(instanceId, taskName));
        var response = await chatService.SendMessageAsync(channelId, request, ct: ct);
        await taskService.AppendLogAsync(
            instanceId, $"Chat → {response.AssistantMessage.Content?.Length ?? 0} chars", ct: ct);
        return response.AssistantMessage.Content;
    }

    public async Task<string> ChatStreamAsync(
        Guid instanceId, string taskName, string message, Guid? agentId, CancellationToken ct)
    {
        var channelId = await GetInstanceChannelIdAsync(instanceId, ct);
        var request = new ChatRequest(message, agentId, WellKnownClientKeys.Api,
            TaskContext: new TaskChatContext(instanceId, taskName));
        var sb = new StringBuilder();
        await foreach (var evt in chatService.SendMessageStreamAsync(
            channelId, request, AutoApproveAsync, ct: ct))
        {
            if (evt.Type == ChatStreamEventType.TextDelta && evt.Delta is not null)
                sb.Append(evt.Delta);
        }
        await taskService.AppendLogAsync(instanceId, $"ChatStream → {sb.Length} chars", ct: ct);
        return sb.ToString();
    }

    public async Task<string?> ChatToThreadAsync(
        Guid instanceId, string taskName, Guid threadId, string message, Guid? agentId, CancellationToken ct)
    {
        var channelId = await GetInstanceChannelIdAsync(instanceId, ct);
        var request = new ChatRequest(message, agentId, WellKnownClientKeys.Api,
            TaskContext: new TaskChatContext(instanceId, taskName));
        var response = await chatService.SendMessageAsync(channelId, request, threadId: threadId, ct: ct);
        await taskService.AppendLogAsync(
            instanceId, $"ChatToThread {threadId} → {response.AssistantMessage.Content?.Length ?? 0} chars", ct: ct);
        return response.AssistantMessage.Content;
    }

    public string ParseStructuredResponse(Guid instanceId, string text, string? typeName)
    {
        // The script definition is needed to validate the parsed shape against
        // the task's declared data types.  We resolve it from the running task
        // instance's source text by re-loading the compiled plan via the task
        // service is overkill — instead we parse and validate against the
        // currently-running plan only when we have access to the definition.
        // For now, just validate JSON shape; the orchestrator-level definition
        // check is preserved by routing through the script engine.
        var jsonText = ExtractJsonObject(text)
            ?? throw new InvalidOperationException("ParseResponse expected a JSON object in the source text.");

        using var doc = JsonDocument.Parse(jsonText);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("ParseResponse expected a JSON object payload.");

        if (!string.IsNullOrWhiteSpace(typeName))
        {
            var instance = db.TaskInstances
                .Include(i => i.TaskDefinition)
                .FirstOrDefault(i => i.Id == instanceId);
            if (instance?.TaskDefinition is not null)
            {
                var compileResult = TaskScriptEngine.ProcessScript(instance.TaskDefinition.SourceText, null);
                if (compileResult.Plan is not null)
                {
                    var dataType = compileResult.Plan.Definition.DataTypes
                        .FirstOrDefault(dt => dt.Name == typeName);
                    if (dataType is not null)
                        ValidateParsedResponseShape(doc.RootElement, dataType);
                }
            }
        }

        return JsonSerializer.Serialize(doc.RootElement);
    }

    public async Task<Guid?> FindModelAsync(string search, CancellationToken ct)
        => (await db.Models.FirstOrDefaultAsync(m => m.CustomId == search || m.Name == search, ct))?.Id;

    public async Task<Guid?> FindProviderAsync(string search, CancellationToken ct)
        => (await db.Providers.FirstOrDefaultAsync(p => p.CustomId == search || p.Name == search, ct))?.Id;

    public async Task<Guid?> FindAgentAsync(string search, CancellationToken ct)
        => (await db.Agents.FirstOrDefaultAsync(a => a.CustomId == search || a.Name == search, ct))?.Id;

    public async Task<Guid?> FindRoleAsync(string search, CancellationToken ct)
        => (await db.Roles.FirstOrDefaultAsync(r => r.Name == search, ct))?.Id;

    public async Task<Guid?> FindChannelAsync(string search, CancellationToken ct)
        => (await db.Channels.FirstOrDefaultAsync(c => c.CustomId == search || c.Title == search, ct))?.Id;

    public async Task<Guid> CreateAgentAsync(
        Guid instanceId, string name, Guid modelId, string? systemPrompt, string? customId, CancellationToken ct)
    {
        SharpClaw.Contracts.Entities.Core.AgentDB? agentEntity = null;
        if (!string.IsNullOrEmpty(customId))
        {
            agentEntity = await db.Agents
                .Where(a => a.CustomId == customId)
                .OrderByDescending(a => a.CreatedAt)
                .FirstOrDefaultAsync(ct);
        }

        if (agentEntity is not null)
        {
            agentEntity.Name = name;
            agentEntity.ModelId = modelId;
            agentEntity.SystemPrompt = systemPrompt;
            await db.SaveChangesAsync(ct);
        }
        else
        {
            agentEntity = new SharpClaw.Contracts.Entities.Core.AgentDB
            {
                Name = name,
                ModelId = modelId,
                SystemPrompt = systemPrompt,
                CustomId = customId,
            };
            db.Agents.Add(agentEntity);
            await db.SaveChangesAsync(ct);
        }

        if (await TryGetInstanceChannelIdAsync(instanceId, ct) is { } channelId)
        {
            var channel = await db.Channels
                .Include(c => c.AllowedAgents)
                .FirstOrDefaultAsync(c => c.Id == channelId, ct);
            if (channel is not null && !channel.AllowedAgents.Any(a => a.Id == agentEntity.Id))
            {
                channel.AllowedAgents.Add(agentEntity);
                await db.SaveChangesAsync(ct);
            }
        }

        await taskService.AppendLogAsync(instanceId, $"CreateAgent '{name}' → {agentEntity.Id}", ct: ct);
        return agentEntity.Id;
    }

    public async Task<Guid> CreateThreadAsync(
        Guid instanceId, Guid? channelId, string? threadName, CancellationToken ct)
    {
        var resolvedChannelId = channelId
            ?? await GetInstanceChannelIdAsync(instanceId, ct);

        var thread = new ChatThreadDB
        {
            Name = threadName ?? $"Task Thread {DateTimeOffset.UtcNow:HH:mm}",
            ChannelId = resolvedChannelId,
        };
        db.ChatThreads.Add(thread);
        await db.SaveChangesAsync(ct);
        await taskService.AppendLogAsync(instanceId, $"CreateThread '{thread.Name}' → {thread.Id}", ct: ct);
        return thread.Id;
    }

    public async Task<Guid> CreateRoleAsync(string roleName, CancellationToken ct)
    {
        var existing = await db.Roles.FirstOrDefaultAsync(r => r.Name == roleName, ct);
        if (existing is not null)
            return existing.Id;

        using var scope = scopeFactory.CreateScope();
        var roleService = scope.ServiceProvider.GetRequiredService<RoleService>();
        var created = await roleService.CreateAsync(roleName, ct);
        return created.Id;
    }

    public async Task SetRolePermissionsAsync(Guid roleId, string requestJson, CancellationToken ct)
    {
        var permRequest = !string.IsNullOrWhiteSpace(requestJson)
            ? JsonSerializer.Deserialize<SetRolePermissionsRequest>(
                  requestJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
              ?? new SetRolePermissionsRequest()
            : new SetRolePermissionsRequest();

        var role = await db.Roles
            .Include(r => r.PermissionSet)
            .FirstOrDefaultAsync(r => r.Id == roleId, ct)
            ?? throw new InvalidOperationException($"SetRolePermissions: role '{roleId}' not found.");

        var permissionSet = role.PermissionSet;
        if (permissionSet is null)
        {
            permissionSet = new PermissionSetDB();
            db.PermissionSets.Add(permissionSet);
            await db.SaveChangesAsync(ct);
            role.PermissionSetId = permissionSet.Id;
        }

        await db.GlobalFlags
            .Where(f => f.PermissionSetId == permissionSet.Id)
            .ExecuteDeleteCompatAsync(db, ct);

        await db.ResourceAccesses
            .Where(a => a.PermissionSetId == permissionSet.Id && a.ResourceId != WellKnownIds.AllResources)
            .ExecuteDeleteCompatAsync(db, ct);

        foreach (var (flagKey, clearance) in permRequest.GlobalFlags ?? new Dictionary<string, PermissionClearance>())
        {
            db.GlobalFlags.Add(new GlobalFlagDB
            {
                PermissionSetId = permissionSet.Id,
                FlagKey = flagKey,
                Clearance = clearance,
            });
        }

        foreach (var (resourceType, grants) in permRequest.ResourceGrants ?? new Dictionary<string, IReadOnlyList<ResourceGrant>>())
        {
            foreach (var grant in grants)
            {
                var existingWildcard = grant.ResourceId == WellKnownIds.AllResources
                    ? await db.ResourceAccesses.FirstOrDefaultAsync(a =>
                        a.PermissionSetId == permissionSet.Id &&
                        a.ResourceType == resourceType &&
                        a.ResourceId == WellKnownIds.AllResources, ct)
                    : null;

                if (existingWildcard is not null)
                {
                    existingWildcard.Clearance = grant.Clearance;
                    continue;
                }

                db.ResourceAccesses.Add(new ResourceAccessDB
                {
                    PermissionSetId = permissionSet.Id,
                    ResourceType = resourceType,
                    ResourceId = grant.ResourceId,
                    Clearance = grant.Clearance,
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task AssignRoleAsync(Guid agentId, Guid roleId, CancellationToken ct)
    {
        var agentEntity = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, ct)
            ?? throw new InvalidOperationException($"AssignRole: agent '{agentId}' not found.");
        if (!await db.Roles.AnyAsync(r => r.Id == roleId, ct))
            throw new InvalidOperationException($"AssignRole: role '{roleId}' not found.");

        agentEntity.RoleId = roleId;
        await db.SaveChangesAsync(ct);
    }

    public async Task<Guid> CreateChannelAsync(
        Guid instanceId, string title, Guid agentId, string? customId, CancellationToken ct)
    {
        var instanceContextId = await db.TaskInstances
            .Where(i => i.Id == instanceId)
            .Select(i => i.ContextId)
            .FirstOrDefaultAsync(ct);

        ChannelDB? existing = !string.IsNullOrEmpty(customId)
            ? await db.Channels.FirstOrDefaultAsync(c => c.CustomId == customId, ct)
            : await db.Channels.FirstOrDefaultAsync(c => c.Title == title, ct);

        Guid channelId;
        if (existing is not null)
        {
            existing.Title = title;
            existing.AgentId = agentId;
            if (!string.IsNullOrEmpty(customId))
                existing.CustomId = customId;
            if (instanceContextId.HasValue)
                existing.AgentContextId = instanceContextId;
            await db.SaveChangesAsync(ct);
            channelId = existing.Id;
        }
        else
        {
            using var scope = scopeFactory.CreateScope();
            var channelService = scope.ServiceProvider.GetRequiredService<ChannelService>();
            var resp = await channelService.CreateAsync(
                new SharpClaw.Contracts.DTOs.Channels.CreateChannelRequest(
                    AgentId: agentId,
                    Title: title,
                    CustomId: customId,
                    ContextId: instanceContextId),
                ct);
            channelId = resp.Id;
        }

        // Adopt as instance channel when none is set yet
        var inst = await db.TaskInstances.FindAsync([instanceId], ct);
        if (inst is not null && inst.ChannelId is null)
        {
            inst.ChannelId = channelId;
            await db.SaveChangesAsync(ct);
        }

        await taskService.AppendLogAsync(instanceId, $"CreateChannel '{title}' → {channelId}", ct: ct);
        return channelId;
    }

    public async Task AddAllowedAgentAsync(
        Guid instanceId, Guid agentId, Guid? channelId, CancellationToken ct)
    {
        var agentEntity = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, ct)
            ?? throw new InvalidOperationException($"AddAllowedAgent: agent '{agentId}' not found.");

        var targetChannelId = channelId
            ?? await GetInstanceChannelIdAsync(instanceId, ct);

        var channel = await db.Channels
            .Include(c => c.AllowedAgents)
            .FirstOrDefaultAsync(c => c.Id == targetChannelId, ct)
            ?? throw new InvalidOperationException($"AddAllowedAgent: channel '{targetChannelId}' not found.");

        if (!channel.AllowedAgents.Any(a => a.Id == agentEntity.Id))
        {
            channel.AllowedAgents.Add(agentEntity);
            await db.SaveChangesAsync(ct);
        }

        await taskService.AppendLogAsync(
            instanceId, $"AddAllowedAgent agent={agentId} → channel={targetChannelId}", ct: ct);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private async Task<Guid> GetInstanceChannelIdAsync(Guid instanceId, CancellationToken ct)
        => await TryGetInstanceChannelIdAsync(instanceId, ct)
           ?? throw new InvalidOperationException(
               $"Task instance {instanceId} has no channel yet. " +
               "Call CreateChannel before using Chat, CreateThread, or other channel-dependent steps.");

    private async Task<Guid?> TryGetInstanceChannelIdAsync(Guid instanceId, CancellationToken ct)
        => await db.TaskInstances
            .Where(i => i.Id == instanceId)
            .Select(i => i.ChannelId)
            .FirstOrDefaultAsync(ct);

    private static Task<bool> AutoApproveAsync(
        SharpClaw.Contracts.DTOs.AgentActions.AgentJobResponse _, CancellationToken __) =>
        Task.FromResult(false);

    private static string? ExtractJsonObject(string text)
    {
        var jsonStart = text.IndexOf('{');
        var jsonEnd = text.LastIndexOf('}');
        return jsonStart >= 0 && jsonEnd > jsonStart
            ? text[jsonStart..(jsonEnd + 1)]
            : null;
    }

    private static void ValidateParsedResponseShape(JsonElement element, TaskDataTypeDefinition dataType)
    {
        foreach (var property in dataType.Properties)
        {
            if (!element.TryGetProperty(property.Name, out var propertyElement))
                throw new InvalidOperationException(
                    $"ParseResponse<{dataType.Name}> missing property '{property.Name}'.");

            if (property.IsCollection)
            {
                if (propertyElement.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException(
                        $"Property '{property.Name}' must be a JSON array.");
                continue;
            }

            if (!IsCompatibleJsonValue(propertyElement, property.TypeName))
                throw new InvalidOperationException(
                    $"Property '{property.Name}' does not match declared type '{property.TypeName}'.");
        }
    }

    private static bool IsCompatibleJsonValue(JsonElement value, string typeName)
    {
        var normalizedType = typeName.TrimEnd('?');
        return normalizedType switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "int" or "long" or "double" or "decimal" => value.ValueKind == JsonValueKind.Number,
            "bool" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "Guid" or "DateTime" or "DateTimeOffset" or "TimeSpan" => value.ValueKind == JsonValueKind.String,
            _ => value.ValueKind == JsonValueKind.Object
        };
    }
}
