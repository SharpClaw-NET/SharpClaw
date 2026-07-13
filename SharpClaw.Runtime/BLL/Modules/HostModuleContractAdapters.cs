using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Runtime.BLL.Modules.Foreign;
using SharpClaw.Contracts.Chat;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Contracts.Entities.Core.Messages;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Agents;
using SharpClaw.Contracts.DTOs.Threads;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Shared.Security;
using SharpClaw.Contracts.Enums;
using SharpClaw.Core.Clients;
using SharpClaw.Core.Conversation;
using SharpClaw.Core.Modules;
using SharpClaw.Contracts.Modules.Foreign;

namespace SharpClaw.Runtime.BLL.Modules;

public interface IHostContextDataReader
{
    Task<IReadOnlyList<ThreadSummary>> GetAccessibleThreadsAsync(
        Guid agentId,
        Guid currentChannelId,
        string crossThreadPermissionKey,
        CancellationToken ct = default);

    Task<IReadOnlyList<HostContextChatMessageSummary>> GetThreadMessagesAsync(
        Guid threadId,
        int maxMessages,
        CancellationToken ct = default);
}

public sealed class HostConversationSteering(
    SharpClawDbContext db,
    ThreadActivitySignal threadActivity,
    ConversationSteeringEngine steering) : IConversationSteering
{
    public async Task<ConversationSteeringResponse> AddAsync(
        ConversationSteeringRequest request,
        CancellationToken ct = default)
    {
        var prepared = steering.PrepareAdd(request);

        await ValidateTargetAsync(prepared.ChannelId, prepared.ThreadId, ct);

        var message = new ChatMessageDB
        {
            Role = prepared.Role,
            Origin = prepared.Origin,
            Content = prepared.Content,
            ChannelId = prepared.ChannelId,
            ThreadId = prepared.ThreadId,
            ClientType = prepared.ClientType,
            ProviderMetadataJson = prepared.ProviderMetadataJson,
        };

        db.ChatMessages.Add(message);
        await db.SaveChangesAsync(ct);

        if (prepared.ThreadId is { } threadId)
            threadActivity.Publish(threadId, new ThreadActivityEvent(
                ThreadActivityEventType.NewMessages,
                message.ClientType));

        return steering.ToResponse(ToStoredMessage(message));
    }

    public async Task<IReadOnlyList<ConversationSteeringResponse>> ListAsync(
        Guid channelId,
        Guid? threadId = null,
        int limit = 20,
        CancellationToken ct = default)
    {
        await ValidateTargetAsync(channelId, threadId, ct);
        limit = steering.ClampListLimit(limit);

        var query = db.ChatMessages
            .AsNoTracking()
            .Where(message =>
                message.ChannelId == channelId
                && message.ThreadId == threadId
                && message.Role == SteeringSystemRole
                && message.Content.StartsWith(ConversationSteeringEngine.ContentPrefix));

        var rows = await query
            .OrderByDescending(message => message.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

        return steering.ToListResponse(rows.Select(ToStoredMessage));
    }

    private async Task ValidateTargetAsync(
        Guid channelId,
        Guid? threadId,
        CancellationToken ct)
    {
        if (channelId == Guid.Empty)
        {
            steering.EnsureTargetValid(
                channelId,
                threadId: null,
                channelExists: true,
                threadChannelId: null);
        }

        if (threadId is null)
        {
            var channelExists = await db.Channels.AnyAsync(channel => channel.Id == channelId, ct);
            steering.EnsureTargetValid(
                channelId,
                threadId: null,
                channelExists,
                threadChannelId: null);
            return;
        }

        var thread = await db.ChatThreads
            .AsNoTracking()
            .Where(row => row.Id == threadId.Value)
            .Select(row => new { row.Id, row.ChannelId })
            .FirstOrDefaultAsync(ct);

        steering.EnsureTargetValid(
            channelId,
            threadId,
            channelExists: false,
            thread?.ChannelId);
    }

    private const string SteeringSystemRole = ChatRoles.System;

    private static ConversationSteeringStoredMessage ToStoredMessage(
        ChatMessageDB message) =>
        new(
            message.Id,
            message.ChannelId,
            message.ThreadId,
            message.Content,
            message.CreatedAt,
            message.ProviderMetadataJson);
}

public sealed class HostAgentManager(
    AgentService agents,
    SharpClawDbContext db) : IAgentManager
{
    public async Task<(Guid AgentId, string ModelName, string AgentName)> CreateSubAgentAsync(
        string name, Guid modelId, string? systemPrompt, CancellationToken ct = default)
    {
        var agent = await agents.CreateAsync(new CreateAgentRequest(name, modelId, systemPrompt), ct);
        return (agent.Id, agent.ModelName, agent.Name);
    }

    public async Task<string> UpdateAgentAsync(
        Guid agentId, string? name, string? systemPrompt, Guid? modelId, CancellationToken ct = default)
    {
        if (name is null && systemPrompt is null && modelId is null)
            return $"Agent (id={agentId}) — no changes applied.";

        var agent = await agents.UpdateAsync(agentId, new UpdateAgentRequest(
            Name: name,
            ModelId: modelId,
            SystemPrompt: systemPrompt), ct)
            ?? throw new InvalidOperationException($"Agent {agentId} not found.");

        return $"Updated agent '{agent.Name}' (id={agent.Id}).";
    }

    public async Task SetAgentHeaderAsync(Guid agentId, string? header, CancellationToken ct = default)
    {
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, ct)
            ?? throw new InvalidOperationException($"Agent {agentId} not found.");

        agent.CustomChatHeader = string.IsNullOrEmpty(header) ? null : header;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetChannelHeaderAsync(Guid channelId, string? header, CancellationToken ct = default)
    {
        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, ct)
            ?? throw new InvalidOperationException($"Channel {channelId} not found.");

        channel.CustomChatHeader = string.IsNullOrEmpty(header) ? null : header;
        await db.SaveChangesAsync(ct);
    }
}

