using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.Infrastructure.Models.Access;
using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Enums;
using SharpClaw.Application.Core.Modules;
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
public sealed class AgentActionService(SharpClawDbContext db, ModuleRegistry registry)
{
    // ═══════════════════════════════════════════════════════════════
    // Global-flag evaluation (generic — all flags resolved by key)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Evaluate a global-flag permission by its canonical key
    /// (e.g. "CanClickDesktop"). The flag must exist in the agent's
    /// <see cref="PermissionSetDB.GlobalFlags"/> collection.
    /// Replaces the 16 typed wrapper methods — see Module-System-Design §12.4.4.
    /// </summary>
    public Task<AgentActionResult> EvaluateGlobalFlagByKeyAsync(
        string flagKey, Guid agentId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateGlobalFlagAsync(
            agentId, caller,
            ps => ps.GlobalFlags.Any(f => f.FlagKey == flagKey),
            ps => ps.GlobalFlags.FirstOrDefault(f => f.FlagKey == flagKey)
                    ?.Clearance ?? PermissionClearance.Unset,
            flagKey, onApproved, ct);

    // ═══════════════════════════════════════════════════════════════
    // Core evaluation engine
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Evaluate a boolean (global-flag) permission.
    /// Each flag has its own clearance level; <see cref="PermissionClearance.Unset"/>
    /// means the grant is inert and the action is denied.
    /// </summary>
    private async Task<AgentActionResult> EvaluateGlobalFlagAsync(
        Guid agentId,
        ActionCaller caller,
        Func<PermissionSetDB, bool> hasFlag,
        Func<PermissionSetDB, PermissionClearance> getFlagClearance,
        string flagDescription,
        Func<Task>? onApproved,
        CancellationToken ct)
    {
        var agentPerms = await LoadAgentPermissionsAsync(agentId, ct);
        if (agentPerms is null)
            return AgentActionResult.Denied("Agent has no role or permissions assigned.");

        if (!hasFlag(agentPerms))
            return AgentActionResult.Denied($"Agent does not have permission to {flagDescription}.");

        var effective = ResolveClearance(getFlagClearance(agentPerms));

        var result = await EvaluateCallerClearanceAsync(
            agentPerms, effective, caller,
            callerPerms => hasFlag(callerPerms), ct);

        if (result.Verdict == ClearanceVerdict.Approved && onApproved is not null)
            await onApproved();

        return result;
    }

    /// <summary>
    /// Check whether a permission set has a grant for a specific resource
    /// in the unified <see cref="Infrastructure.Models.Access.ResourceAccessDB"/> collection.
    /// Replaces 18 typed GrantCheckMap lambdas with a single method.
    /// See Module-System-Design §3.10.5.
    /// </summary>
    private static bool HasResourceGrant(
        PermissionSetDB ps, string resourceType, Guid? resourceId)
        => ps.ResourceAccesses.Any(a =>
            a.ResourceType == resourceType
            && (a.ResourceId == resourceId || a.ResourceId == WellKnownIds.AllResources));

    /// <summary>
    /// Evaluate a per-resource grant using the unified
    /// <see cref="Infrastructure.Models.Access.ResourceAccessDB"/> collection.
    /// See Module-System-Design §3.10.5.
    /// </summary>
    private async Task<AgentActionResult> EvaluateResourceAccessAsync(
        Guid agentId,
        Guid resourceId,
        string resourceType,
        ActionCaller caller,
        string resourceDescription,
        Func<Task>? onApproved = null,
        CancellationToken ct = default)
    {
        var agentPerms = await LoadAgentPermissionsAsync(agentId, ct);
        if (agentPerms is null)
        {
            Debug.WriteLine(
                $"[PermissionCheck] DENIED: Agent {agentId} has no role or permission set.",
                "SharpClaw.CLI");
            return AgentActionResult.Denied("Agent has no role or permissions assigned.");
        }

        var access = agentPerms.ResourceAccesses
            .FirstOrDefault(a => a.ResourceType == resourceType
                              && (a.ResourceId == resourceId
                               || a.ResourceId == WellKnownIds.AllResources));

        if (access is null)
        {
            Debug.WriteLine(
                $"[PermissionCheck] DENIED: Agent {agentId} has no '{resourceType}' grant for resource {resourceId}. "
                + $"ResourceAccesses count={agentPerms.ResourceAccesses.Count}, "
                + $"types=[{string.Join(", ", agentPerms.ResourceAccesses.Select(a => $"{a.ResourceType}:{a.ResourceId:D}"))}]",
                "SharpClaw.CLI");
            return AgentActionResult.Denied($"Agent does not have {resourceDescription}.");
        }

        var effective = ResolveClearance(access.Clearance);

        var result = await EvaluateCallerClearanceAsync(
            agentPerms, effective, caller,
            callerPerms => HasResourceGrant(callerPerms, resourceType, resourceId),
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
        // ── Level 0: Unset / Denied — no approval path ────────
        if (effectiveClearance == PermissionClearance.Unset)
            return AgentActionResult.Denied(
                "Permission clearance is not configured (Unset). "
                + "An admin must explicitly set a clearance level.");

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
    /// including the unified resource access collection and whitelists.
    /// </summary>
    public async Task<PermissionSetDB?> LoadPermissionSetAsync(
        Guid permissionSetId, CancellationToken ct)
    {
        var ps = await db.PermissionSets
            .Include(p => p.GlobalFlags)
            .Include(p => p.ResourceAccesses)
            .Include(p => p.ClearanceUserWhitelist)
            .Include(p => p.ClearanceAgentWhitelist)
            .FirstOrDefaultAsync(p => p.Id == permissionSetId, ct);

        return ps;
    }

    // ═══════════════════════════════════════════════════════════════
    // Module delegation (DelegateTo resolution)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Evaluates a permission check by delegate-method name.
    /// Global flags are resolved dynamically via <see cref="ModuleRegistry.ResolveGlobalFlag"/>;
    /// per-resource delegates via <see cref="ModuleRegistry.ResolveResourceType"/>.
    /// Returns <c>null</c> if <paramref name="delegateName"/> is not recognised.
    /// See Module-System-Design §12.4.4.
    /// </summary>
    public Task<AgentActionResult>? TryEvaluateByDelegateNameAsync(
        string delegateName, Guid agentId, Guid? resourceId,
        ActionCaller caller, CancellationToken ct = default)
    {
        var flagKey = registry.ResolveGlobalFlag(delegateName);
        if (flagKey is not null)
            return EvaluateGlobalFlagByKeyAsync(flagKey, agentId, caller, ct: ct);

        var resourceType = registry.ResolveResourceType(delegateName);
        if (resourceType is not null && resourceId.HasValue)
            return EvaluateResourceAccessAsync(
                agentId, resourceId.Value, resourceType, caller,
                $"{resourceType} access", ct: ct);

        return null;
    }

    /// <summary>
    /// Checks whether a <see cref="PermissionSetDB"/> contains a grant
    /// that matches the given delegate-method name and optional resource ID.
    /// Global flags are resolved dynamically via <see cref="ModuleRegistry.ResolveGlobalFlag"/>;
    /// per-resource delegates via <see cref="ModuleRegistry.ResolveResourceType"/>.
    /// Used by channel pre-authorization for module actions.
    /// See Module-System-Design §12.4.4.
    /// </summary>
    public bool HasGrantByDelegateName(
        PermissionSetDB ps, string delegateName, Guid? resourceId)
    {
        var flagKey = registry.ResolveGlobalFlag(delegateName);
        if (flagKey is not null)
            return ps.GlobalFlags.Any(f => f.FlagKey == flagKey);

        var resourceType = registry.ResolveResourceType(delegateName);
        return resourceType is not null && HasResourceGrant(ps, resourceType, resourceId);
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the per-permission clearance as-is. <see cref="PermissionClearance.Unset"/>
    /// is preserved — the caller (<see cref="EvaluateCallerClearanceAsync"/>) treats it
    /// as a hard deny.
    /// </summary>
    private static PermissionClearance ResolveClearance(
        PermissionClearance perPermission) =>
        perPermission;
}
