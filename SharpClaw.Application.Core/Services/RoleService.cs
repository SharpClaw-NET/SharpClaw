using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.Infrastructure.Models.Access;
using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.Roles;
using SharpClaw.Contracts.Enums;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

/// <summary>
/// Manages roles and their permission sets. Enforces the rule that a
/// user can only grant permissions they already hold themselves.
/// </summary>
public sealed class RoleService(SharpClawDbContext db)
{
    // ═══════════════════════════════════════════════════════════════
    // Read
    // ═══════════════════════════════════════════════════════════════

    public async Task<IReadOnlyList<RoleResponse>> ListAsync(CancellationToken ct = default)
    {
        return await db.Roles
            .OrderBy(r => r.Name)
            .Select(r => new RoleResponse(r.Id, r.Name, r.PermissionSetId))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Creates a new role with an empty permission set.
    /// </summary>
    public async Task<RoleResponse> CreateAsync(
        string name, CancellationToken ct = default)
    {
        var ps = new PermissionSetDB();
        db.PermissionSets.Add(ps);
        await db.SaveChangesAsync(ct);

        var role = new RoleDB { Name = name, PermissionSetId = ps.Id };
        db.Roles.Add(role);
        await db.SaveChangesAsync(ct);

        return new RoleResponse(role.Id, role.Name, role.PermissionSetId);
    }

    public async Task<RoleResponse?> GetByIdAsync(
        Guid roleId, CancellationToken ct = default)
    {
        return await db.Roles
            .Where(r => r.Id == roleId)
            .Select(r => new RoleResponse(r.Id, r.Name, r.PermissionSetId))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<RolePermissionsResponse?> GetPermissionsAsync(
        Guid roleId, CancellationToken ct = default)
    {
        var role = await db.Roles
            .Include(r => r.PermissionSet)
            .FirstOrDefaultAsync(r => r.Id == roleId, ct);

        if (role is null)
            return null;

        var ps = role.PermissionSet is not null
            ? await LoadFullPermissionSetAsync(role.PermissionSet.Id, ct)
            : null;

        return ToResponse(role, ps);
    }

    // ═══════════════════════════════════════════════════════════════
    // Set permissions
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Replaces the entire permission set of <paramref name="roleId"/>
    /// with the values in <paramref name="request"/>. The calling user
    /// (<paramref name="callerUserId"/>) must hold every permission
    /// they are granting — you cannot give what you don't have.
    /// </summary>
    public async Task<RolePermissionsResponse?> SetPermissionsAsync(
        Guid roleId, SetRolePermissionsRequest request,
        Guid callerUserId, CancellationToken ct = default)
    {
        var role = await db.Roles
            .Include(r => r.PermissionSet)
            .FirstOrDefaultAsync(r => r.Id == roleId, ct);

        if (role is null)
            return null;

        // Admin users bypass permission validation — they can grant anything.
        var isAdmin = await db.Users.AnyAsync(u => u.Id == callerUserId && u.IsUserAdmin, ct);
        if (!isAdmin)
        {
            // Load caller's own permission set for validation.
            var callerPs = await LoadCallerPermissionSetAsync(callerUserId, ct);

            ValidateGlobalFlags(request, callerPs);
            ValidateResourceGrants(request, callerPs);
        }

        // Replace the permission set (delete old access entries, rebuild).
        PermissionSetDB ps;
        if (role.PermissionSetId is { } existingPsId)
        {
            ps = await LoadFullPermissionSetAsync(existingPsId, ct)
                ?? throw new InvalidOperationException(
                    $"Permission set {existingPsId} not found.");

            // Clear existing grant collections.
            ps.ResourceAccesses.Clear();
        }
        else
        {
            ps = new PermissionSetDB();
            db.PermissionSets.Add(ps);
            await db.SaveChangesAsync(ct);
            role.PermissionSetId = ps.Id;
        }

        // Apply global flags.
        ps.DefaultClearance = request.DefaultClearance;
        ps.CanCreateSubAgents = request.CanCreateSubAgents;
        ps.CreateSubAgentsClearance = request.CreateSubAgentsClearance;
        ps.CanCreateContainers = request.CanCreateContainers;
        ps.CreateContainersClearance = request.CreateContainersClearance;
        ps.CanRegisterDatabases = request.CanRegisterDatabases;
        ps.RegisterDatabasesClearance = request.RegisterDatabasesClearance;
        ps.CanAccessLocalhostInBrowser = request.CanAccessLocalhostInBrowser;
        ps.AccessLocalhostInBrowserClearance = request.AccessLocalhostInBrowserClearance;
        ps.CanAccessLocalhostCli = request.CanAccessLocalhostCli;
        ps.AccessLocalhostCliClearance = request.AccessLocalhostCliClearance;
        ps.CanClickDesktop = request.CanClickDesktop;
        ps.ClickDesktopClearance = request.ClickDesktopClearance;
        ps.CanTypeOnDesktop = request.CanTypeOnDesktop;
        ps.TypeOnDesktopClearance = request.TypeOnDesktopClearance;
        ps.CanReadCrossThreadHistory = request.CanReadCrossThreadHistory;
        ps.ReadCrossThreadHistoryClearance = request.ReadCrossThreadHistoryClearance;
        ps.CanEditAgentHeader = request.CanEditAgentHeader;
        ps.EditAgentHeaderClearance = request.EditAgentHeaderClearance;
        ps.CanEditChannelHeader = request.CanEditChannelHeader;
        ps.EditChannelHeaderClearance = request.EditChannelHeaderClearance;
        ps.CanCreateDocumentSessions = request.CanCreateDocumentSessions;
        ps.CreateDocumentSessionsClearance = request.CreateDocumentSessionsClearance;
        ps.CanEnumerateWindows = request.CanEnumerateWindows;
        ps.EnumerateWindowsClearance = request.EnumerateWindowsClearance;
        ps.CanFocusWindow = request.CanFocusWindow;
        ps.FocusWindowClearance = request.FocusWindowClearance;
        ps.CanCloseWindow = request.CanCloseWindow;
        ps.CloseWindowClearance = request.CloseWindowClearance;
        ps.CanResizeWindow = request.CanResizeWindow;
        ps.ResizeWindowClearance = request.ResizeWindowClearance;
        ps.CanSendHotkey = request.CanSendHotkey;
        ps.SendHotkeyClearance = request.SendHotkeyClearance;
        ps.CanReadClipboard = request.CanReadClipboard;
        ps.ReadClipboardClearance = request.ReadClipboardClearance;
        ps.CanWriteClipboard = request.CanWriteClipboard;
        ps.WriteClipboardClearance = request.WriteClipboardClearance;

        // Apply per-resource grants.
        AddResourceGrants(ps, ResourceTypes.DsShell, request.DangerousShellAccesses);
        AddResourceGrants(ps, ResourceTypes.Mk8Shell, request.SafeShellAccesses);
        AddResourceGrants(ps, ResourceTypes.Container, request.ContainerAccesses);
        AddResourceGrants(ps, ResourceTypes.WaWebsite, request.WebsiteAccesses);
        AddResourceGrants(ps, ResourceTypes.WaSearch, request.SearchEngineAccesses);
        AddResourceGrants(ps, ResourceTypes.DbInternal, request.InternalDatabaseAccesses);
        AddResourceGrants(ps, ResourceTypes.DbExternal, request.ExternalDatabaseAccesses);
        AddResourceGrants(ps, ResourceTypes.TrAudio, request.InputAudioAccesses);
        AddResourceGrants(ps, ResourceTypes.CuDisplay, request.DisplayDeviceAccesses);
        AddResourceGrants(ps, ResourceTypes.EditorSession, request.EditorSessionAccesses);
        AddResourceGrants(ps, ResourceTypes.AoAgent, request.AgentAccesses);
        AddResourceGrants(ps, ResourceTypes.AoTask, request.TaskAccesses);
        AddResourceGrants(ps, ResourceTypes.AoSkill, request.SkillAccesses);
        AddResourceGrants(ps, ResourceTypes.AoAgentHeader, request.AgentHeaderAccesses);
        AddResourceGrants(ps, ResourceTypes.AoChannelHeader, request.ChannelHeaderAccesses);
        AddResourceGrants(ps, ResourceTypes.OaDocument, request.DocumentSessionAccesses);
        AddResourceGrants(ps, ResourceTypes.CuNativeApp, request.NativeApplicationAccesses);

        await db.SaveChangesAsync(ct);

        return ToResponse(role, ps);
    }

    // ═══════════════════════════════════════════════════════════════
    // Validation — you can only grant what you hold
    // ═══════════════════════════════════════════════════════════════

    private static void ValidateGlobalFlags(
        SetRolePermissionsRequest request, PermissionSetDB? callerPs)
    {
        if (callerPs is null)
        {
            if (request is
                {
                    CanCreateSubAgents: false,
                    CanCreateContainers: false,
                    CanRegisterDatabases: false,
                    CanAccessLocalhostInBrowser: false,
                    CanAccessLocalhostCli: false,
                    CanClickDesktop: false,
                    CanTypeOnDesktop: false,
                    CanReadCrossThreadHistory: false,
                    CanEditAgentHeader: false,
                    CanEditChannelHeader: false,
                    CanCreateDocumentSessions: false,
                    CanEnumerateWindows: false,
                    CanFocusWindow: false,
                    CanCloseWindow: false,
                    CanResizeWindow: false,
                    CanSendHotkey: false,
                    CanReadClipboard: false,
                    CanWriteClipboard: false
                })
                return;

            throw new UnauthorizedAccessException(
                "You have no permissions — cannot grant any global flags.");
        }

        if (request.CanCreateSubAgents && !callerPs.CanCreateSubAgents)
            throw new UnauthorizedAccessException(
                "Cannot grant CanCreateSubAgents — you do not hold this permission.");

        if (request.CanCreateContainers && !callerPs.CanCreateContainers)
            throw new UnauthorizedAccessException(
                "Cannot grant CanCreateContainers — you do not hold this permission.");

        if (request.CanRegisterDatabases && !callerPs.CanRegisterDatabases)
            throw new UnauthorizedAccessException(
                "Cannot grant CanRegisterDatabases — you do not hold this permission.");

        if (request.CanAccessLocalhostInBrowser && !callerPs.CanAccessLocalhostInBrowser)
            throw new UnauthorizedAccessException(
                "Cannot grant CanAccessLocalhostInBrowser — you do not hold this permission.");

        if (request.CanAccessLocalhostCli && !callerPs.CanAccessLocalhostCli)
            throw new UnauthorizedAccessException(
                "Cannot grant CanAccessLocalhostCli — you do not hold this permission.");

        if (request.CanClickDesktop && !callerPs.CanClickDesktop)
            throw new UnauthorizedAccessException(
                "Cannot grant CanClickDesktop — you do not hold this permission.");

        if (request.CanTypeOnDesktop && !callerPs.CanTypeOnDesktop)
            throw new UnauthorizedAccessException(
                "Cannot grant CanTypeOnDesktop — you do not hold this permission.");

        if (request.CanReadCrossThreadHistory && !callerPs.CanReadCrossThreadHistory)
            throw new UnauthorizedAccessException(
                "Cannot grant CanReadCrossThreadHistory — you do not hold this permission.");

        if (request.CanEditAgentHeader && !callerPs.CanEditAgentHeader)
            throw new UnauthorizedAccessException(
                "Cannot grant CanEditAgentHeader — you do not hold this permission.");

        if (request.CanEditChannelHeader && !callerPs.CanEditChannelHeader)
            throw new UnauthorizedAccessException(
                "Cannot grant CanEditChannelHeader — you do not hold this permission.");

        if (request.CanCreateDocumentSessions && !callerPs.CanCreateDocumentSessions)
            throw new UnauthorizedAccessException(
                "Cannot grant CanCreateDocumentSessions — you do not hold this permission.");

        if (request.CanEnumerateWindows && !callerPs.CanEnumerateWindows)
            throw new UnauthorizedAccessException(
                "Cannot grant CanEnumerateWindows — you do not hold this permission.");

        if (request.CanFocusWindow && !callerPs.CanFocusWindow)
            throw new UnauthorizedAccessException(
                "Cannot grant CanFocusWindow — you do not hold this permission.");

        if (request.CanCloseWindow && !callerPs.CanCloseWindow)
            throw new UnauthorizedAccessException(
                "Cannot grant CanCloseWindow — you do not hold this permission.");

        if (request.CanResizeWindow && !callerPs.CanResizeWindow)
            throw new UnauthorizedAccessException(
                "Cannot grant CanResizeWindow — you do not hold this permission.");

        if (request.CanSendHotkey && !callerPs.CanSendHotkey)
            throw new UnauthorizedAccessException(
                "Cannot grant CanSendHotkey — you do not hold this permission.");

        if (request.CanReadClipboard && !callerPs.CanReadClipboard)
            throw new UnauthorizedAccessException(
                "Cannot grant CanReadClipboard — you do not hold this permission.");

        if (request.CanWriteClipboard && !callerPs.CanWriteClipboard)
            throw new UnauthorizedAccessException(
                "Cannot grant CanWriteClipboard — you do not hold this permission.");
    }

    private static void ValidateResourceGrants(
        SetRolePermissionsRequest request, PermissionSetDB? callerPs)
    {
        ValidateGrants("DangerousShellAccesses", ResourceTypes.DsShell, request.DangerousShellAccesses, callerPs);
        ValidateGrants("SafeShellAccesses", ResourceTypes.Mk8Shell, request.SafeShellAccesses, callerPs);
        ValidateGrants("ContainerAccesses", ResourceTypes.Container, request.ContainerAccesses, callerPs);
        ValidateGrants("WebsiteAccesses", ResourceTypes.WaWebsite, request.WebsiteAccesses, callerPs);
        ValidateGrants("SearchEngineAccesses", ResourceTypes.WaSearch, request.SearchEngineAccesses, callerPs);
        ValidateGrants("InternalDatabaseAccesses", ResourceTypes.DbInternal, request.InternalDatabaseAccesses, callerPs);
        ValidateGrants("ExternalDatabaseAccesses", ResourceTypes.DbExternal, request.ExternalDatabaseAccesses, callerPs);
        ValidateGrants("InputAudioAccesses", ResourceTypes.TrAudio, request.InputAudioAccesses, callerPs);
        ValidateGrants("DisplayDeviceAccesses", ResourceTypes.CuDisplay, request.DisplayDeviceAccesses, callerPs);
        ValidateGrants("EditorSessionAccesses", ResourceTypes.EditorSession, request.EditorSessionAccesses, callerPs);
        ValidateGrants("AgentAccesses", ResourceTypes.AoAgent, request.AgentAccesses, callerPs);
        ValidateGrants("TaskAccesses", ResourceTypes.AoTask, request.TaskAccesses, callerPs);
        ValidateGrants("SkillAccesses", ResourceTypes.AoSkill, request.SkillAccesses, callerPs);
        ValidateGrants("AgentHeaderAccesses", ResourceTypes.AoAgentHeader, request.AgentHeaderAccesses, callerPs);
        ValidateGrants("ChannelHeaderAccesses", ResourceTypes.AoChannelHeader, request.ChannelHeaderAccesses, callerPs);
        ValidateGrants("DocumentSessionAccesses", ResourceTypes.OaDocument, request.DocumentSessionAccesses, callerPs);
        ValidateGrants("NativeApplicationAccesses", ResourceTypes.CuNativeApp, request.NativeApplicationAccesses, callerPs);
    }

    /// <summary>
    /// For each requested grant, the caller must hold either the exact
    /// resource ID or the <see cref="WellKnownIds.AllResources"/> wildcard.
    /// </summary>
    private static void ValidateGrants(
        string name,
        string resourceType,
        IReadOnlyList<ResourceGrant>? requested,
        PermissionSetDB? callerPs)
    {
        if (requested is null or { Count: 0 })
            return;

        var callerGrants = callerPs?.ResourceAccesses
            .Where(a => a.ResourceType == resourceType)
            .Select(a => a.ResourceId)
            .ToHashSet();

        if (callerGrants is null or { Count: 0 })
            throw new UnauthorizedAccessException(
                $"Cannot grant {name} — you hold no grants of this type.");

        var hasWildcard = callerGrants.Contains(WellKnownIds.AllResources);

        foreach (var grant in requested)
        {
            if (hasWildcard)
                continue;

            if (!callerGrants.Contains(grant.ResourceId))
                throw new UnauthorizedAccessException(
                    $"Cannot grant {name} for resource {grant.ResourceId} " +
                    "— you do not hold this permission.");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private static void AddResourceGrants(
        PermissionSetDB ps,
        string resourceType,
        IReadOnlyList<ResourceGrant>? grants)
    {
        if (grants is null)
            return;

        foreach (var g in grants)
        {
            ps.ResourceAccesses.Add(new ResourceAccessDB
            {
                ResourceType = resourceType,
                ResourceId = g.ResourceId,
                Clearance = g.Clearance,
            });
        }
    }

    private async Task<PermissionSetDB?> LoadCallerPermissionSetAsync(
        Guid userId, CancellationToken ct)
    {
        var user = await db.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user?.Role?.PermissionSetId is not { } psId)
            return null;

        return await LoadFullPermissionSetAsync(psId, ct);
    }

    private async Task<PermissionSetDB?> LoadFullPermissionSetAsync(
        Guid psId, CancellationToken ct)
    {
        return await db.PermissionSets
            .Include(p => p.ResourceAccesses)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == psId, ct);
    }

    private static RolePermissionsResponse ToResponse(RoleDB role, PermissionSetDB? ps) =>
        new(
            RoleId: role.Id,
            RoleName: role.Name,
            DefaultClearance: ps?.DefaultClearance ?? PermissionClearance.Unset,
            CanCreateSubAgents: ps?.CanCreateSubAgents ?? false,
            CreateSubAgentsClearance: ps?.CreateSubAgentsClearance ?? PermissionClearance.Unset,
            CanCreateContainers: ps?.CanCreateContainers ?? false,
            CreateContainersClearance: ps?.CreateContainersClearance ?? PermissionClearance.Unset,
            CanRegisterDatabases: ps?.CanRegisterDatabases ?? false,
            RegisterDatabasesClearance: ps?.RegisterDatabasesClearance ?? PermissionClearance.Unset,
            CanAccessLocalhostInBrowser: ps?.CanAccessLocalhostInBrowser ?? false,
            AccessLocalhostInBrowserClearance: ps?.AccessLocalhostInBrowserClearance ?? PermissionClearance.Unset,
            CanAccessLocalhostCli: ps?.CanAccessLocalhostCli ?? false,
            AccessLocalhostCliClearance: ps?.AccessLocalhostCliClearance ?? PermissionClearance.Unset,
            CanClickDesktop: ps?.CanClickDesktop ?? false,
            ClickDesktopClearance: ps?.ClickDesktopClearance ?? PermissionClearance.Unset,
            CanTypeOnDesktop: ps?.CanTypeOnDesktop ?? false,
            TypeOnDesktopClearance: ps?.TypeOnDesktopClearance ?? PermissionClearance.Unset,
            CanReadCrossThreadHistory: ps?.CanReadCrossThreadHistory ?? false,
            ReadCrossThreadHistoryClearance: ps?.ReadCrossThreadHistoryClearance ?? PermissionClearance.Unset,
            CanEditAgentHeader: ps?.CanEditAgentHeader ?? false,
            EditAgentHeaderClearance: ps?.EditAgentHeaderClearance ?? PermissionClearance.Unset,
            CanEditChannelHeader: ps?.CanEditChannelHeader ?? false,
            EditChannelHeaderClearance: ps?.EditChannelHeaderClearance ?? PermissionClearance.Unset,
            CanCreateDocumentSessions: ps?.CanCreateDocumentSessions ?? false,
            CreateDocumentSessionsClearance: ps?.CreateDocumentSessionsClearance ?? PermissionClearance.Unset,
            CanEnumerateWindows: ps?.CanEnumerateWindows ?? false,
            EnumerateWindowsClearance: ps?.EnumerateWindowsClearance ?? PermissionClearance.Unset,
            CanFocusWindow: ps?.CanFocusWindow ?? false,
            FocusWindowClearance: ps?.FocusWindowClearance ?? PermissionClearance.Unset,
            CanCloseWindow: ps?.CanCloseWindow ?? false,
            CloseWindowClearance: ps?.CloseWindowClearance ?? PermissionClearance.Unset,
            CanResizeWindow: ps?.CanResizeWindow ?? false,
            ResizeWindowClearance: ps?.ResizeWindowClearance ?? PermissionClearance.Unset,
            CanSendHotkey: ps?.CanSendHotkey ?? false,
            SendHotkeyClearance: ps?.SendHotkeyClearance ?? PermissionClearance.Unset,
            CanReadClipboard: ps?.CanReadClipboard ?? false,
            ReadClipboardClearance: ps?.ReadClipboardClearance ?? PermissionClearance.Unset,
            CanWriteClipboard: ps?.CanWriteClipboard ?? false,
            WriteClipboardClearance: ps?.WriteClipboardClearance ?? PermissionClearance.Unset,
            DangerousShellAccesses: MapResourceGrants(ps, ResourceTypes.DsShell),
            SafeShellAccesses: MapResourceGrants(ps, ResourceTypes.Mk8Shell),
            ContainerAccesses: MapResourceGrants(ps, ResourceTypes.Container),
            WebsiteAccesses: MapResourceGrants(ps, ResourceTypes.WaWebsite),
            SearchEngineAccesses: MapResourceGrants(ps, ResourceTypes.WaSearch),
            InternalDatabaseAccesses: MapResourceGrants(ps, ResourceTypes.DbInternal),
            ExternalDatabaseAccesses: MapResourceGrants(ps, ResourceTypes.DbExternal),
            InputAudioAccesses: MapResourceGrants(ps, ResourceTypes.TrAudio),
            DisplayDeviceAccesses: MapResourceGrants(ps, ResourceTypes.CuDisplay),
            EditorSessionAccesses: MapResourceGrants(ps, ResourceTypes.EditorSession),
            AgentAccesses: MapResourceGrants(ps, ResourceTypes.AoAgent),
            TaskAccesses: MapResourceGrants(ps, ResourceTypes.AoTask),
            SkillAccesses: MapResourceGrants(ps, ResourceTypes.AoSkill),
            AgentHeaderAccesses: MapResourceGrants(ps, ResourceTypes.AoAgentHeader),
            ChannelHeaderAccesses: MapResourceGrants(ps, ResourceTypes.AoChannelHeader),
            DocumentSessionAccesses: MapResourceGrants(ps, ResourceTypes.OaDocument),
            NativeApplicationAccesses: MapResourceGrants(ps, ResourceTypes.CuNativeApp));

    private static IReadOnlyList<ResourceGrant> MapResourceGrants(
        PermissionSetDB? ps, string resourceType)
    {
        if (ps?.ResourceAccesses is null or { Count: 0 })
            return [];

        return ps.ResourceAccesses
            .Where(a => a.ResourceType == resourceType)
            .Select(a => new ResourceGrant(a.ResourceId, a.Clearance))
            .ToList();
    }
}