public sealed class HostAgentJobReader(AgentJobService jobs) : IAgentJobReader
{
    public Task<AgentJobResponse?> GetJobAsync(Guid jobId, CancellationToken ct = default) =>
        jobs.GetAsync(jobId, ct);

    public Task<IReadOnlyList<AgentJobResponse>> ListJobsByActionPrefixAsync(
        string actionKeyPrefix,
        Guid? resourceId = null,
        CancellationToken ct = default) =>
        jobs.ListJobsByActionPrefixAsync(actionKeyPrefix, resourceId, ct);

    public Task<IReadOnlyList<AgentJobSummaryResponse>> ListJobSummariesByActionPrefixAsync(
        string actionKeyPrefix,
        Guid? resourceId = null,
        CancellationToken ct = default) =>
        jobs.ListJobSummariesByActionPrefixAsync(actionKeyPrefix, resourceId, ct);

    public Task<bool> JobExistsWithActionPrefixAsync(
        Guid jobId, string actionKeyPrefix, CancellationToken ct = default) =>
        jobs.JobExistsWithActionPrefixAsync(jobId, actionKeyPrefix, ct);
}

public sealed class HostModelInfoProvider(
    IServiceScopeFactory scopeFactory,
    ProviderApiClientFactory clientFactory) : IModelInfoProvider
{
    public async Task<ModelProviderInfo?> GetModelProviderInfoAsync(Guid modelId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();

        var model = await db.Models
            .Include(m => m.Provider)
            .FirstOrDefaultAsync(m => m.Id == modelId, ct);

        if (model is null)
            return null;

        var plugin = clientFactory.GetPlugin(model.Provider.ProviderKey);
        var requiresApiKey = plugin?.RequiresApiKey ?? true;
        var hasApiKey = !string.IsNullOrEmpty(model.Provider.EncryptedApiKey);

        return new ModelProviderInfo(
            model.Name,
            model.Provider.ProviderKey,
            requiresApiKey,
            hasApiKey);
    }
}

