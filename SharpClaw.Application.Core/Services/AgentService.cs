using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.Core.Clients;
using SharpClaw.Application.Core.LocalInference;
using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.Agents;
using SharpClaw.Contracts.DTOs.Auth;
using SharpClaw.Contracts.Enums;
using SharpClaw.Infrastructure.Models;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

public sealed class AgentService(SharpClawDbContext db, SessionService session)
{
    public async Task<AgentResponse> CreateAsync(CreateAgentRequest request, CancellationToken ct = default)
    {
        var model = await db.Models
            .Include(m => m.Provider)
            .FirstOrDefaultAsync(m => m.Id == request.ModelId, ct)
            ?? throw new ArgumentException($"Model {request.ModelId} not found.");

        // Validate typed completion parameters against provider constraints
        ValidateCompletionParameters(request, model.Provider.ProviderType);

        var agent = new AgentDB
        {
            Name = request.Name,
            SystemPrompt = request.SystemPrompt,
            MaxCompletionTokens = request.MaxCompletionTokens,
            ModelId = model.Id,
            CustomId = request.CustomId,
            Temperature = request.Temperature,
            TopP = request.TopP,
            TopK = request.TopK,
            FrequencyPenalty = request.FrequencyPenalty,
            PresencePenalty = request.PresencePenalty,
            Stop = request.Stop,
            Seed = request.Seed,
            ResponseFormat = request.ResponseFormat,
            ReasoningEffort = request.ReasoningEffort,
            ProviderParameters = request.ProviderParameters,
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
                a.RoleId, a.Role != null ? a.Role.Name : null,
                a.MaxCompletionTokens, a.CustomId,
                a.Temperature, a.TopP, a.TopK,
                a.FrequencyPenalty, a.PresencePenalty, a.Stop,
                a.Seed, a.ResponseFormat, a.ReasoningEffort,
                a.ProviderParameters))
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
        if (request.MaxCompletionTokens is not null) agent.MaxCompletionTokens = request.MaxCompletionTokens;
        if (request.CustomId is not null) agent.CustomId = request.CustomId;
        if (request.Temperature is not null) agent.Temperature = request.Temperature;
        if (request.TopP is not null) agent.TopP = request.TopP;
        if (request.TopK is not null) agent.TopK = request.TopK;
        if (request.FrequencyPenalty is not null) agent.FrequencyPenalty = request.FrequencyPenalty;
        if (request.PresencePenalty is not null) agent.PresencePenalty = request.PresencePenalty;
        if (request.Stop is not null) agent.Stop = request.Stop.Length > 0 ? request.Stop : null;
        if (request.Seed is not null) agent.Seed = request.Seed;
        if (request.ResponseFormat is not null) agent.ResponseFormat = request.ResponseFormat;
        if (request.ReasoningEffort is not null) agent.ReasoningEffort = request.ReasoningEffort;
        if (request.ProviderParameters is not null)
            agent.ProviderParameters = request.ProviderParameters.Count > 0 ? request.ProviderParameters : null;
        if (request.CustomChatHeader is not null)
            agent.CustomChatHeader = request.CustomChatHeader.Length > 0 ? request.CustomChatHeader : null;
        if (request.ModelId is { } modelId)
        {
            var model = await db.Models.Include(m => m.Provider).FirstOrDefaultAsync(m => m.Id == modelId, ct)
                ?? throw new ArgumentException($"Model {modelId} not found.");
            agent.ModelId = model.Id;
            agent.Model = model;
        }

        // Validate the effective completion parameters against the (possibly updated) provider
        ValidateCompletionParameters(agent, agent.Model.Provider.ProviderType);

