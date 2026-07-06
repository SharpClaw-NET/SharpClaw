using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.DTOs.Agents;
using SharpClaw.Contracts.DTOs.Auth;
using SharpClaw.Core.Agents;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.BLL.Services;

public sealed class AgentService(
    SharpClawDbContext db,
    AgentAdministrationEngine agentAdministration,
    AgentRuntimeAdministrationEngine runtimeAdministration,
    EfAgentAdministrationHost administrationHost)
{
    public async Task<AgentResponse> CreateAsync(
        CreateAgentRequest request,
        CancellationToken ct = default)
    {
        return await runtimeAdministration.CreateAsync(
            request,
            administrationHost,
            ct);
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
        return await runtimeAdministration.UpdateAsync(
            id,
            request,
            administrationHost,
            ct);
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
        return await runtimeAdministration.AssignRoleAsync(
            agentId,
            roleId,
            administrationHost,
            ct);
    }

    /// <summary>
    /// Creates a <c>default-{modelName}-{providerSuffix}</c> agent for every
    /// chat-capable model that does not already have one.
    /// </summary>
    public async Task<IReadOnlyList<AgentResponse>> SyncWithModelsAsync(
        CancellationToken ct = default)
    {
        return await runtimeAdministration.SyncWithModelsAsync(
            administrationHost,
            ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        return await runtimeAdministration.DeleteAsync(
            id,
            administrationHost,
            ct);
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
        return await runtimeAdministration.AssignUserRoleAsync(
            roleId,
            administrationHost,
            ct);
    }
}