public sealed class HostInProcessModuleSecretReader(
    IServiceScopeFactory scopeFactory,
    Contracts.Persistence.EncryptionOptions encryptionOptions) : IInProcessModuleSecretReader
{
    public async Task<string?> GetProviderApiKeyAsync(
        string providerKey,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();

        var encryptedApiKey = await db.Providers
            .AsNoTracking()
            .Where(provider => provider.ProviderKey == providerKey)
            .Select(provider => provider.EncryptedApiKey)
            .FirstOrDefaultAsync(ct);

        return DecryptOrNull(encryptedApiKey);
    }

    public async Task<string?> GetModelProviderApiKeyAsync(
        Guid modelId,
        CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();

        var encryptedApiKey = await db.Models
            .AsNoTracking()
            .Where(model => model.Id == modelId)
            .Select(model => model.Provider.EncryptedApiKey)
            .FirstOrDefaultAsync(ct);

        return DecryptOrNull(encryptedApiKey);
    }

    private string? DecryptOrNull(string? encryptedApiKey)
    {
        if (string.IsNullOrEmpty(encryptedApiKey))
            return null;

        return ApiKeyEncryptor.DecryptOrPassthrough(
            encryptedApiKey,
            encryptionOptions.Key);
    }
}

/// <summary>
/// Host-side <see cref="IModelRegistrar"/> impl over the host
/// <see cref="SharpClawDbContext"/>. Modules call into this to upsert
/// <c>ProviderDB</c> / <c>ModelDB</c> rows when they own additional
/// related runtime state outside the host schema.
/// </summary>
public sealed class HostModelRegistrar(IServiceScopeFactory scopeFactory) : IModelRegistrar
{
    public async Task<Guid> EnsureProviderAsync(
        string providerKey, string displayName, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();

        var existing = await db.Providers
            .FirstOrDefaultAsync(p => p.ProviderKey == providerKey, ct);
        if (existing is not null) return existing.Id;

        var provider = new ProviderDB
        {
            Name = displayName,
            ProviderKey = providerKey,
        };
        db.Providers.Add(provider);
        await db.SaveChangesAsync(ct);
        return provider.Id;
    }

    public async Task<Guid> EnsureModelAsync(
        string modelName, Guid providerId, IReadOnlyList<string> capabilityTags,
        CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();

        var existing = await db.Models
            .FirstOrDefaultAsync(m => m.Name == modelName && m.ProviderId == providerId, ct);
        if (existing is not null) return existing.Id;

        var model = new ModelDB
        {
            Name = modelName,
            ProviderId = providerId,
            CapabilityTagsRaw = capabilityTags.Count == 0 ? null : string.Join(',', capabilityTags),
        };
        db.Models.Add(model);
        await db.SaveChangesAsync(ct);
        return model.Id;
    }

    public async Task<ModelMetadata?> GetModelMetadataAsync(
        Guid modelId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();

        var model = await db.Models
            .Include(m => m.Provider)
            .FirstOrDefaultAsync(m => m.Id == modelId, ct);
        if (model is null) return null;

        return new ModelMetadata(
            model.Name,
            model.ProviderId,
            model.Provider.Name,
            model.Provider.ProviderKey,
            model.CustomId,
            model.CapabilityTags);
    }

    public async Task<bool> DeleteModelAsync(Guid modelId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();

        var model = await db.Models.FirstOrDefaultAsync(m => m.Id == modelId, ct);
        if (model is null) return false;
        db.Models.Remove(model);
        await db.SaveChangesAsync(ct);
        return true;
    }
}

