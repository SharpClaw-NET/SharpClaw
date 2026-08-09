using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Runtime.INF.Persistence.Entities.Core.Clearance;
using SharpClaw.Contracts.Models;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Agents;
using SharpClaw.Core.Clients;
using SharpClaw.Core.Modules;
using SharpClaw.Core.State;
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
    private readonly CoreStateSession _states = new(db);

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

    public async Task<ModelState?> LoadModelAsync(
        Guid modelId,
        CancellationToken ct)
    {
        var entity = await db.Models
            .Include(m => m.Provider)
            .FirstOrDefaultAsync(m => m.Id == modelId, ct);
        return entity is null ? null : _states.Map(entity);
    }

    public async Task<AgentState?> LoadAgentAsync(
        Guid agentId,
        CancellationToken ct)
    {
        var entity = await db.Agents
            .Include(a => a.Model).ThenInclude(m => m.Provider)
            .Include(a => a.Role)
            .FirstOrDefaultAsync(a => a.Id == agentId, ct);
        return entity is null ? null : _states.Map(entity);
    }

    public async Task<RoleState?> LoadRoleAsync(
        Guid roleId,
        CancellationToken ct)
    {
        var entity = await db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct);
        return entity is null ? null : _states.Map(entity);
    }

    public async Task<UserState?> LoadUserAsync(
        Guid userId,
        CancellationToken ct)
    {
        var entity = await db.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
        return entity is null ? null : _states.Map(entity);
    }

    public async Task<PermissionSetState?> LoadFullPermissionSetAsync(
        Guid permissionSetId,
        CancellationToken ct)
    {
        var entity = await db.PermissionSets
            .Include(p => p.GlobalFlags)
            .Include(p => p.ResourceAccesses)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == permissionSetId, ct);
        return entity is null ? null : _states.Map(entity);
    }

    public async Task<IReadOnlyList<ModelState>> LoadChatCapableModelsAsync(
        CancellationToken ct)
    {
        var entities = await db.Models
            .Include(m => m.Provider)
            .Where(m => m.CapabilityTagsRaw != null
                && m.CapabilityTagsRaw.Contains(WellKnownCapabilityKeys.Chat))
            .ToListAsync(ct);
        return _states.Map(entities);
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

    public void TrackAgent(AgentState agent)
    {
        _states.Track(agent);
    }

    public void RemoveAgent(AgentState agent)
    {
        _states.Remove(agent);
    }

    public async Task SaveAsync(
        Func<ChatRuntimeInvalidationPlan?>? buildInvalidationPlan,
        CancellationToken ct)
    {
        await _states.SaveChangesAsync(ct);
        buildInvalidationPlan?.Invoke()?.ApplyTo(chatCache);
    }
}
