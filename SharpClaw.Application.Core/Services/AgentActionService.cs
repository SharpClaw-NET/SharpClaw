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
            agentId, caller, p => p.CanCreateSubAgents, p => p.CreateSubAgentsClearance,
            "create sub-agents", onApproved, ct);

    public Task<AgentActionResult> CreateContainerAsync(
        Guid agentId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateGlobalFlagAsync(
            agentId, caller, p => p.CanCreateContainers, p => p.CreateContainersClearance,
            "create containers", onApproved, ct);

    public Task<AgentActionResult> RegisterDatabaseAsync(
        Guid agentId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateGlobalFlagAsync(
            agentId, caller, p => p.CanRegisterDatabases, p => p.RegisterDatabasesClearance,
            "register databases", onApproved, ct);

    public Task<AgentActionResult> AccessLocalhostInBrowserAsync(
        Guid agentId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateGlobalFlagAsync(
            agentId, caller, p => p.CanAccessLocalhostInBrowser, p => p.AccessLocalhostInBrowserClearance,
            "access localhost in browser", onApproved, ct);

    public Task<AgentActionResult> AccessLocalhostCliAsync(
        Guid agentId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateGlobalFlagAsync(
            agentId, caller, p => p.CanAccessLocalhostCli, p => p.AccessLocalhostCliClearance,
            "access localhost via CLI", onApproved, ct);

    public Task<AgentActionResult> ClickDesktopAsync(
        Guid agentId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateGlobalFlagAsync(
            agentId, caller, p => p.CanClickDesktop, p => p.ClickDesktopClearance,
            "click desktop", onApproved, ct);

    public Task<AgentActionResult> TypeOnDesktopAsync(
        Guid agentId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateGlobalFlagAsync(
            agentId, caller, p => p.CanTypeOnDesktop, p => p.TypeOnDesktopClearance,
            "type on desktop", onApproved, ct);

    public Task<AgentActionResult> ReadCrossThreadHistoryAsync(
        Guid agentId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateGlobalFlagAsync(
            agentId, caller, p => p.CanReadCrossThreadHistory, p => p.ReadCrossThreadHistoryClearance,
            "read cross-thread history", onApproved, ct);

    public Task<AgentActionResult> CreateDocumentSessionAsync(
        Guid agentId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateGlobalFlagAsync(
            agentId, caller, p => p.CanCreateDocumentSessions, p => p.CreateDocumentSessionsClearance,
            "create document sessions", onApproved, ct);

    public Task<AgentActionResult> EnumerateWindowsAsync(
        Guid agentId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateGlobalFlagAsync(
            agentId, caller, p => p.CanEnumerateWindows, p => p.EnumerateWindowsClearance,
            "enumerate windows", onApproved, ct);

    public Task<AgentActionResult> FocusWindowAsync(
        Guid agentId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateGlobalFlagAsync(
            agentId, caller, p => p.CanFocusWindow, p => p.FocusWindowClearance,
            "focus window", onApproved, ct);

    public Task<AgentActionResult> CloseWindowAsync(
        Guid agentId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateGlobalFlagAsync(
            agentId, caller, p => p.CanCloseWindow, p => p.CloseWindowClearance,
            "close window", onApproved, ct);

    public Task<AgentActionResult> ResizeWindowAsync(
        Guid agentId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateGlobalFlagAsync(
            agentId, caller, p => p.CanResizeWindow, p => p.ResizeWindowClearance,
            "resize window", onApproved, ct);

    public Task<AgentActionResult> SendHotkeyAsync(
        Guid agentId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateGlobalFlagAsync(
            agentId, caller, p => p.CanSendHotkey, p => p.SendHotkeyClearance,
            "send hotkey", onApproved, ct);

    public Task<AgentActionResult> ReadClipboardAsync(
        Guid agentId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateGlobalFlagAsync(
            agentId, caller, p => p.CanReadClipboard, p => p.ReadClipboardClearance,
            "read clipboard", onApproved, ct);

    public Task<AgentActionResult> WriteClipboardAsync(
        Guid agentId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateGlobalFlagAsync(
            agentId, caller, p => p.CanWriteClipboard, p => p.WriteClipboardClearance,
            "write clipboard", onApproved, ct);

    // ═══════════════════════════════════════════════════════════════
    // Per-resource actions (generic ResourceAccessDB §3.10)
    // ═══════════════════════════════════════════════════════════════

    public Task<AgentActionResult> UnsafeExecuteAsDangerousShellAsync(
        Guid agentId, Guid systemUserId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, systemUserId, ResourceTypes.DsShell, caller,
            "dangerous shell access", onApproved, ct);

    public Task<AgentActionResult> ExecuteAsSafeShellAsync(
        Guid agentId, Guid containerId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, containerId, ResourceTypes.Mk8Shell, caller,
            "safe shell access", onApproved, ct);

    public Task<AgentActionResult> AccessInternalDatabaseAsync(
        Guid agentId, Guid databaseId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, databaseId, ResourceTypes.DbInternal, caller,
            "internal database access", onApproved, ct);

    public Task<AgentActionResult> AccessExternalDatabaseAsync(
        Guid agentId, Guid databaseId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, databaseId, ResourceTypes.DbExternal, caller,
            "external database access", onApproved, ct);

    public Task<AgentActionResult> AccessWebsiteAsync(
        Guid agentId, Guid websiteId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, websiteId, ResourceTypes.WaWebsite, caller,
            "website access", onApproved, ct);

    public Task<AgentActionResult> QuerySearchEngineAsync(
        Guid agentId, Guid searchEngineId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, searchEngineId, ResourceTypes.WaSearch, caller,
            "search engine access", onApproved, ct);

    public Task<AgentActionResult> AccessContainerAsync(
        Guid agentId, Guid containerId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, containerId, ResourceTypes.Container, caller,
            "container access", onApproved, ct);

    public Task<AgentActionResult> ManageAgentAsync(
        Guid agentId, Guid targetAgentId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, targetAgentId, ResourceTypes.AoAgent, caller,
            "agent management", onApproved, ct);

    public Task<AgentActionResult> EditTaskAsync(
        Guid agentId, Guid taskId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, taskId, ResourceTypes.AoTask, caller,
            "task edit", onApproved, ct);

    public Task<AgentActionResult> AccessSkillAsync(
        Guid agentId, Guid skillId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, skillId, ResourceTypes.AoSkill, caller,
            "skill access", onApproved, ct);

    public Task<AgentActionResult> AccessInputAudioAsync(
        Guid agentId, Guid inputAudioId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, inputAudioId, ResourceTypes.TrAudio, caller,
            "input audio access", onApproved, ct);

    public Task<AgentActionResult> AccessDisplayDeviceAsync(
        Guid agentId, Guid displayDeviceId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, displayDeviceId, ResourceTypes.CuDisplay, caller,
            "display device access", onApproved, ct);

    public Task<AgentActionResult> AccessEditorSessionAsync(
        Guid agentId, Guid editorSessionId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, editorSessionId, ResourceTypes.EditorSession, caller,
            "editor session access", onApproved, ct);

    public Task<AgentActionResult> AccessBotIntegrationAsync(
        Guid agentId, Guid botIntegrationId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, botIntegrationId, ResourceTypes.BiChannel, caller,
            "bot integration access", onApproved, ct);

    public Task<AgentActionResult> AccessDocumentSessionAsync(
        Guid agentId, Guid documentSessionId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, documentSessionId, ResourceTypes.OaDocument, caller,
            "document session access", onApproved, ct);

    public Task<AgentActionResult> LaunchNativeApplicationAsync(
        Guid agentId, Guid nativeApplicationId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, nativeApplicationId, ResourceTypes.CuNativeApp, caller,
            "native application launch", onApproved, ct);

    public Task<AgentActionResult> EditAgentHeaderAsync(
        Guid agentId, Guid targetAgentId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, targetAgentId, ResourceTypes.AoAgentHeader, caller,
            "agent header edit", onApproved, ct);

    public Task<AgentActionResult> EditChannelHeaderAsync(
        Guid agentId, Guid targetChannelId, ActionCaller caller,
        Func<Task>? onApproved = null, CancellationToken ct = default)
        => EvaluateResourceAccessAsync(
            agentId, targetChannelId, ResourceTypes.AoChannelHeader, caller,
            "channel header edit", onApproved, ct);

    // ═══════════════════════════════════════════════════════════════
    // Core evaluation engine
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Evaluate a boolean (global-flag) permission.
    /// Each flag has its own clearance level; when it is
    /// <see cref="PermissionClearance.Unset"/> the group
    /// <see cref="PermissionSetDB.DefaultClearance"/> is used.
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

        var effective = ResolveClearance(getFlagClearance(agentPerms), agentPerms.DefaultClearance);

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
            return AgentActionResult.Denied("Agent has no role or permissions assigned.");

        var access = agentPerms.ResourceAccesses
            .FirstOrDefault(a => a.ResourceType == resourceType
                              && (a.ResourceId == resourceId
                               || a.ResourceId == WellKnownIds.AllResources));

        if (access is null)
            return AgentActionResult.Denied($"Agent does not have {resourceDescription}.");

        var effective = ResolveClearance(access.Clearance, agentPerms.DefaultClearance);

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
        return await db.PermissionSets
            .Include(p => p.ResourceAccesses)
            .Include(p => p.ClearanceUserWhitelist)
            .Include(p => p.ClearanceAgentWhitelist)
            .FirstOrDefaultAsync(p => p.Id == permissionSetId, ct);
    }

    // ═══════════════════════════════════════════════════════════════
    // Module delegation (DelegateTo resolution)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Maps <see cref="Contracts.Modules.ModuleToolPermission.DelegateTo"/>
    /// method-name strings to the corresponding permission evaluation.
    /// Modules declare which existing permission check to reuse (e.g. a
    /// shell module tool reuses <c>AccessContainerAsync</c>).
    /// </summary>
    private static readonly Dictionary<string, Func<AgentActionService, Guid, Guid?, ActionCaller, CancellationToken, Task<AgentActionResult>>>
        DelegationMap = new(StringComparer.Ordinal)
    {
        // Global flags
        ["CreateSubAgentAsync"] = (svc, agentId, _, caller, ct) => svc.CreateSubAgentAsync(agentId, caller, ct: ct),
        ["CreateContainerAsync"] = (svc, agentId, _, caller, ct) => svc.CreateContainerAsync(agentId, caller, ct: ct),
        ["RegisterDatabaseAsync"] = (svc, agentId, _, caller, ct) => svc.RegisterDatabaseAsync(agentId, caller, ct: ct),
        ["AccessLocalhostInBrowserAsync"] = (svc, agentId, _, caller, ct) => svc.AccessLocalhostInBrowserAsync(agentId, caller, ct: ct),
        ["AccessLocalhostCliAsync"] = (svc, agentId, _, caller, ct) => svc.AccessLocalhostCliAsync(agentId, caller, ct: ct),
        ["ClickDesktopAsync"] = (svc, agentId, _, caller, ct) => svc.ClickDesktopAsync(agentId, caller, ct: ct),
        ["TypeOnDesktopAsync"] = (svc, agentId, _, caller, ct) => svc.TypeOnDesktopAsync(agentId, caller, ct: ct),
        ["ReadCrossThreadHistoryAsync"] = (svc, agentId, _, caller, ct) => svc.ReadCrossThreadHistoryAsync(agentId, caller, ct: ct),
        ["CreateDocumentSessionAsync"] = (svc, agentId, _, caller, ct) => svc.CreateDocumentSessionAsync(agentId, caller, ct: ct),
        ["EnumerateWindowsAsync"] = (svc, agentId, _, caller, ct) => svc.EnumerateWindowsAsync(agentId, caller, ct: ct),
        ["FocusWindowAsync"] = (svc, agentId, _, caller, ct) => svc.FocusWindowAsync(agentId, caller, ct: ct),
        ["CloseWindowAsync"] = (svc, agentId, _, caller, ct) => svc.CloseWindowAsync(agentId, caller, ct: ct),
        ["ResizeWindowAsync"] = (svc, agentId, _, caller, ct) => svc.ResizeWindowAsync(agentId, caller, ct: ct),
        ["SendHotkeyAsync"] = (svc, agentId, _, caller, ct) => svc.SendHotkeyAsync(agentId, caller, ct: ct),
        ["ReadClipboardAsync"] = (svc, agentId, _, caller, ct) => svc.ReadClipboardAsync(agentId, caller, ct: ct),
        ["WriteClipboardAsync"] = (svc, agentId, _, caller, ct) => svc.WriteClipboardAsync(agentId, caller, ct: ct),

        // Per-resource
        ["UnsafeExecuteAsDangerousShellAsync"] = (svc, agentId, resId, caller, ct) => svc.UnsafeExecuteAsDangerousShellAsync(agentId, resId!.Value, caller, ct: ct),
        ["ExecuteAsSafeShellAsync"] = (svc, agentId, resId, caller, ct) => svc.ExecuteAsSafeShellAsync(agentId, resId!.Value, caller, ct: ct),
        ["AccessInternalDatabaseAsync"] = (svc, agentId, resId, caller, ct) => svc.AccessInternalDatabaseAsync(agentId, resId!.Value, caller, ct: ct),
        ["AccessExternalDatabaseAsync"] = (svc, agentId, resId, caller, ct) => svc.AccessExternalDatabaseAsync(agentId, resId!.Value, caller, ct: ct),
        ["AccessWebsiteAsync"] = (svc, agentId, resId, caller, ct) => svc.AccessWebsiteAsync(agentId, resId!.Value, caller, ct: ct),
        ["QuerySearchEngineAsync"] = (svc, agentId, resId, caller, ct) => svc.QuerySearchEngineAsync(agentId, resId!.Value, caller, ct: ct),
        ["AccessContainerAsync"] = (svc, agentId, resId, caller, ct) => svc.AccessContainerAsync(agentId, resId!.Value, caller, ct: ct),
        ["ManageAgentAsync"] = (svc, agentId, resId, caller, ct) => svc.ManageAgentAsync(agentId, resId!.Value, caller, ct: ct),
        ["EditTaskAsync"] = (svc, agentId, resId, caller, ct) => svc.EditTaskAsync(agentId, resId!.Value, caller, ct: ct),
        ["AccessSkillAsync"] = (svc, agentId, resId, caller, ct) => svc.AccessSkillAsync(agentId, resId!.Value, caller, ct: ct),
        ["AccessInputAudioAsync"] = (svc, agentId, resId, caller, ct) => svc.AccessInputAudioAsync(agentId, resId!.Value, caller, ct: ct),
        ["AccessDisplayDeviceAsync"] = (svc, agentId, resId, caller, ct) => svc.AccessDisplayDeviceAsync(agentId, resId!.Value, caller, ct: ct),
        ["AccessEditorSessionAsync"] = (svc, agentId, resId, caller, ct) => svc.AccessEditorSessionAsync(agentId, resId!.Value, caller, ct: ct),
        ["AccessBotIntegrationAsync"] = (svc, agentId, resId, caller, ct) => svc.AccessBotIntegrationAsync(agentId, resId!.Value, caller, ct: ct),
        ["AccessDocumentSessionAsync"] = (svc, agentId, resId, caller, ct) => svc.AccessDocumentSessionAsync(agentId, resId!.Value, caller, ct: ct),
        ["LaunchNativeApplicationAsync"] = (svc, agentId, resId, caller, ct) => svc.LaunchNativeApplicationAsync(agentId, resId!.Value, caller, ct: ct),
        ["EditAgentHeaderAsync"] = (svc, agentId, resId, caller, ct) => svc.EditAgentHeaderAsync(agentId, resId!.Value, caller, ct: ct),
        ["EditChannelHeaderAsync"] = (svc, agentId, resId, caller, ct) => svc.EditChannelHeaderAsync(agentId, resId!.Value, caller, ct: ct),
    };

    /// <summary>
    /// Maps <see cref="Contracts.Modules.ModuleToolPermission.DelegateTo"/>
    /// method-name strings to grant-existence checks against a
    /// <see cref="PermissionSetDB"/>. Used by channel pre-authorization.
    /// </summary>
    private static readonly Dictionary<string, Func<PermissionSetDB, Guid?, bool>>
        GrantCheckMap = new(StringComparer.Ordinal)
    {
        // Global flags (resourceId ignored)
        ["CreateSubAgentAsync"] = (ps, _) => ps.CanCreateSubAgents,
        ["CreateContainerAsync"] = (ps, _) => ps.CanCreateContainers,
        ["RegisterDatabaseAsync"] = (ps, _) => ps.CanRegisterDatabases,
        ["AccessLocalhostInBrowserAsync"] = (ps, _) => ps.CanAccessLocalhostInBrowser,
        ["AccessLocalhostCliAsync"] = (ps, _) => ps.CanAccessLocalhostCli,
        ["ClickDesktopAsync"] = (ps, _) => ps.CanClickDesktop,
        ["TypeOnDesktopAsync"] = (ps, _) => ps.CanTypeOnDesktop,
        ["ReadCrossThreadHistoryAsync"] = (ps, _) => ps.CanReadCrossThreadHistory,
        ["CreateDocumentSessionAsync"] = (ps, _) => ps.CanCreateDocumentSessions,
        ["EnumerateWindowsAsync"] = (ps, _) => ps.CanEnumerateWindows,
        ["FocusWindowAsync"] = (ps, _) => ps.CanFocusWindow,
        ["CloseWindowAsync"] = (ps, _) => ps.CanCloseWindow,
        ["ResizeWindowAsync"] = (ps, _) => ps.CanResizeWindow,
        ["SendHotkeyAsync"] = (ps, _) => ps.CanSendHotkey,
        ["ReadClipboardAsync"] = (ps, _) => ps.CanReadClipboard,
        ["WriteClipboardAsync"] = (ps, _) => ps.CanWriteClipboard,

        // Per-resource (generic ResourceAccessDB §3.10)
        ["UnsafeExecuteAsDangerousShellAsync"] = (ps, rid) => HasResourceGrant(ps, ResourceTypes.DsShell, rid),
        ["ExecuteAsSafeShellAsync"] = (ps, rid) => HasResourceGrant(ps, ResourceTypes.Mk8Shell, rid),
        ["AccessInternalDatabaseAsync"] = (ps, rid) => HasResourceGrant(ps, ResourceTypes.DbInternal, rid),
        ["AccessExternalDatabaseAsync"] = (ps, rid) => HasResourceGrant(ps, ResourceTypes.DbExternal, rid),
        ["AccessWebsiteAsync"] = (ps, rid) => HasResourceGrant(ps, ResourceTypes.WaWebsite, rid),
        ["QuerySearchEngineAsync"] = (ps, rid) => HasResourceGrant(ps, ResourceTypes.WaSearch, rid),
        ["AccessContainerAsync"] = (ps, rid) => HasResourceGrant(ps, ResourceTypes.Container, rid),
        ["ManageAgentAsync"] = (ps, rid) => HasResourceGrant(ps, ResourceTypes.AoAgent, rid),
        ["EditTaskAsync"] = (ps, rid) => HasResourceGrant(ps, ResourceTypes.AoTask, rid),
        ["AccessSkillAsync"] = (ps, rid) => HasResourceGrant(ps, ResourceTypes.AoSkill, rid),
        ["AccessInputAudioAsync"] = (ps, rid) => HasResourceGrant(ps, ResourceTypes.TrAudio, rid),
        ["AccessDisplayDeviceAsync"] = (ps, rid) => HasResourceGrant(ps, ResourceTypes.CuDisplay, rid),
        ["AccessEditorSessionAsync"] = (ps, rid) => HasResourceGrant(ps, ResourceTypes.EditorSession, rid),
        ["AccessBotIntegrationAsync"] = (ps, rid) => HasResourceGrant(ps, ResourceTypes.BiChannel, rid),
        ["AccessDocumentSessionAsync"] = (ps, rid) => HasResourceGrant(ps, ResourceTypes.OaDocument, rid),
        ["LaunchNativeApplicationAsync"] = (ps, rid) => HasResourceGrant(ps, ResourceTypes.CuNativeApp, rid),
        ["EditAgentHeaderAsync"] = (ps, rid) => HasResourceGrant(ps, ResourceTypes.AoAgentHeader, rid),
        ["EditChannelHeaderAsync"] = (ps, rid) => HasResourceGrant(ps, ResourceTypes.AoChannelHeader, rid),
    };

    /// <summary>
    /// Evaluates a permission check by delegate-method name. Returns
    /// <c>null</c> if <paramref name="delegateName"/> is not a recognised
    /// method name.
    /// </summary>
    public Task<AgentActionResult>? TryEvaluateByDelegateNameAsync(
        string delegateName, Guid agentId, Guid? resourceId,
        ActionCaller caller, CancellationToken ct = default)
    {
        return DelegationMap.TryGetValue(delegateName, out var factory)
            ? factory(this, agentId, resourceId, caller, ct)
            : null;
    }

    /// <summary>
    /// Checks whether a <see cref="PermissionSetDB"/> contains a grant
    /// that matches the given delegate-method name and optional resource ID.
    /// Used by channel pre-authorization for module actions.
    /// </summary>
    public static bool HasGrantByDelegateName(
        PermissionSetDB ps, string delegateName, Guid? resourceId)
    {
        return GrantCheckMap.TryGetValue(delegateName, out var check)
            && check(ps, resourceId);
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