public sealed class HostThreadResolver(ThreadService threads) : IThreadResolver
{
    public async Task<Guid> ResolveOrCreateAsync(Guid channelId, CancellationToken ct = default)
    {
        var existing = await threads.ListAsync(channelId, ct);
        if (existing.Count > 0)
            return existing[0].Id;

        var created = await threads.CreateAsync(
            channelId,
            new CreateThreadRequest("Default"),
            ct);

        return created.Id;
    }
}

public sealed class HostContextDataReader(SharpClawDbContext db) : IHostContextDataReader
{
    public async Task<IReadOnlyList<ThreadSummary>> GetAccessibleThreadsAsync(
        Guid agentId,
        Guid currentChannelId,
        string crossThreadPermissionKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(crossThreadPermissionKey))
            return [];

        var agentWithRole = await db.Agents
            .Include(a => a.Role)
                .ThenInclude(r => r!.PermissionSet)
                    .ThenInclude(ps => ps!.GlobalFlags)
            .FirstOrDefaultAsync(a => a.Id == agentId, ct);

        var agentPs = agentWithRole?.Role?.PermissionSet;
        if (agentPs is null || !agentPs.GlobalFlags.Any(f => f.FlagKey == crossThreadPermissionKey))
            return [];

        var isIndependent = (agentPs.GlobalFlags
            .FirstOrDefault(f => f.FlagKey == crossThreadPermissionKey)
            ?.Clearance ?? PermissionClearance.Unset) == PermissionClearance.Independent;

        var channels = await db.Channels
            .Include(c => c.AllowedAgents)
            .Include(c => c.PermissionSet)
                .ThenInclude(ps => ps!.GlobalFlags)
            .Include(c => c.AgentContext)
                .ThenInclude(ctx => ctx!.PermissionSet)
                    .ThenInclude(ps => ps!.GlobalFlags)
            .Include(c => c.AgentContext)
                .ThenInclude(ctx => ctx!.AllowedAgents)
            .Where(c => c.Id != currentChannelId)
            .Where(c =>
                c.AgentId == agentId ||
                c.AllowedAgents.Any(a => a.Id == agentId) ||
                (c.AgentId == null && c.AgentContext != null && c.AgentContext.AgentId == agentId) ||
                (!c.AllowedAgents.Any() && c.AgentContext != null &&
                    c.AgentContext.AllowedAgents.Any(a => a.Id == agentId)))
            .ToListAsync(ct);

        if (!isIndependent)
        {
            channels = channels
                .Where(c =>
                {
                    var effectivePs = c.PermissionSet ?? c.AgentContext?.PermissionSet;
                    return effectivePs?.GlobalFlags.Any(f => f.FlagKey == crossThreadPermissionKey) == true;
                })
                .ToList();
        }

        if (channels.Count == 0)
            return [];

        var channelIds = channels.Select(c => c.Id).ToList();
        var channelTitles = channels.ToDictionary(c => c.Id, c => c.Title);

        var threads = await db.ChatThreads
            .Where(t => channelIds.Contains(t.ChannelId))
            .OrderByDescending(t => t.UpdatedAt)
            .Select(t => new { t.Id, t.Name, t.ChannelId })
            .ToListAsync(ct);

        return [.. threads.Select(t => new ThreadSummary(
            t.Id,
            t.Name,
            t.ChannelId,
            channelTitles[t.ChannelId]))];
    }

    public async Task<IReadOnlyList<HostContextChatMessageSummary>> GetThreadMessagesAsync(
        Guid threadId,
        int maxMessages,
        CancellationToken ct = default)
    {
        maxMessages = Math.Clamp(maxMessages, 1, 200);

        return await db.ChatMessages
            .Where(m => m.ThreadId == threadId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(maxMessages)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new HostContextChatMessageSummary(
                m.Role,
                m.Content,
                m.SenderUsername ?? m.SenderAgentName ?? "unknown",
                m.CreatedAt))
            .ToListAsync(ct);
    }
}

