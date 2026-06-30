using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Entities.Core.Tasks;
using SharpClaw.Core.Permissions;
using SharpClaw.Core.Tasks;
using SharpClaw.Core.Tasks.Runtime;
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
    IServiceScopeFactory scopeFactory,
    RolePermissionAdministrationEngine rolePermissions,
    ChatCache chatCache) : IHostAgentBridge
{
    private readonly TaskStructuredResponseParser _structuredResponses = new();

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
        IReadOnlyList<SharpClaw.Core.Tasks.Models.TaskDataTypeDefinition>? dataTypes = null;
        if (!string.IsNullOrWhiteSpace(typeName))
        {
            var instance = db.TaskInstances
                .Include(i => i.TaskDefinition)
                .FirstOrDefault(i => i.Id == instanceId);
            if (instance?.TaskDefinition is not null)
            {
                var compileResult = TaskScriptEngine.ProcessScript(instance.TaskDefinition.SourceText, null);
                dataTypes = compileResult.Plan?.Definition.DataTypes;
            }
        }

        return _structuredResponses.Parse(text, typeName, dataTypes);
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
            InvalidateAgentRuntimeState();
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
            InvalidateAgentRuntimeState();
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
                InvalidateChannelRuntimeState();
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
        InvalidateThreadRuntimeState(thread.Id);
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

        await db.Entry(permissionSet)
            .Collection(ps => ps.GlobalFlags)
            .LoadAsync(ct);
        await db.Entry(permissionSet)
            .Collection(ps => ps.ResourceAccesses)
            .LoadAsync(ct);

        rolePermissions.ReconcilePermissionSet(permissionSet, permRequest);

        await db.SaveChangesAsync(ct);
        InvalidatePermissionRuntimeState();
    }

    public async Task AssignRoleAsync(Guid agentId, Guid roleId, CancellationToken ct)
    {
        var agentEntity = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, ct)
            ?? throw new InvalidOperationException($"AssignRole: agent '{agentId}' not found.");
        if (!await db.Roles.AnyAsync(r => r.Id == roleId, ct))
            throw new InvalidOperationException($"AssignRole: role '{roleId}' not found.");

        agentEntity.RoleId = roleId;
        await db.SaveChangesAsync(ct);
        InvalidateAgentRuntimeState();
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
            InvalidateChannelRuntimeState();
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
            InvalidateChannelRuntimeState();
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

    private void InvalidateAgentRuntimeState()
    {
        chatCache.RemoveByPrefix(ChatCache.PrefixHeaderAgentSuffix);
        chatCache.RemoveByPrefix(ChatCache.PrefixEffectiveTools);
    }

    private void InvalidateChannelRuntimeState()
    {
        chatCache.RemoveByPrefix(ChatCache.PrefixHeaderAgentSuffix);
        chatCache.RemoveByPrefix(ChatCache.PrefixEffectiveTools);
    }

    private void InvalidateThreadRuntimeState(Guid threadId)
    {
        chatCache.Remove(ChatCache.KeyThreadHistoryLimits(threadId));
        chatCache.RemoveByPrefix(ChatCache.PrefixHeaderAgentSuffix);
    }

    private void InvalidatePermissionRuntimeState()
    {
        chatCache.RemoveByPrefix(ChatCache.PrefixHeaderUser);
        InvalidateAgentRuntimeState();
    }

}
