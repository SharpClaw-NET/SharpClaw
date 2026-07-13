using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Channels;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Entities.Core.Tasks;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Core.Tasks.Runtime;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.BLL.Services;

/// <summary>
/// Application adapter for task-exposed host agent bridge operations. Core owns
/// bridge workflow sequencing; this type supplies EF, chat, role, and cache
/// operations.
/// </summary>
public sealed class HostAgentBridge(
    SharpClawDbContext db,
    TaskService taskService,
    ChatService chatService,
    IServiceScopeFactory scopeFactory,
    ChatCache chatCache,
    TaskHostBridgeWorkflowEngine workflow) :
    IHostAgentBridge,
    ITaskHostBridgeWorkflowHost
{
    private static readonly TaskHostBridgeInvalidationPlanner BridgeInvalidations =
        new();

    public Task<string?> ChatAsync(
        Guid instanceId,
        string taskName,
        string message,
        Guid? agentId,
        CancellationToken ct)
    {
        return workflow.ChatAsync(
            instanceId,
            taskName,
            message,
            agentId,
            this,
            ct);
    }

    public Task<string> ChatStreamAsync(
        Guid instanceId,
        string taskName,
        string message,
        Guid? agentId,
        CancellationToken ct)
    {
        return workflow.ChatStreamAsync(
            instanceId,
            taskName,
            message,
            agentId,
            this,
            ct);
    }

    public Task<string?> ChatToThreadAsync(
        Guid instanceId,
        string taskName,
        Guid threadId,
        string message,
        Guid? agentId,
        CancellationToken ct)
    {
        return workflow.ChatToThreadAsync(
            instanceId,
            taskName,
            threadId,
            message,
            agentId,
            this,
            ct);
    }

    public string ParseStructuredResponse(
        Guid instanceId,
        string text,
        string? typeName)
    {
        return workflow.ParseStructuredResponse(instanceId, text, typeName, this);
    }

    public Task<Guid?> FindModelAsync(string search, CancellationToken ct)
        => workflow.FindModelAsync(search, this, ct);

    public Task<Guid?> FindProviderAsync(string search, CancellationToken ct)
        => workflow.FindProviderAsync(search, this, ct);

    public Task<Guid?> FindAgentAsync(string search, CancellationToken ct)
        => workflow.FindAgentAsync(search, this, ct);

    public Task<Guid?> FindRoleAsync(string search, CancellationToken ct)
        => workflow.FindRoleAsync(search, this, ct);

    public Task<Guid?> FindChannelAsync(string search, CancellationToken ct)
        => workflow.FindChannelAsync(search, this, ct);

    public Task<Guid> CreateAgentAsync(
        Guid instanceId,
        string name,
        Guid modelId,
        string? systemPrompt,
        string? customId,
        CancellationToken ct)
    {
        return workflow.CreateAgentAsync(
            instanceId,
            name,
            modelId,
            systemPrompt,
            customId,
            this,
            ct);
    }

    public Task<Guid> CreateThreadAsync(
        Guid instanceId,
        Guid? channelId,
        string? threadName,
        CancellationToken ct)
    {
        return workflow.CreateThreadAsync(
            instanceId,
            channelId,
            threadName,
            this,
            ct);
    }

    public Task<Guid> CreateRoleAsync(string roleName, CancellationToken ct)
    {
        return workflow.CreateRoleAsync(roleName, this, ct);
    }

    public Task SetRolePermissionsAsync(
        Guid roleId,
        string requestJson,
        CancellationToken ct)
    {
        return workflow.SetRolePermissionsAsync(roleId, requestJson, this, ct);
    }

    public Task AssignRoleAsync(Guid agentId, Guid roleId, CancellationToken ct)
    {
        return workflow.AssignRoleAsync(agentId, roleId, this, ct);
    }

    public Task<Guid> CreateChannelAsync(
        Guid instanceId,
        string title,
        Guid agentId,
        string? customId,
        CancellationToken ct)
    {
        return workflow.CreateChannelAsync(
            instanceId,
            title,
            agentId,
            customId,
            this,
            ct);
    }

    public Task AddAllowedAgentAsync(
        Guid instanceId,
        Guid agentId,
        Guid? channelId,
        CancellationToken ct)
    {
        return workflow.AddAllowedAgentAsync(
            instanceId,
            agentId,
            channelId,
            this,
            ct);
    }

    public async Task<Guid?> LoadInstanceChannelIdAsync(
        Guid instanceId,
        CancellationToken ct)
    {
        return await db.TaskInstances
            .Where(instance => instance.Id == instanceId)
            .Select(instance => instance.ChannelId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<Guid?> LoadInstanceContextIdAsync(
        Guid instanceId,
        CancellationToken ct)
    {
        return await db.TaskInstances
            .Where(instance => instance.Id == instanceId)
            .Select(instance => instance.ContextId)
            .FirstOrDefaultAsync(ct);
    }

    public string? LoadTaskDefinitionSourceText(Guid instanceId)
    {
        return db.TaskInstances
            .Include(instance => instance.TaskDefinition)
            .Where(instance => instance.Id == instanceId)
            .Select(instance => instance.TaskDefinition == null
                ? null
                : instance.TaskDefinition.SourceText)
            .FirstOrDefault();
    }

    public async Task<ChatResponse> SendChatAsync(
        Guid channelId,
        ChatRequest request,
        Guid? threadId,
        CancellationToken ct)
    {
        return await chatService.SendMessageAsync(
            channelId,
            request,
            threadId: threadId,
            ct: ct);
    }

    public async IAsyncEnumerable<ChatStreamEvent> SendChatStreamAsync(
        Guid channelId,
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in chatService.SendMessageStreamAsync(
                           channelId,
                           request,
                           AutoApproveAsync,
                           ct: ct))
        {
            yield return evt;
        }
    }

    public Task AppendTaskLogAsync(
        Guid instanceId,
        string message,
        CancellationToken ct)
    {
        return taskService.AppendLogAsync(instanceId, message, ct: ct);
    }

    public async Task<Guid?> FindIdAsync(
        TaskHostBridgeLookupKind kind,
        string search,
        CancellationToken ct)
    {
        return kind switch
        {
            TaskHostBridgeLookupKind.Model => await db.Models
                .Where(model => model.CustomId == search || model.Name == search)
                .Select(model => (Guid?)model.Id)
                .FirstOrDefaultAsync(ct),
            TaskHostBridgeLookupKind.Provider => await db.Providers
                .Where(provider => provider.CustomId == search
                    || provider.Name == search)
                .Select(provider => (Guid?)provider.Id)
                .FirstOrDefaultAsync(ct),
            TaskHostBridgeLookupKind.Agent => await db.Agents
                .Where(agent => agent.CustomId == search || agent.Name == search)
                .Select(agent => (Guid?)agent.Id)
                .FirstOrDefaultAsync(ct),
            TaskHostBridgeLookupKind.Role => await db.Roles
                .Where(role => role.Name == search)
                .Select(role => (Guid?)role.Id)
                .FirstOrDefaultAsync(ct),
            TaskHostBridgeLookupKind.Channel => await db.Channels
                .Where(channel => channel.CustomId == search
                    || channel.Title == search)
                .Select(channel => (Guid?)channel.Id)
                .FirstOrDefaultAsync(ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }

    public async Task<AgentDB?> LoadLatestAgentByCustomIdAsync(
        string customId,
        CancellationToken ct)
    {
        return await db.Agents
            .Where(agent => agent.CustomId == customId)
            .OrderByDescending(agent => agent.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public void TrackAgent(AgentDB agent)
    {
        db.Agents.Add(agent);
    }

    public async Task<ChannelDB?> LoadChannelWithAllowedAgentsAsync(
        Guid channelId,
        CancellationToken ct)
    {
        return await db.Channels
            .Include(channel => channel.AllowedAgents)
            .FirstOrDefaultAsync(channel => channel.Id == channelId, ct);
    }

    public void TrackThread(ChatThreadDB thread)
    {
        db.ChatThreads.Add(thread);
    }

    public async Task<RoleDB?> LoadRoleByNameAsync(
        string roleName,
        CancellationToken ct)
    {
        return await db.Roles.FirstOrDefaultAsync(
            role => role.Name == roleName,
            ct);
    }

    async Task<Guid> ITaskHostBridgeWorkflowHost.CreateRoleAsync(
        string roleName,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var roleService = scope.ServiceProvider.GetRequiredService<RoleService>();
        var created = await roleService.CreateAsync(roleName, ct);
        return created.Id;
    }

    public async Task<RoleDB?> LoadRoleWithPermissionSetAsync(
        Guid roleId,
        CancellationToken ct)
    {
        return await db.Roles
            .Include(role => role.PermissionSet)
            .FirstOrDefaultAsync(role => role.Id == roleId, ct);
    }

    public async Task<PermissionSetDB> EnsureRolePermissionSetAsync(
        RoleDB role,
        CancellationToken ct)
    {
        if (role.PermissionSet is { } existing)
            return existing;

        var permissionSet = new PermissionSetDB();
        db.PermissionSets.Add(permissionSet);
        await db.SaveChangesAsync(ct);
        role.PermissionSetId = permissionSet.Id;
        role.PermissionSet = permissionSet;
        return permissionSet;
    }

    public async Task LoadPermissionSetCollectionsAsync(
        PermissionSetDB permissionSet,
        CancellationToken ct)
    {
        await db.Entry(permissionSet)
            .Collection(set => set.GlobalFlags)
            .LoadAsync(ct);
        await db.Entry(permissionSet)
            .Collection(set => set.ResourceAccesses)
            .LoadAsync(ct);
    }

    public async Task<AgentDB?> LoadAgentAsync(Guid agentId, CancellationToken ct)
    {
        return await db.Agents.FirstOrDefaultAsync(
            agent => agent.Id == agentId,
            ct);
    }

    public async Task<bool> RoleExistsAsync(Guid roleId, CancellationToken ct)
    {
        return await db.Roles.AnyAsync(role => role.Id == roleId, ct);
    }

    public async Task<ChannelDB?> LoadChannelByCustomIdAsync(
        string customId,
        CancellationToken ct)
    {
        return await db.Channels.FirstOrDefaultAsync(
            channel => channel.CustomId == customId,
            ct);
    }

    public async Task<ChannelDB?> LoadChannelByTitleAsync(
        string title,
        CancellationToken ct)
    {
        return await db.Channels.FirstOrDefaultAsync(
            channel => channel.Title == title,
            ct);
    }

    async Task<Guid> ITaskHostBridgeWorkflowHost.CreateChannelAsync(
        CreateChannelRequest request,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var channelService = scope.ServiceProvider
            .GetRequiredService<ChannelService>();
        var response = await channelService.CreateAsync(request, ct);
        return response.Id;
    }

    public async Task<TaskInstanceDB?> LoadTaskInstanceAsync(
        Guid instanceId,
        CancellationToken ct)
    {
        return await db.TaskInstances.FindAsync([instanceId], ct);
    }

    public Task SaveAsync(CancellationToken ct)
    {
        return db.SaveChangesAsync(ct);
    }

    public void Invalidate(
        TaskHostBridgeInvalidationTarget target,
        Guid? entityId = null)
    {
        ApplyBridgeInvalidationPlan(
            BridgeInvalidations.BuildPlan(target, entityId));
    }

    private static Task<bool> AutoApproveAsync(
        AgentJobResponse _,
        CancellationToken __)
    {
        return Task.FromResult(false);
    }

    private void ApplyBridgeInvalidationPlan(
        ChatRuntimeInvalidationPlan plan)
    {
        foreach (var invalidation in plan.Invalidations)
            ApplyBridgeInvalidation(invalidation);
    }

    private void ApplyBridgeInvalidation(ChatCacheInvalidation invalidation)
    {
        switch (invalidation.Kind)
        {
            case ChatCacheInvalidationKind.Key:
                chatCache.Remove(invalidation.Value ?? throw MissingInvalidationValue());
                break;
            case ChatCacheInvalidationKind.Prefix:
                chatCache.RemoveByPrefix(invalidation.Value ?? throw MissingInvalidationValue());
                break;
            case ChatCacheInvalidationKind.HeaderAgentSuffixesForAgent:
                chatCache.RemoveHeaderAgentSuffixesForAgent(
                    invalidation.Id ?? throw MissingInvalidationId());
                break;
            case ChatCacheInvalidationKind.HeaderAgentSuffixesForChannel:
                chatCache.RemoveHeaderAgentSuffixesForChannel(
                    invalidation.Id ?? throw MissingInvalidationId());
                break;
            case ChatCacheInvalidationKind.EffectiveToolsForAgent:
                chatCache.RemoveEffectiveToolsForAgent(
                    invalidation.Id ?? throw MissingInvalidationId());
                break;
            case ChatCacheInvalidationKind.DefaultResourceResolutionForChannel:
                chatCache.RemoveDefaultResourceResolutionForChannel(
                    invalidation.Id ?? throw MissingInvalidationId());
                break;
            case ChatCacheInvalidationKind.DefaultResourceResolutionForAgent:
                chatCache.RemoveDefaultResourceResolutionForAgent(
                    invalidation.Id ?? throw MissingInvalidationId());
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown cache invalidation kind '{invalidation.Kind}'.");
        }
    }

    private static InvalidOperationException MissingInvalidationValue() =>
        new("Cache invalidation value is required.");

    private static InvalidOperationException MissingInvalidationId() =>
        new("Cache invalidation id is required.");
}
