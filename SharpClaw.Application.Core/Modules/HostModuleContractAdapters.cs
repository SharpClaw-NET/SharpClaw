using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Application.Infrastructure.Models.Access;
using SharpClaw.Application.Infrastructure.Models.Context;
using SharpClaw.Application.Infrastructure.Models.Jobs;
using SharpClaw.Application.Services;
using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Agents;
using SharpClaw.Contracts.DTOs.Threads;
using SharpClaw.Contracts.Modules;
using SharpClaw.Infrastructure.Models;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Utils.Security;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Core.Modules;

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
    Contracts.Persistence.EncryptionOptions encryptionOptions) : IModelInfoProvider
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

        var apiKey = string.IsNullOrEmpty(model.Provider.EncryptedApiKey)
            ? string.Empty
            : ApiKeyEncryptor.DecryptOrPassthrough(model.Provider.EncryptedApiKey, encryptionOptions.Key);

        return new ModelProviderInfo(model.Name, model.Provider.ProviderKey, apiKey);
    }

    public async Task<string?> GetLocalModelFilePathAsync(Guid modelId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var lookup = scope.ServiceProvider.GetService<ILocalModelLookup>();
        if (lookup is null)
            return null;
        return await lookup.GetReadyFilePathAsync(modelId, ct);
    }
}

/// <summary>
/// Host-side <see cref="IModelRegistrar"/> impl over the host
/// <see cref="SharpClawDbContext"/>. Modules call into this to upsert
/// <c>ProviderDB</c> / <c>ModelDB</c> rows when they own additional
/// related state (e.g. LlamaSharp module owning <c>LocalModelFileDB</c>).
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

public sealed class HostContextDataReader(SharpClawDbContext db) : IContextDataReader
{
    public Task<bool> ThreadExistsAsync(Guid threadId, Guid channelId, CancellationToken ct = default) =>
        db.ChatThreads.AnyAsync(t => t.Id == threadId && t.ChannelId == channelId, ct);

    public async Task<IReadOnlyList<ChatMessageSummary>> GetThreadMessagesAsync(
        Guid threadId, int maxMessages, CancellationToken ct = default)
    {
        return await db.ChatMessages
            .Where(m => m.ThreadId == threadId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(maxMessages)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new ChatMessageSummary(
                m.Role,
                m.Content,
                m.SenderUsername ?? m.SenderAgentName ?? "unknown",
                m.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ThreadSummary>> GetAccessibleThreadsAsync(
        Guid agentId, Guid currentChannelId, CancellationToken ct = default)
    {
        var agentWithRole = await db.Agents
            .Include(a => a.Role)
                .ThenInclude(r => r!.PermissionSet)
                    .ThenInclude(ps => ps!.GlobalFlags)
            .FirstOrDefaultAsync(a => a.Id == agentId, ct);

        var agentPs = agentWithRole?.Role?.PermissionSet;
        if (agentPs is null || !agentPs.GlobalFlags.Any(f => f.FlagKey == "CanReadCrossThreadHistory"))
            return [];

        var isIndependent = (agentPs.GlobalFlags
            .FirstOrDefault(f => f.FlagKey == "CanReadCrossThreadHistory")
            ?.Clearance ?? PermissionClearance.Unset) == PermissionClearance.Independent;

        var channels = await db.Channels
            .Include(c => c.AllowedAgents)
            .Include(c => c.PermissionSet)
                .ThenInclude(ps => ps!.GlobalFlags)
            .Include(c => c.AgentContext)
                .ThenInclude(ctx => ctx!.PermissionSet)
                    .ThenInclude(ps => ps!.GlobalFlags)
            .Where(c => c.Id != currentChannelId)
            .Where(c => c.AgentId == agentId || c.AllowedAgents.Any(a => a.Id == agentId))
            .ToListAsync(ct);

        if (!isIndependent)
        {
            channels = channels
                .Where(c =>
                {
                    var effectivePs = c.PermissionSet ?? c.AgentContext?.PermissionSet;
                    return effectivePs?.GlobalFlags.Any(f => f.FlagKey == "CanReadCrossThreadHistory") == true;
                })
                .ToList();
        }

        if (channels.Count == 0)
            return [];

        var channelIds = channels.Select(c => c.Id).ToList();
        var channelTitles = channels.ToDictionary(c => c.Id, c => c.Title);

        return await db.ChatThreads
            .Where(t => channelIds.Contains(t.ChannelId))
            .OrderByDescending(t => t.UpdatedAt)
            .Select(t => new ThreadSummary(t.Id, t.Name, t.ChannelId, channelTitles[t.ChannelId]))
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

        var role = new Application.Infrastructure.Models.Clearance.RoleDB
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
                m.ExportedContracts.Select(e => e.ContractName).ToList()))
            .ToList();
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

    public (ISharpClawModule Module, string ToolName)? FindToolByName(string toolName) =>
        registry.FindToolByName(toolName);

    public async Task<Contracts.Modules.ModuleStateResponse> LoadExternalAsync(
        string moduleDir, IServiceProvider hostServices, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var modules = scope.ServiceProvider.GetRequiredService<ModuleService>();
        return ToContractResponse(await modules.LoadExternalAsync(moduleDir, hostServices, ct));
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
        return ToContractResponse(await modules.ReloadExternalAsync(moduleId, hostServices, ct));
    }

    private static Contracts.Modules.ModuleStateResponse ToContractResponse(
        Application.Services.ModuleStateResponse response) => new(
            response.ModuleId,
            response.DisplayName,
            response.ToolPrefix,
            response.Enabled,
            response.Version,
            response.Registered,
            response.IsExternal,
            response.CreatedAt,
            response.UpdatedAt);
}
