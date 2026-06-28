using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SharpClaw.Core.Agents;
using SharpClaw.Core.Chat;
using SharpClaw.Core.Clients;
using SharpClaw.Core.Modules;
using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.Agents;
using SharpClaw.Contracts.DTOs.Auth;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Models;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

public sealed class AgentService(
    SharpClawDbContext db,
    SessionService session,
    ModuleRegistry moduleRegistry,
    IConfiguration configuration,
    ProviderApiClientFactory clientFactory,
    ChatCache chatCache,
    AgentAdministrationEngine agentAdministration)
{
    public async Task<AgentResponse> CreateAsync(
        CreateAgentRequest request,
        CancellationToken ct = default)
    {
        if (IsUniqueAgentNamesEnforced())
            await EnsureAgentNameUniqueAsync(request.Name, excludeId: null, ct);

        var model = await db.Models
            .Include(m => m.Provider)
            .FirstOrDefaultAsync(m => m.Id == request.ModelId, ct)
            ?? throw new ArgumentException($"Model {request.ModelId} not found.");

        var agent = agentAdministration.Create(
            request,
            model,
            clientFactory.GetParameterSpec(model.Provider.ProviderKey));

        db.Agents.Add(agent);
        await db.SaveChangesAsync(ct);

        return agentAdministration.ToResponse(agent, model);
    }

    public async Task<AgentResponse?> GetByNameAsync(
        string name,
        CancellationToken ct = default)
    {
        var agent = await db.Agents
            .Include(a => a.Model).ThenInclude(m => m.Provider)
            .Include(a => a.Role)
            .FirstOrDefaultAsync(a => a.Name == name, ct);

        return agent is null
            ? null
            : agentAdministration.ToResponse(agent, agent.Model);
    }

    public async Task<AgentResponse?> GetByIdAsync(
        Guid id,
        CancellationToken ct = default)
    {
        var agent = await db.Agents
            .Include(a => a.Model).ThenInclude(m => m.Provider)
            .Include(a => a.Role)
            .FirstOrDefaultAsync(a => a.Id == id, ct);

        return agent is null
            ? null
            : agentAdministration.ToResponse(agent, agent.Model);
    }

    public async Task<IReadOnlyList<AgentResponse>> ListAsync(
        CancellationToken ct = default)
    {
        return await db.Agents
            .Select(agentAdministration.ToResponseProjection())
            .ToListAsync(ct);
    }

    public async Task<AgentResponse?> UpdateAsync(
        Guid id,
        UpdateAgentRequest request,
        CancellationToken ct = default)
    {
        var agent = await db.Agents
            .Include(a => a.Model).ThenInclude(m => m.Provider)
            .Include(a => a.Role)
            .FirstOrDefaultAsync(a => a.Id == id, ct);
        if (agent is null)
            return null;

        ModelDB? replacementModel = null;
        if (request.ModelId is { } modelId)
        {
            replacementModel = await db.Models
                .Include(m => m.Provider)
                .FirstOrDefaultAsync(m => m.Id == modelId, ct)
                ?? throw new ArgumentException($"Model {modelId} not found.");
        }

        var effectiveModel = replacementModel ?? agent.Model;
        var enforceUniqueNames = IsUniqueAgentNamesEnforced();
        IReadOnlyList<string> existingNames = enforceUniqueNames
            ? await LoadAgentNamesAsync(excludeId: id, ct)
            : Array.Empty<string>();

        agentAdministration.ApplyUpdate(
            agent,
            request,
            replacementModel,
            clientFactory.GetParameterSpec(effectiveModel.Provider.ProviderKey),
            enforceUniqueNames,
            existingNames);

        await db.SaveChangesAsync(ct);
        InvalidateAgentRuntimeState(id);
        return agentAdministration.ToResponse(agent, agent.Model);
    }

    /// <summary>
    /// Assigns or removes a role on an agent. The calling user must either
    /// hold the exact same role, or hold a role whose permission set covers
    /// every permission in the target role at the same or higher clearance
    /// level.
    /// </summary>
    public async Task<AgentResponse?> AssignRoleAsync(
        Guid agentId,
        Guid roleId,
        CancellationToken ct = default)
    {
        var agent = await db.Agents
            .Include(a => a.Model).ThenInclude(m => m.Provider)
            .Include(a => a.Role)
            .FirstOrDefaultAsync(a => a.Id == agentId, ct);
        if (agent is null)
            return null;

        RoleDB? role = null;
        Guid? callerRoleId = null;
        PermissionSetDB? callerPermissions = null;
        PermissionSetDB? targetPermissions = null;

        if (roleId != Guid.Empty)
        {
            role = await db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct)
                ?? throw new ArgumentException($"Role {roleId} not found.");

            var callerUserId = session.UserId
                ?? throw new UnauthorizedAccessException(
                    "A logged-in user is required to assign roles.");

            var caller = await db.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.Id == callerUserId, ct);

            callerRoleId = caller?.RoleId;
            if (callerRoleId != role.Id)
            {
                targetPermissions = role.PermissionSetId.HasValue
                    ? await LoadFullPermissionSetAsync(role.PermissionSetId.Value, ct)
                    : null;

                callerPermissions = caller?.Role?.PermissionSetId is { } cpId
                    ? await LoadFullPermissionSetAsync(cpId, ct)
                    : null;
            }
        }

        agentAdministration.AssignRole(
            agent,
            roleId,
            role,
            callerRoleId,
            callerPermissions,
            targetPermissions,
            moduleRegistry.GetAllRegisteredResourceTypes());

        await db.SaveChangesAsync(ct);
        InvalidateAgentRuntimeState(agentId);
        return agentAdministration.ToResponse(agent, agent.Model);
    }

    /// <summary>
    /// Creates a <c>default-{modelName}-{providerSuffix}</c> agent for every
    /// chat-capable model that does not already have one.
    /// </summary>
    public async Task<IReadOnlyList<AgentResponse>> SyncWithModelsAsync(
        CancellationToken ct = default)
    {
        var models = await db.Models
            .Include(m => m.Provider)
            .Where(m => m.CapabilityTagsRaw != null
                && m.CapabilityTagsRaw.Contains(WellKnownCapabilityKeys.Chat))
            .ToListAsync(ct);

        var existingNames = await db.Agents
            .Select(a => a.Name)
            .ToListAsync(ct);

        var nameSet = new HashSet<string>(
            existingNames,
            StringComparer.OrdinalIgnoreCase);
        var created = new List<AgentResponse>();

        foreach (var model in models)
        {
            var plugin = clientFactory.GetPlugin(model.Provider.ProviderKey)
                ?? throw new InvalidOperationException(
                    $"Cannot synthesise default agent for model '{model.Name}' "
                    + $"(provider '{model.Provider.Name}', key '{model.Provider.ProviderKey}'): "
                    + "no provider plugin is registered. Ensure the owning module is "
                    + "loaded and enabled before running agent sync.");

            var providerSuffix = await plugin.GetAgentIdentifierSuffixAsync(
                model.Provider.Name,
                model.Id,
                ct);

            var agent = agentAdministration.CreateDefaultAgentIfMissing(
                model,
                providerSuffix,
                nameSet);
            if (agent is null)
                continue;

            db.Agents.Add(agent);
            await db.SaveChangesAsync(ct);

            created.Add(agentAdministration.ToResponse(agent, model));
        }

        return created;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var agent = await db.Agents.FindAsync([id], ct);
        if (agent is null)
            return false;

        db.Agents.Remove(agent);
        await db.SaveChangesAsync(ct);
        InvalidateAgentRuntimeState(id);
        return true;
    }

    /// <summary>
    /// Assigns or removes a role on the calling user. The same permission
    /// validation applies: you can only assign a role whose permissions are
    /// covered by your current role.
    /// </summary>
    public async Task<MeResponse?> AssignUserRoleAsync(
        Guid roleId,
        CancellationToken ct = default)
    {
        var userId = session.UserId
            ?? throw new UnauthorizedAccessException("A logged-in user is required.");

        var user = await db.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return null;

        RoleDB? role = null;
        PermissionSetDB? callerPermissions = null;
        PermissionSetDB? targetPermissions = null;

        if (roleId != Guid.Empty)
        {
            role = await db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct)
                ?? throw new ArgumentException($"Role {roleId} not found.");

            if (user.RoleId != role.Id)
            {
                targetPermissions = role.PermissionSetId.HasValue
                    ? await LoadFullPermissionSetAsync(role.PermissionSetId.Value, ct)
                    : null;

                callerPermissions = user.Role?.PermissionSetId is { } cpId
                    ? await LoadFullPermissionSetAsync(cpId, ct)
                    : null;
            }
        }

        agentAdministration.AssignUserRole(
            user,
            roleId,
            role,
            callerPermissions,
            targetPermissions,
            moduleRegistry.GetAllRegisteredResourceTypes());

        await db.SaveChangesAsync(ct);
        chatCache.Remove(ChatCache.KeyHeaderUser(userId));
        return new MeResponse(
            user.Id,
            user.Username,
            user.Bio,
            user.RoleId,
            user.Role?.Name);
    }

    private async Task<PermissionSetDB?> LoadFullPermissionSetAsync(
        Guid psId,
        CancellationToken ct) =>
        await db.PermissionSets
            .Include(p => p.GlobalFlags)
            .Include(p => p.ResourceAccesses)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == psId, ct);

    private bool IsUniqueAgentNamesEnforced()
    {
        return AgentAdministrationEngine.IsUniqueAgentNameEnforced(
            configuration["UniqueNames:Agents"]);
    }

    private void InvalidateAgentRuntimeState(Guid agentId)
    {
        chatCache.RemoveHeaderAgentSuffixesForAgent(agentId);
        chatCache.RemoveEffectiveToolsForAgent(agentId);
        chatCache.RemoveDefaultResourceResolutionForAgent(agentId);
    }

    private async Task EnsureAgentNameUniqueAsync(
        string name,
        Guid? excludeId,
        CancellationToken ct)
    {
        var names = await LoadAgentNamesAsync(excludeId, ct);
        agentAdministration.EnsureAgentNameAvailable(name, names);
    }

    private async Task<IReadOnlyList<string>> LoadAgentNamesAsync(
        Guid? excludeId,
        CancellationToken ct)
    {
        return await db.Agents
            .Where(a => excludeId == null || a.Id != excludeId)
            .Select(a => a.Name)
            .ToListAsync(ct);
    }
}