public sealed class HostContainerProvisioner(SharpClawDbContext db) : IContainerProvisioner
{
    public async Task CreateOwnerRoleAsync(
        Guid containerId,
        string containerName,
        string accessContainerActionName,
        string executeSafeShellActionName,
        string containerTypeKey,
        Guid? userId,
        CancellationToken ct = default)
    {
        var permissionSet = new PermissionSetDB
        {
        };

        permissionSet.ResourceAccesses.Add(new ResourceAccessDB
        {
            ResourceType = accessContainerActionName,
            ResourceId = containerId,
            SubType = "",
            Clearance = PermissionClearance.Independent,
            AccessLevel = "ReadWrite",
            IsDefault = true,
        });

        permissionSet.ResourceAccesses.Add(new ResourceAccessDB
        {
            ResourceType = containerTypeKey,
            ResourceId = containerId,
            SubType = "",
            Clearance = PermissionClearance.Independent,
            AccessLevel = "ReadWrite",
            IsDefault = true,
        });

        var role = new RoleDB
        {
            Name = $"Container Owner: {containerName}",
            PermissionSet = permissionSet,
        };

        db.Roles.Add(role);
        await db.SaveChangesAsync(ct);

        if (userId is not { } id)
            return;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is not null && user.RoleId is null)
        {
            user.RoleId = role.Id;
            await db.SaveChangesAsync(ct);
        }
    }
}

public sealed class HostModuleInfoProvider(ModuleRegistry registry) : IModuleInfoProvider
{
    public IReadOnlyList<ModuleInfo> GetAllModules() =>
        registry.GetAllModules()
            .Select(m => new ModuleInfo(
                m.Id,
                m.ToolPrefix,
                m.ExportedContracts
                    .Select(e => e.ContractName)
                    .Concat(
                        m is IForeignModuleProtocolContractModule protocolModule
                            ? protocolModule.ExportedProtocolContracts.Select(e => e.ContractName)
                            : Enumerable.Empty<string>())
                    .ToList()))
            .ToList();
}

public sealed class HostModuleProtocolContractResolver(
    ModuleRegistry registry) : IForeignModuleProtocolContractResolver
{
    public IForeignModuleProtocolContractInvoker? Resolve(string contractName) =>
        registry.ResolveProtocolContractInvoker(contractName);

    public IReadOnlyList<ForeignModuleProtocolContractExport> GetAllExports() =>
        registry.GetAllProtocolContractExports();
}

public sealed class HostModuleLifecycleManager(
    IServiceScopeFactory scopeFactory,
    ModuleRegistry registry) : IModuleLifecycleManager
{
    public string ExternalModulesDir => ModuleService.ResolveExternalModulesDir();

    public bool IsModuleRegistered(string moduleId) =>
        registry.GetModule(moduleId) is not null;

    public bool IsToolPrefixRegistered(string toolPrefix) =>
        registry.GetModuleByPrefix(toolPrefix) is not null;

    public (ISharpClawCoreModule Module, string ToolName)? FindToolByName(string toolName) =>
        registry.FindToolByName(toolName);

    public async Task<Contracts.Modules.ModuleStateResponse> LoadExternalAsync(
        string moduleDir, IServiceProvider hostServices, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var modules = scope.ServiceProvider.GetRequiredService<ModuleService>();
        return await modules.LoadExternalAsync(moduleDir, hostServices, ct);
    }

    public async Task UnloadExternalAsync(string moduleId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var modules = scope.ServiceProvider.GetRequiredService<ModuleService>();
        await modules.UnloadExternalAsync(moduleId, ct);
    }

    public async Task<Contracts.Modules.ModuleStateResponse> ReloadExternalAsync(
        string moduleId, IServiceProvider hostServices, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var modules = scope.ServiceProvider.GetRequiredService<ModuleService>();
        return await modules.ReloadExternalAsync(moduleId, hostServices, ct);
    }
}