        await db.SaveChangesAsync(ct);
        return ToResponse(agent, agent.Model);
    }

    /// <summary>
    /// Assigns or removes a role on an agent. The calling user must either
    /// hold the exact same role, or hold a role whose permission set covers
    /// every permission in the target role at the same or higher clearance
    /// level.
    /// Pass <see cref="Guid.Empty"/> as <paramref name="roleId"/> to remove
    /// the current role.
    /// </summary>
    public async Task<AgentResponse?> AssignRoleAsync(
        Guid agentId, Guid roleId, CancellationToken ct = default)
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

            var callerUserId = session.UserId
                ?? throw new UnauthorizedAccessException("A logged-in user is required to assign roles.");

            var caller = await db.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.Id == callerUserId, ct);

            // Exact same role → always allowed.
            if (caller?.RoleId != role.Id)
            {
                // Load both permission sets fully and compare.
                var targetPs = role.PermissionSetId.HasValue
                    ? await LoadFullPermissionSetAsync(role.PermissionSetId.Value, ct)
                    : null;

                var callerPs = caller?.Role?.PermissionSetId is { } cpId
                    ? await LoadFullPermissionSetAsync(cpId, ct)
                    : null;

                ValidateCallerCoversTargetRole(callerPs, targetPs, role.Name);
            }

            agent.RoleId = role.Id;
            agent.Role = role;
        }

        await db.SaveChangesAsync(ct);
        return ToResponse(agent, agent.Model);
    }

    /// <summary>
    /// Verifies that <paramref name="callerPs"/> covers every permission in
    /// <paramref name="targetPs"/> at the same or higher clearance level.
    /// </summary>
    private static void ValidateCallerCoversTargetRole(
        PermissionSetDB? callerPs, PermissionSetDB? targetPs, string roleName)
    {
        // Target role has no permissions → anyone can assign it.
        if (targetPs is null)
            return;

        if (callerPs is null)
            throw new UnauthorizedAccessException(
                $"You have no permissions — cannot assign the '{roleName}' role.");

        // Default clearance: caller must have ≥ target.
        if (Effective(targetPs.DefaultClearance) > Effective(callerPs.DefaultClearance))
            throw new UnauthorizedAccessException(
                $"Cannot assign '{roleName}': target default clearance " +
                $"{targetPs.DefaultClearance} exceeds your {callerPs.DefaultClearance}.");

        // Global flags.
        if (targetPs.CanCreateSubAgents && !callerPs.CanCreateSubAgents)
            Deny(roleName, "CanCreateSubAgents");
        if (targetPs.CanCreateContainers && !callerPs.CanCreateContainers)
            Deny(roleName, "CanCreateContainers");
        if (targetPs.CanRegisterInfoStores && !callerPs.CanRegisterInfoStores)
            Deny(roleName, "CanRegisterInfoStores");
        if (targetPs.CanAccessLocalhostInBrowser && !callerPs.CanAccessLocalhostInBrowser)
            Deny(roleName, "CanAccessLocalhostInBrowser");
        if (targetPs.CanAccessLocalhostCli && !callerPs.CanAccessLocalhostCli)
            Deny(roleName, "CanAccessLocalhostCli");
        if (targetPs.CanClickDesktop && !callerPs.CanClickDesktop)
            Deny(roleName, "CanClickDesktop");
        if (targetPs.CanTypeOnDesktop && !callerPs.CanTypeOnDesktop)
            Deny(roleName, "CanTypeOnDesktop");

        // Per-resource grant collections.
        ValidateResourceCoverage(roleName, "DangerousShellAccesses",
            targetPs.DangerousShellAccesses, callerPs.DangerousShellAccesses,
            a => a.SystemUserId, a => a.Clearance, targetPs.DefaultClearance, callerPs.DefaultClearance);
        ValidateResourceCoverage(roleName, "SafeShellAccesses",
            targetPs.SafeShellAccesses, callerPs.SafeShellAccesses,
            a => a.ContainerId, a => a.Clearance, targetPs.DefaultClearance, callerPs.DefaultClearance);
        ValidateResourceCoverage(roleName, "ContainerAccesses",
            targetPs.ContainerAccesses, callerPs.ContainerAccesses,
            a => a.ContainerId, a => a.Clearance, targetPs.DefaultClearance, callerPs.DefaultClearance);
        ValidateResourceCoverage(roleName, "WebsiteAccesses",
            targetPs.WebsiteAccesses, callerPs.WebsiteAccesses,
            a => a.WebsiteId, a => a.Clearance, targetPs.DefaultClearance, callerPs.DefaultClearance);
        ValidateResourceCoverage(roleName, "SearchEngineAccesses",
            targetPs.SearchEngineAccesses, callerPs.SearchEngineAccesses,
            a => a.SearchEngineId, a => a.Clearance, targetPs.DefaultClearance, callerPs.DefaultClearance);
        ValidateResourceCoverage(roleName, "LocalInfoStorePermissions",
            targetPs.LocalInfoStorePermissions, callerPs.LocalInfoStorePermissions,
            a => a.LocalInformationStoreId, a => a.Clearance, targetPs.DefaultClearance, callerPs.DefaultClearance);
        ValidateResourceCoverage(roleName, "ExternalInfoStorePermissions",
            targetPs.ExternalInfoStorePermissions, callerPs.ExternalInfoStorePermissions,
            a => a.ExternalInformationStoreId, a => a.Clearance, targetPs.DefaultClearance, callerPs.DefaultClearance);
        ValidateResourceCoverage(roleName, "AudioDeviceAccesses",
            targetPs.AudioDeviceAccesses, callerPs.AudioDeviceAccesses,
            a => a.AudioDeviceId, a => a.Clearance, targetPs.DefaultClearance, callerPs.DefaultClearance);
        ValidateResourceCoverage(roleName, "DisplayDeviceAccesses",
            targetPs.DisplayDeviceAccesses, callerPs.DisplayDeviceAccesses,
            a => a.DisplayDeviceId, a => a.Clearance, targetPs.DefaultClearance, callerPs.DefaultClearance);
        ValidateResourceCoverage(roleName, "EditorSessionAccesses",
            targetPs.EditorSessionAccesses, callerPs.EditorSessionAccesses,
            a => a.EditorSessionId, a => a.Clearance, targetPs.DefaultClearance, callerPs.DefaultClearance);
        ValidateResourceCoverage(roleName, "AgentPermissions",
            targetPs.AgentPermissions, callerPs.AgentPermissions,
            a => a.AgentId, a => a.Clearance, targetPs.DefaultClearance, callerPs.DefaultClearance);
        ValidateResourceCoverage(roleName, "TaskPermissions",
            targetPs.TaskPermissions, callerPs.TaskPermissions,
            a => a.ScheduledTaskId, a => a.Clearance, targetPs.DefaultClearance, callerPs.DefaultClearance);
        ValidateResourceCoverage(roleName, "SkillPermissions",
            targetPs.SkillPermissions, callerPs.SkillPermissions,
            a => a.SkillId, a => a.Clearance, targetPs.DefaultClearance, callerPs.DefaultClearance);
    }

    /// <summary>
    /// For every resource grant in <paramref name="targetAccesses"/>, the caller
    /// must hold either the same resource or the AllResources wildcard, at the
    /// same or higher effective clearance.
    /// </summary>
    private static void ValidateResourceCoverage<T>(
        string roleName, string grantName,
        ICollection<T> targetAccesses, ICollection<T> callerAccesses,
        Func<T, Guid> resourceSelector, Func<T, PermissionClearance> clearanceSelector,
        PermissionClearance targetDefault, PermissionClearance callerDefault)
    {
        if (targetAccesses is { Count: > 0 } && callerAccesses is not { Count: > 0 })
            throw new UnauthorizedAccessException(
                $"Cannot assign '{roleName}': you hold no {grantName} grants.");

        if (targetAccesses is not { Count: > 0 })
            return;

        // Build caller lookup: resourceId → effective clearance.
        var callerMap = new Dictionary<Guid, PermissionClearance>();
        foreach (var a in callerAccesses)
            callerMap[resourceSelector(a)] = EffectiveOr(clearanceSelector(a), callerDefault);

        var callerHasWildcard = callerMap.TryGetValue(WellKnownIds.AllResources, out var wildcardClearance);

        foreach (var target in targetAccesses)
        {
            var targetResId = resourceSelector(target);
            var targetClearance = EffectiveOr(clearanceSelector(target), targetDefault);

            PermissionClearance callerClearance;
            if (callerMap.TryGetValue(targetResId, out var exact))
                callerClearance = exact;
            else if (callerHasWildcard)
                callerClearance = wildcardClearance;
            else
                throw new UnauthorizedAccessException(
                    $"Cannot assign '{roleName}': you lack {grantName} " +
                    $"for resource {targetResId}.");

            if (targetClearance > callerClearance)
                throw new UnauthorizedAccessException(
                    $"Cannot assign '{roleName}': {grantName} for resource " +
                    $"{targetResId} requires clearance {targetClearance} but " +
                    $"you only have {callerClearance}.");
        }
    }

    /// <summary>Returns the effective clearance, falling back to default when Unset.</summary>
    private static PermissionClearance EffectiveOr(
        PermissionClearance clearance, PermissionClearance fallback) =>
        clearance != PermissionClearance.Unset ? clearance : fallback;

    /// <summary>Returns the effective clearance, treating Unset as the lowest level.</summary>
    private static PermissionClearance Effective(PermissionClearance clearance) =>
        clearance != PermissionClearance.Unset ? clearance : PermissionClearance.ApprovedBySameLevelUser;

    private static void Deny(string roleName, string flag) =>
        throw new UnauthorizedAccessException(
            $"Cannot assign '{roleName}': you do not hold {flag}.");

    private async Task<PermissionSetDB?> LoadFullPermissionSetAsync(
        Guid psId, CancellationToken ct) =>
        await db.PermissionSets
            .Include(p => p.DangerousShellAccesses)
            .Include(p => p.SafeShellAccesses)
            .Include(p => p.ContainerAccesses)
            .Include(p => p.WebsiteAccesses)
            .Include(p => p.SearchEngineAccesses)
            .Include(p => p.LocalInfoStorePermissions)
            .Include(p => p.ExternalInfoStorePermissions)
            .Include(p => p.AudioDeviceAccesses)
            .Include(p => p.DisplayDeviceAccesses)
            .Include(p => p.EditorSessionAccesses)
            .Include(p => p.AgentPermissions)
            .Include(p => p.TaskPermissions)
            .Include(p => p.SkillPermissions)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == psId, ct);

    /// <summary>
    /// Creates a <c>default-{modelName}</c> agent for every chat-capable model
    /// that does not already have one.  Returns the list of newly created agents.
    /// For local models the suffix is derived from the download source
    /// (e.g. "huggingface") instead of the provider name.
    /// </summary>
    public async Task<IReadOnlyList<AgentResponse>> SyncWithModelsAsync(CancellationToken ct = default)
    {
        var models = await db.Models
            .Include(m => m.Provider)
            .Where(m => (m.Capabilities & ModelCapability.Chat) != 0)
            .ToListAsync(ct);

        // Pre-load source URLs for local models so we can derive the suffix.
        var localModelIds = models
            .Where(m => m.Provider.ProviderType == ProviderType.Local)
            .Select(m => m.Id)
            .ToHashSet();

        var localSourceUrls = localModelIds.Count > 0
            ? await db.LocalModelFiles
                .Where(f => localModelIds.Contains(f.ModelId))
                .ToDictionaryAsync(f => f.ModelId, f => f.SourceUrl, ct)
            : [];

        var existingNames = await db.Agents
            .Select(a => a.Name)
            .ToListAsync(ct);

        var nameSet = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        var created = new List<AgentResponse>();

        foreach (var model in models)
        {
            string providerSuffix;
            if (model.Provider.ProviderType == ProviderType.Local
                && localSourceUrls.TryGetValue(model.Id, out var sourceUrl))
            {
                providerSuffix = ModelDownloadManager.ResolveSourceFolder(sourceUrl).ToLowerInvariant();
            }
            else
            {
                providerSuffix = model.Provider.Name
                    .Replace(" ", "-")
                    .ToLowerInvariant();
            }

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

    /// <summary>
    /// Assigns or removes a role on the calling user. The same permission
    /// validation applies: you can only assign a role whose permissions are
    /// covered by your current role.
    /// Pass <see cref="Guid.Empty"/> as <paramref name="roleId"/> to remove.
    /// </summary>
    public async Task<MeResponse?> AssignUserRoleAsync(
        Guid roleId, CancellationToken ct = default)
    {
        var userId = session.UserId
            ?? throw new UnauthorizedAccessException("A logged-in user is required.");

        var user = await db.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return null;

        if (roleId == Guid.Empty)
        {
            user.RoleId = null;
            user.Role = null;
        }
        else
        {
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct)
                ?? throw new ArgumentException($"Role {roleId} not found.");

            if (user.RoleId != role.Id)
            {
                var targetPs = role.PermissionSetId.HasValue
                    ? await LoadFullPermissionSetAsync(role.PermissionSetId.Value, ct)
                    : null;

                var callerPs = user.Role?.PermissionSetId is { } cpId
                    ? await LoadFullPermissionSetAsync(cpId, ct)
                    : null;

                ValidateCallerCoversTargetRole(callerPs, targetPs, role.Name);
            }

            user.RoleId = role.Id;
            user.Role = role;
        }

        await db.SaveChangesAsync(ct);
        return new MeResponse(user.Id, user.Username, user.Bio, user.RoleId, user.Role?.Name);
    }

    private static AgentResponse ToResponse(AgentDB agent, ModelDB model) =>
        new(agent.Id, agent.Name, agent.SystemPrompt, model.Id, model.Name, model.Provider.Name,
            agent.RoleId, agent.Role?.Name, agent.MaxCompletionTokens, agent.CustomId,
            agent.Temperature, agent.TopP, agent.TopK,
            agent.FrequencyPenalty, agent.PresencePenalty, agent.Stop,
            agent.Seed, agent.ResponseFormat, agent.ReasoningEffort,
            agent.ProviderParameters, agent.CustomChatHeader);

    /// <summary>
    /// Validates the typed completion parameters from a create request
    /// against the target provider's constraints.
    /// </summary>
    private static void ValidateCompletionParameters(CreateAgentRequest request, ProviderType providerType)
    {
        var cp = new CompletionParameters
        {
            Temperature = request.Temperature,
            TopP = request.TopP,
            TopK = request.TopK,
            FrequencyPenalty = request.FrequencyPenalty,
            PresencePenalty = request.PresencePenalty,
            Stop = request.Stop,
            Seed = request.Seed,
            ResponseFormat = request.ResponseFormat,
            ReasoningEffort = request.ReasoningEffort,
        };
        CompletionParameterValidator.ValidateOrThrow(cp, providerType);
    }

    /// <summary>
    /// Validates the effective completion parameters on an agent entity
    /// (after fields have been applied) against the target provider.
    /// </summary>
    private static void ValidateCompletionParameters(AgentDB agent, ProviderType providerType)
    {
        var cp = new CompletionParameters
        {
            Temperature = agent.Temperature,
            TopP = agent.TopP,
            TopK = agent.TopK,
            FrequencyPenalty = agent.FrequencyPenalty,
            PresencePenalty = agent.PresencePenalty,
            Stop = agent.Stop,
            Seed = agent.Seed,
            ResponseFormat = agent.ResponseFormat,
            ReasoningEffort = agent.ReasoningEffort,
        };
        CompletionParameterValidator.ValidateOrThrow(cp, providerType);
    }
}
