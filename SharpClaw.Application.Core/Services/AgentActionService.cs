using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Enums;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

/// <summary>
/// Central gateway for every high-level action an agent can perform.
/// Each method checks the agent's permissions and the caller's clearance
/// before deciding whether to proceed, pause, or deny.
/// <para>
/// • <see cref="ClearanceVerdict.Approved"/>  — <paramref name="onApproved"/>
///   delegate (when supplied) is invoked automatically.<br/>
/// • <see cref="ClearanceVerdict.PendingApproval"/> — the action is paused;
///   the caller must arrange for an authorised entity to approve later.<br/>
/// • <see cref="ClearanceVerdict.Denied"/> — the agent lacks the permission.
/// </para>
/// The service never implements the action logic itself; execution is
/// delegated through the <c>onApproved</c> callback.
/// </summary>
public sealed class AgentActionService(SharpClawDbContext db)
{
    // ═══════════════════════════════════════════════════════════════
    // Global-flag actions
    // ═══════════════════════════════════════════════════════════════

    public Task<AgentActionResult> CreateSubAgentAsync(
        Guid agentId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateGlobalFlagAsync(
            agentId, caller, p => p.CanCreateSubAgents,
            "create sub-agents", onApproved, ct);

    public Task<AgentActionResult> CreateContainerAsync(
        Guid agentId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateGlobalFlagAsync(
            agentId, caller, p => p.CanCreateContainers,
            "create containers", onApproved, ct);

    public Task<AgentActionResult> RegisterInfoStoreAsync(
        Guid agentId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateGlobalFlagAsync(
            agentId, caller, p => p.CanRegisterInfoStores,
            "register information stores", onApproved, ct);

    public Task<AgentActionResult> AccessLocalhostInBrowserAsync(
        Guid agentId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateGlobalFlagAsync(
            agentId, caller, p => p.CanAccessLocalhostInBrowser,
            "access localhost in browser", onApproved, ct);

    public Task<AgentActionResult> AccessLocalhostCliAsync(
        Guid agentId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateGlobalFlagAsync(
            agentId, caller, p => p.CanAccessLocalhostCli,
            "access localhost via CLI", onApproved, ct);

    // ═══════════════════════════════════════════════════════════════
    // Per-resource actions
    // ═══════════════════════════════════════════════════════════════

    public Task<AgentActionResult> UnsafeExecuteAsDangerousShellAsync(
        Guid agentId, Guid systemUserId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, systemUserId, caller,
            p => p.DangerousShellAccesses, a => a.SystemUserId, a => a.Clearance,
            "dangerous shell access", onApproved, ct);

    public Task<AgentActionResult> ExecuteAsSafeShellAsync(
        Guid agentId, Guid containerId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, containerId, caller,
            p => p.SafeShellAccesses, a => a.ContainerId, a => a.Clearance,
            "safe shell access", onApproved, ct);

    public Task<AgentActionResult> AccessLocalInfoStoreAsync(
        Guid agentId, Guid storeId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, storeId, caller,
            p => p.LocalInfoStorePermissions, a => a.LocalInformationStoreId, a => a.Clearance,
            "local information store access", onApproved, ct);

    public Task<AgentActionResult> AccessExternalInfoStoreAsync(
        Guid agentId, Guid storeId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, storeId, caller,
            p => p.ExternalInfoStorePermissions, a => a.ExternalInformationStoreId, a => a.Clearance,
            "external information store access", onApproved, ct);

    public Task<AgentActionResult> AccessWebsiteAsync(
        Guid agentId, Guid websiteId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, websiteId, caller,
            p => p.WebsiteAccesses, a => a.WebsiteId, a => a.Clearance,
            "website access", onApproved, ct);

    public Task<AgentActionResult> QuerySearchEngineAsync(
        Guid agentId, Guid searchEngineId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, searchEngineId, caller,
            p => p.SearchEngineAccesses, a => a.SearchEngineId, a => a.Clearance,
            "search engine access", onApproved, ct);

    public Task<AgentActionResult> AccessContainerAsync(
        Guid agentId, Guid containerId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, containerId, caller,
            p => p.ContainerAccesses, a => a.ContainerId, a => a.Clearance,
            "container access", onApproved, ct);

    public Task<AgentActionResult> ManageAgentAsync(
        Guid agentId, Guid targetAgentId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, targetAgentId, caller,
            p => p.AgentPermissions, a => a.AgentId, a => a.Clearance,
            "agent management", onApproved, ct);

    public Task<AgentActionResult> EditTaskAsync(
        Guid agentId, Guid taskId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, taskId, caller,
            p => p.TaskPermissions, a => a.ScheduledTaskId, a => a.Clearance,
            "task edit", onApproved, ct);

    public Task<AgentActionResult> AccessSkillAsync(
        Guid agentId, Guid skillId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, skillId, caller,
            p => p.SkillPermissions, a => a.SkillId, a => a.Clearance,
            "skill access", onApproved, ct);

    public Task<AgentActionResult> AccessAudioDeviceAsync(
        Guid agentId, Guid audioDeviceId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, audioDeviceId, caller,
            p => p.AudioDeviceAccesses, a => a.AudioDeviceId, a => a.Clearance,
            "audio device access", onApproved, ct);

    // ═══════════════════════════════════════════════════════════════
    // Core evaluation engine
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Evaluate a boolean (global-flag) permission.
    /// Global flags have no per-permission clearance — the group
    /// <see cref="PermissionSetDB.DefaultClearance"/> is used.
    /// </summary>
    private async Task<AgentActionResult> EvaluateGlobalFlagAsync(
        Guid agentId,
        ActionCaller caller,
        Func<PermissionSetDB, bool> hasFlag,
        string flagDescription,
        Func<Task>? onApproved,
        CancellationToken ct)
    {
        var agentPerms = await LoadAgentPermissionsAsync(agentId, ct);
        if (agentPerms is null)
            return AgentActionResult.Denied("Agent has no role or permissions assigned.");

        if (!hasFlag(agentPerms))
            return AgentActionResult.Denied($"Agent does not have permission to {flagDescription}.");

        var effective = ResolveClearance(PermissionClearance.Unset, agentPerms.DefaultClearance);

        var result = await EvaluateCallerClearanceAsync(
            agentPerms, effective, caller,
            callerPerms => hasFlag(callerPerms), ct);

        if (result.Verdict == ClearanceVerdict.Approved && onApproved is not null)
            await onApproved();

        return result;
    }

    /// <summary>
    /// Evaluate a per-resource grant (one of the typed access collections).
    /// </summary>
    private async Task<AgentActionResult> EvaluateResourceAccessAsync<TAccess>(
        Guid agentId,
        Guid resourceId,
        ActionCaller caller,
        Func<PermissionSetDB, IEnumerable<TAccess>> getAccessCollection,
        Func<TAccess, Guid> getResourceId,
        Func<TAccess, PermissionClearance> getClearance,
        string resourceDescription,
        Func<Task>? onApproved,
        CancellationToken ct)
    {
        var agentPerms = await LoadAgentPermissionsAsync(agentId, ct);
        if (agentPerms is null)
            return AgentActionResult.Denied("Agent has no role or permissions assigned.");

        var access = getAccessCollection(agentPerms)
            .FirstOrDefault(a => getResourceId(a) == resourceId
                              || getResourceId(a) == WellKnownIds.AllResources);

        if (access is null)
            return AgentActionResult.Denied($"Agent does not have {resourceDescription}.");

        var effective = ResolveClearance(getClearance(access), agentPerms.DefaultClearance);

        var result = await EvaluateCallerClearanceAsync(
            agentPerms, effective, caller,
            callerPerms => getAccessCollection(callerPerms)
                .Any(a => getResourceId(a) == resourceId
                       || getResourceId(a) == WellKnownIds.AllResources),
            ct);

        if (result.Verdict == ClearanceVerdict.Approved && onApproved is not null)
            await onApproved();

        return result;
    }

    /// <summary>
    /// Determines whether <paramref name="caller"/> satisfies the
    /// <paramref name="effectiveClearance"/> requirement.
    /// <list type="bullet">
    ///   <item><b>Independent (5)</b> — no caller check needed.</item>
    ///   <item><b>ApprovedByWhitelistedAgent (4)</b> — whitelisted agent;
    ///         falls back to permitted agent, whitelisted user, then
    ///         same-level user.</item>
    ///   <item><b>ApprovedByPermittedAgent (3)</b> — agent-only: only a
    ///         permitted agent can approve.  No user fallback.</item>
    ///   <item><b>ApprovedByWhitelistedUser (2)</b> — whitelisted user;
    ///         falls back to same-level user.</item>
    ///   <item><b>ApprovedBySameLevelUser (1)</b> — same-level user only.</item>
    /// </list>
    /// </summary>
    private async Task<AgentActionResult> EvaluateCallerClearanceAsync(
        PermissionSetDB agentPerms,
        PermissionClearance effectiveClearance,
        ActionCaller caller,
        Func<PermissionSetDB, bool> callerHasSamePermission,
        CancellationToken ct)
    {
        // ── Level 5: independent ─────────────────────────────────
        if (effectiveClearance == PermissionClearance.Independent)
            return AgentActionResult.Approve(
                "Agent can act independently.", effectiveClearance);

        // Load the caller's permissions once (may be null if no role)
        PermissionSetDB? callerPerms = null;
        if (caller.UserId is { } userId)
        {
            var user = await db.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user?.Role?.PermissionSetId is { } userPsId)
                callerPerms = await LoadPermissionSetAsync(userPsId, ct);
        }
        else if (caller.AgentId is { } callerAgentId)
        {
            var callerAgent = await db.Agents
                .Include(a => a.Role)
                .FirstOrDefaultAsync(a => a.Id == callerAgentId, ct);
            if (callerAgent?.Role?.PermissionSetId is { } agentPsId)
                callerPerms = await LoadPermissionSetAsync(agentPsId, ct);
        }
        else
        {
            return AgentActionResult.Pending(
                $"No caller identified. Awaiting approval (clearance: {effectiveClearance}).",
                effectiveClearance);
        }

        // ── Whitelisted agent (level 4 primary) ──────────────────
        if (caller.AgentId is { } whitelistAgentId
            && effectiveClearance == PermissionClearance.ApprovedByWhitelistedAgent
            && agentPerms.ClearanceAgentWhitelist.Any(w => w.AgentId == whitelistAgentId))
        {
            return AgentActionResult.Approve(
                "Approved by whitelisted agent.", effectiveClearance);
        }

        // ── Permitted agent (levels 3, 4) ─────────────────────────
        if (caller.AgentId is not null
            && effectiveClearance is PermissionClearance.ApprovedByPermittedAgent
                                  or PermissionClearance.ApprovedByWhitelistedAgent
            && callerPerms is not null
            && callerHasSamePermission(callerPerms))
        {
            return AgentActionResult.Approve(
                "Approved by permitted agent.", effectiveClearance);
        }

        // ── Whitelisted user (levels 2, 4) ───────────────────────
        if (caller.UserId is { } whitelistUserId
            && effectiveClearance is PermissionClearance.ApprovedByWhitelistedUser
                                  or PermissionClearance.ApprovedByWhitelistedAgent
            && agentPerms.ClearanceUserWhitelist.Any(w => w.UserId == whitelistUserId))
        {
            return AgentActionResult.Approve(
                "Approved by whitelisted user.", effectiveClearance);
        }

        // ── Same-level user (levels 1, 2, 4 — NOT 3) ─────────────
        // Level 3 is agent-only; no user can satisfy it.
        if (caller.UserId is not null
            && effectiveClearance is not PermissionClearance.ApprovedByPermittedAgent
            && callerPerms is not null
            && callerHasSamePermission(callerPerms))
        {
            return AgentActionResult.Approve(
                "Approved by same-level user.", effectiveClearance);
        }

        // ── None of the above matched ────────────────────────────
        return AgentActionResult.Pending(
            $"Awaiting approval (clearance: {effectiveClearance}).",
            effectiveClearance);
    }

    // ═══════════════════════════════════════════════════════════════
    // Data loading
    // ═══════════════════════════════════════════════════════════════

    private async Task<PermissionSetDB?> LoadAgentPermissionsAsync(
        Guid agentId, CancellationToken ct)
    {
        var agent = await db.Agents
            .Include(a => a.Role)
            .FirstOrDefaultAsync(a => a.Id == agentId, ct);
        return agent?.Role?.PermissionSetId is { } psId
            ? await LoadPermissionSetAsync(psId, ct)
            : null;
    }

    /// <summary>
    /// Loads a full <see cref="PermissionSetDB"/> by its primary key,
    /// including all typed access collections and whitelists.
    /// </summary>
    public async Task<PermissionSetDB?> LoadPermissionSetAsync(
        Guid permissionSetId, CancellationToken ct)
    {
        return await db.PermissionSets
            .Include(p => p.DangerousShellAccesses)
            .Include(p => p.SafeShellAccesses)
            .Include(p => p.LocalInfoStorePermissions)
            .Include(p => p.ExternalInfoStorePermissions)
            .Include(p => p.WebsiteAccesses)
            .Include(p => p.SearchEngineAccesses)
            .Include(p => p.ContainerAccesses)
            .Include(p => p.AudioDeviceAccesses)
            .Include(p => p.AgentPermissions)
            .Include(p => p.TaskPermissions)
            .Include(p => p.SkillPermissions)
            .Include(p => p.ClearanceUserWhitelist)
            .Include(p => p.ClearanceAgentWhitelist)
            .FirstOrDefaultAsync(p => p.Id == permissionSetId, ct);
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolves the effective clearance: per-permission value wins;
    /// falls back to the group default; ultimate fallback is
    /// <see cref="PermissionClearance.ApprovedBySameLevelUser"/>.
    /// </summary>
    private static PermissionClearance ResolveClearance(
        PermissionClearance perPermission,
        PermissionClearance groupDefault) =>
        perPermission is not PermissionClearance.Unset
            ? perPermission
            : groupDefault is not PermissionClearance.Unset
                ? groupDefault
                : PermissionClearance.ApprovedBySameLevelUser;
}
