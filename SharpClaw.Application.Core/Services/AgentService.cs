using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.DTOs.Agents;
using SharpClaw.Contracts.Enums;
using SharpClaw.Infrastructure.Models;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

public sealed class AgentService(SharpClawDbContext db)
{
    public async Task<AgentResponse> CreateAsync(CreateAgentRequest request, CancellationToken ct = default)
    {
        var model = await db.Models
            .Include(m => m.Provider)
            .FirstOrDefaultAsync(m => m.Id == request.ModelId, ct)
            ?? throw new ArgumentException($"Model {request.ModelId} not found.");

        var agent = new AgentDB
        {
            Name = request.Name,
            SystemPrompt = request.SystemPrompt,
            ModelId = model.Id,
        };

        db.Agents.Add(agent);
        await db.SaveChangesAsync(ct);

        return ToResponse(agent, model);
    }

    public async Task<AgentResponse?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        var agent = await db.Agents
            .Include(a => a.Model).ThenInclude(m => m.Provider)
            .Include(a => a.Role)
            .FirstOrDefaultAsync(a => a.Name == name, ct);

        return agent is null ? null : ToResponse(agent, agent.Model);
    }

    public async Task<AgentResponse?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var agent = await db.Agents
            .Include(a => a.Model).ThenInclude(m => m.Provider)
            .Include(a => a.Role)
            .FirstOrDefaultAsync(a => a.Id == id, ct);

        return agent is null ? null : ToResponse(agent, agent.Model);
    }

    public async Task<IReadOnlyList<AgentResponse>> ListAsync(CancellationToken ct = default)
    {
        return await db.Agents
            .Include(a => a.Model).ThenInclude(m => m.Provider)
            .Include(a => a.Role)
            .Select(a => new AgentResponse(
                a.Id, a.Name, a.SystemPrompt,
                a.ModelId, a.Model.Name, a.Model.Provider.Name,
                a.RoleId, a.Role != null ? a.Role.Name : null))
            .ToListAsync(ct);
    }

    public async Task<AgentResponse?> UpdateAsync(Guid id, UpdateAgentRequest request, CancellationToken ct = default)
    {
        var agent = await db.Agents
            .Include(a => a.Model).ThenInclude(m => m.Provider)
            .Include(a => a.Role)
            .FirstOrDefaultAsync(a => a.Id == id, ct);
        if (agent is null) return null;

        if (request.Name is not null) agent.Name = request.Name;
        if (request.SystemPrompt is not null) agent.SystemPrompt = request.SystemPrompt;
        if (request.ModelId is { } modelId)
        {
            var model = await db.Models.Include(m => m.Provider).FirstOrDefaultAsync(m => m.Id == modelId, ct)
                ?? throw new ArgumentException($"Model {modelId} not found.");
            agent.ModelId = model.Id;
            agent.Model = model;
        }

        await db.SaveChangesAsync(ct);
        return ToResponse(agent, agent.Model);
    }

    /// <summary>
    /// Assigns or removes a role on an agent. The calling user must hold the
    /// target role themselves — you cannot grant permissions you don't have.
    /// Pass <see cref="Guid.Empty"/> as <paramref name="roleId"/> to remove
    /// the current role.
    /// </summary>
    public async Task<AgentResponse?> AssignRoleAsync(
        Guid agentId, Guid roleId, Guid? callerUserId, CancellationToken ct = default)
    {
        var agent = await db.Agents
            .Include(a => a.Model).ThenInclude(m => m.Provider)
            .Include(a => a.Role)
            .FirstOrDefaultAsync(a => a.Id == agentId, ct);
        if (agent is null) return null;

        if (roleId == Guid.Empty)
        {
            agent.RoleId = null;
            agent.Role = null;
        }
        else
        {
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct)
                ?? throw new ArgumentException($"Role {roleId} not found.");

            // Caller must hold this role to grant it
            if (callerUserId is null)
                throw new UnauthorizedAccessException("A logged-in user is required to assign roles.");

            var caller = await db.Users.FirstOrDefaultAsync(u => u.Id == callerUserId, ct);
            if (caller?.RoleId != role.Id)
                throw new UnauthorizedAccessException(
                    $"You must hold the '{role.Name}' role to assign it to an agent.");

            agent.RoleId = role.Id;
            agent.Role = role;
        }

        await db.SaveChangesAsync(ct);
        return ToResponse(agent, agent.Model);
    }

    /// <summary>
    /// Creates a <c>default-{modelName}</c> agent for every chat-capable model
    /// that does not already have one.  Returns the list of newly created agents.
    /// </summary>
    public async Task<IReadOnlyList<AgentResponse>> SyncWithModelsAsync(CancellationToken ct = default)
    {
        var models = await db.Models
            .Include(m => m.Provider)
            .Where(m => (m.Capabilities & ModelCapability.Chat) != 0)
            .ToListAsync(ct);

        var existingNames = await db.Agents
            .Select(a => a.Name)
            .ToListAsync(ct);

        var nameSet = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        var created = new List<AgentResponse>();

        foreach (var model in models)
        {
            var providerSuffix = model.Provider.Name
                .Replace(" ", "-")
                .ToLowerInvariant();
            var agentName = $"default-{model.Name}-{providerSuffix}";
            if (nameSet.Contains(agentName)) continue;

            var agent = new AgentDB
            {
                Name = agentName,
                ModelId = model.Id,
            };

            db.Agents.Add(agent);
            await db.SaveChangesAsync(ct);

            nameSet.Add(agentName);
            created.Add(ToResponse(agent, model));
        }

        return created;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var agent = await db.Agents.FindAsync([id], ct);
        if (agent is null) return false;

        db.Agents.Remove(agent);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static AgentResponse ToResponse(AgentDB agent, ModelDB model) =>
        new(agent.Id, agent.Name, agent.SystemPrompt, model.Id, model.Name, model.Provider.Name,
            agent.RoleId, agent.Role?.Name);
}
