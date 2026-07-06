using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Models;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Agents;
using SharpClaw.Core.Clients;
using SharpClaw.Core.Modules;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.BLL.Services;

public sealed class EfAgentAdministrationHost(
    SharpClawDbContext db,
    SessionService session,
    ModuleRegistry moduleRegistry,
    IConfiguration configuration,
    ProviderApiClientFactory clientFactory,
    ChatCache chatCache) : IAgentAdministrationHost
{
    public bool UniqueAgentNamesEnforced =>
        AgentAdministrationEngine.IsUniqueAgentNameEnforced(
            configuration["UniqueNames:Agents"]);

    public Guid? SessionUserId => session.UserId;

    public ModuleRegistry ModuleRegistry => moduleRegistry;

    public ICompletionParameterSpec GetParameterSpec(string providerKey)
    {
        return clientFactory.GetParameterSpec(providerKey);
    }

    public IProviderPlugin? GetProviderPlugin(string providerKey)
    {
        return clientFactory.GetPlugin(providerKey);
    }

    public async Task<ModelDB?> LoadModelAsync(
        Guid modelId,
        CancellationToken ct)
    {
        return await db.Models
            .Include(m => m.Provider)
            .FirstOrDefaultAsync(m => m.Id == modelId, ct);
    }

    public async Task<AgentDB?> LoadAgentAsync(
        Guid agentId,
        CancellationToken ct)
    {
        return await db.Agents
            .Include(a => a.Model).ThenInclude(m => m.Provider)
            .Include(a => a.Role)
            .FirstOrDefaultAsync(a => a.Id == agentId, ct);
    }

    public async Task<RoleDB?> LoadRoleAsync(
        Guid roleId,
        CancellationToken ct)
    {
        return await db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct);
    }

    public async Task<UserDB?> LoadUserAsync(
        Guid userId,
        CancellationToken ct)
    {
        return await db.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
    }

    public async Task<PermissionSetDB?> LoadFullPermissionSetAsync(
        Guid permissionSetId,
        CancellationToken ct)
    {
        return await db.PermissionSets
            .Include(p => p.GlobalFlags)
            .Include(p => p.ResourceAccesses)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == permissionSetId, ct);
    }

    public async Task<IReadOnlyList<ModelDB>> LoadChatCapableModelsAsync(
        CancellationToken ct)
    {
        return await db.Models
            .Include(m => m.Provider)
            .Where(m => m.CapabilityTagsRaw != null
                && m.CapabilityTagsRaw.Contains(WellKnownCapabilityKeys.Chat))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> ListAgentNamesAsync(
        Guid? excludeId,
        CancellationToken ct)
    {
        return await db.Agents
            .Where(a => excludeId == null || a.Id != excludeId)
            .Select(a => a.Name)
            .ToListAsync(ct);
    }

    public void TrackAgent(AgentDB agent)
    {
        db.Agents.Add(agent);
    }

    public void RemoveAgent(AgentDB agent)
    {
        db.Agents.Remove(agent);
    }

    public async Task SaveAsync(
        Func<ChatRuntimeInvalidationPlan?>? buildInvalidationPlan,
        CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        buildInvalidationPlan?.Invoke()?.ApplyTo(chatCache);
    }
}
