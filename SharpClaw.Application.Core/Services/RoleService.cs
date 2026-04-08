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
            ps.DangerousShellAccesses.Clear();
            ps.SafeShellAccesses.Clear();
            ps.ContainerAccesses.Clear();
            ps.WebsiteAccesses.Clear();
            ps.SearchEngineAccesses.Clear();
            ps.InternalDatabaseAccesses.Clear();
            ps.ExternalDatabaseAccesses.Clear();
            ps.InputAudioAccesses.Clear();
            ps.DisplayDeviceAccesses.Clear();
            ps.EditorSessionAccesses.Clear();
            ps.AgentPermissions.Clear();
            ps.TaskPermissions.Clear();
            ps.SkillPermissions.Clear();
            ps.AgentHeaderAccesses.Clear();
            ps.ChannelHeaderAccesses.Clear();
            ps.DocumentSessionAccesses.Clear();
            ps.NativeApplicationAccesses.Clear();
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
        AddGrants(ps.DangerousShellAccesses, request.DangerousShellAccesses,
            (g, psId) => new DangerousShellAccessDB
            { PermissionSetId = psId, SystemUserId = g.ResourceId, Clearance = g.Clearance });

        AddGrants(ps.SafeShellAccesses, request.SafeShellAccesses,
            (g, psId) => new SafeShellAccessDB
            { PermissionSetId = psId, ContainerId = g.ResourceId, Clearance = g.Clearance });

        AddGrants(ps.ContainerAccesses, request.ContainerAccesses,
            (g, psId) => new ContainerAccessDB
            { PermissionSetId = psId, ContainerId = g.ResourceId, Clearance = g.Clearance });

        AddGrants(ps.WebsiteAccesses, request.WebsiteAccesses,
            (g, psId) => new WebsiteAccessDB
            { PermissionSetId = psId, WebsiteId = g.ResourceId, Clearance = g.Clearance });

        AddGrants(ps.SearchEngineAccesses, request.SearchEngineAccesses,
            (g, psId) => new SearchEngineAccessDB
            { PermissionSetId = psId, SearchEngineId = g.ResourceId, Clearance = g.Clearance });

        AddGrants(ps.InternalDatabaseAccesses, request.InternalDatabaseAccesses,
            (g, psId) => new InternalDatabaseAccessDB
            { PermissionSetId = psId, InternalDatabaseId = g.ResourceId, Clearance = g.Clearance });

        AddGrants(ps.ExternalDatabaseAccesses, request.ExternalDatabaseAccesses,
            (g, psId) => new ExternalDatabaseAccessDB
            { PermissionSetId = psId, ExternalDatabaseId = g.ResourceId, Clearance = g.Clearance });

        AddGrants(ps.InputAudioAccesses, request.InputAudioAccesses,
            (g, psId) => new InputAudioAccessDB
            { PermissionSetId = psId, InputAudioId = g.ResourceId, Clearance = g.Clearance });

        AddGrants(ps.DisplayDeviceAccesses, request.DisplayDeviceAccesses,
            (g, psId) => new DisplayDeviceAccessDB
            { PermissionSetId = psId, DisplayDeviceId = g.ResourceId, Clearance = g.Clearance });

        AddGrants(ps.EditorSessionAccesses, request.EditorSessionAccesses,
            (g, psId) => new EditorSessionAccessDB
            { PermissionSetId = psId, EditorSessionId = g.ResourceId, Clearance = g.Clearance });

        AddGrants(ps.AgentPermissions, request.AgentAccesses,
            (g, psId) => new AgentManagementAccessDB
            { PermissionSetId = psId, AgentId = g.ResourceId, Clearance = g.Clearance });

        AddGrants(ps.TaskPermissions, request.TaskAccesses,
            (g, psId) => new TaskManageAccessDB
            { PermissionSetId = psId, ScheduledTaskId = g.ResourceId, Clearance = g.Clearance });

        AddGrants(ps.SkillPermissions, request.SkillAccesses,
            (g, psId) => new SkillManageAccessDB
            { PermissionSetId = psId, SkillId = g.ResourceId, Clearance = g.Clearance });

        AddGrants(ps.AgentHeaderAccesses, request.AgentHeaderAccesses,
            (g, psId) => new AgentHeaderAccessDB
            { PermissionSetId = psId, AgentId = g.ResourceId, Clearance = g.Clearance });

        AddGrants(ps.ChannelHeaderAccesses, request.ChannelHeaderAccesses,
            (g, psId) => new ChannelHeaderAccessDB
            { PermissionSetId = psId, ChannelId = g.ResourceId, Clearance = g.Clearance });

        AddGrants(ps.DocumentSessionAccesses, request.DocumentSessionAccesses,
            (g, psId) => new DocumentSessionAccessDB
            { PermissionSetId = psId, DocumentSessionId = g.ResourceId, Clearance = g.Clearance });

        AddGrants(ps.NativeApplicationAccesses, request.NativeApplicationAccesses,
            (g, psId) => new NativeApplicationAccessDB
            { PermissionSetId = psId, NativeApplicationId = g.ResourceId, Clearance = g.Clearance });

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
        ValidateCollection("DangerousShellAccesses", request.DangerousShellAccesses,
            callerPs?.DangerousShellAccesses, a => a.SystemUserId);
        ValidateCollection("SafeShellAccesses", request.SafeShellAccesses,
            callerPs?.SafeShellAccesses, a => a.ContainerId);
        ValidateCollection("ContainerAccesses", request.ContainerAccesses,
            callerPs?.ContainerAccesses, a => a.ContainerId);
        ValidateCollection("WebsiteAccesses", request.WebsiteAccesses,
            callerPs?.WebsiteAccesses, a => a.WebsiteId);
        ValidateCollection("SearchEngineAccesses", request.SearchEngineAccesses,
            callerPs?.SearchEngineAccesses, a => a.SearchEngineId);
        ValidateCollection("InternalDatabaseAccesses", request.InternalDatabaseAccesses,
            callerPs?.InternalDatabaseAccesses, a => a.InternalDatabaseId);
        ValidateCollection("ExternalDatabaseAccesses", request.ExternalDatabaseAccesses,
            callerPs?.ExternalDatabaseAccesses, a => a.ExternalDatabaseId);
        ValidateCollection("InputAudioAccesses", request.InputAudioAccesses,
            callerPs?.InputAudioAccesses, a => a.InputAudioId);
        ValidateCollection("DisplayDeviceAccesses", request.DisplayDeviceAccesses,
            callerPs?.DisplayDeviceAccesses, a => a.DisplayDeviceId);
        ValidateCollection("EditorSessionAccesses", request.EditorSessionAccesses,
            callerPs?.EditorSessionAccesses, a => a.EditorSessionId);
        ValidateCollection("AgentAccesses", request.AgentAccesses,
            callerPs?.AgentPermissions, a => a.AgentId);
        ValidateCollection("TaskAccesses", request.TaskAccesses,
            callerPs?.TaskPermissions, a => a.ScheduledTaskId);
        ValidateCollection("SkillAccesses", request.SkillAccesses,
            callerPs?.SkillPermissions, a => a.SkillId);
        ValidateCollection("AgentHeaderAccesses", request.AgentHeaderAccesses,
            callerPs?.AgentHeaderAccesses, a => a.AgentId);
        ValidateCollection("ChannelHeaderAccesses", request.ChannelHeaderAccesses,
            callerPs?.ChannelHeaderAccesses, a => a.ChannelId);
        ValidateCollection("DocumentSessionAccesses", request.DocumentSessionAccesses,
            callerPs?.DocumentSessionAccesses, a => a.DocumentSessionId);
        ValidateCollection("NativeApplicationAccesses", request.NativeApplicationAccesses,
            callerPs?.NativeApplicationAccesses, a => a.NativeApplicationId);
    }

    /// <summary>
    /// For each requested grant, the caller must hold either the exact
    /// resource ID or the <see cref="WellKnownIds.AllResources"/> wildcard.
    /// </summary>
    private static void ValidateCollection<TAccess>(
        string name,
        IReadOnlyList<ResourceGrant>? requested,
        ICollection<TAccess>? callerGrants,
        Func<TAccess, Guid> resourceSelector)
    {
        if (requested is null or { Count: 0 })
            return;

        if (callerGrants is null or { Count: 0 })
            throw new UnauthorizedAccessException(
                $"Cannot grant {name} — you hold no grants of this type.");

        var callerIds = new HashSet<Guid>(callerGrants.Select(resourceSelector));
        var hasWildcard = callerIds.Contains(WellKnownIds.AllResources);

        foreach (var grant in requested)
        {
            if (hasWildcard)
                continue;

            if (!callerIds.Contains(grant.ResourceId))
                throw new UnauthorizedAccessException(
                    $"Cannot grant {name} for resource {grant.ResourceId} " +
                    "— you do not hold this permission.");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private static void AddGrants<TAccess>(
        ICollection<TAccess> collection,
        IReadOnlyList<ResourceGrant>? grants,
        Func<ResourceGrant, Guid, TAccess> factory)
    {
        if (grants is null)
            return;

        foreach (var g in grants)
            collection.Add(factory(g, Guid.Empty)); // psId set by EF navigation
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
            .Include(p => p.DangerousShellAccesses)
            .Include(p => p.SafeShellAccesses)
            .Include(p => p.ContainerAccesses)
            .Include(p => p.WebsiteAccesses)
            .Include(p => p.SearchEngineAccesses)
            .Include(p => p.InternalDatabaseAccesses)
            .Include(p => p.ExternalDatabaseAccesses)
            .Include(p => p.InputAudioAccesses)
            .Include(p => p.DisplayDeviceAccesses)
            .Include(p => p.EditorSessionAccesses)
            .Include(p => p.AgentPermissions)
            .Include(p => p.TaskPermissions)
            .Include(p => p.SkillPermissions)
            .Include(p => p.AgentHeaderAccesses)
            .Include(p => p.ChannelHeaderAccesses)
            .Include(p => p.DocumentSessionAccesses)
            .Include(p => p.NativeApplicationAccesses)
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
            DangerousShellAccesses: MapGrants(ps?.DangerousShellAccesses, a => a.SystemUserId, a => a.Clearance),
            SafeShellAccesses: MapGrants(ps?.SafeShellAccesses, a => a.ContainerId, a => a.Clearance),
            ContainerAccesses: MapGrants(ps?.ContainerAccesses, a => a.ContainerId, a => a.Clearance),
            WebsiteAccesses: MapGrants(ps?.WebsiteAccesses, a => a.WebsiteId, a => a.Clearance),
            SearchEngineAccesses: MapGrants(ps?.SearchEngineAccesses, a => a.SearchEngineId, a => a.Clearance),
            InternalDatabaseAccesses: MapGrants(ps?.InternalDatabaseAccesses, a => a.InternalDatabaseId, a => a.Clearance),
            ExternalDatabaseAccesses: MapGrants(ps?.ExternalDatabaseAccesses, a => a.ExternalDatabaseId, a => a.Clearance),
            InputAudioAccesses: MapGrants(ps?.InputAudioAccesses, a => a.InputAudioId, a => a.Clearance),
            DisplayDeviceAccesses: MapGrants(ps?.DisplayDeviceAccesses, a => a.DisplayDeviceId, a => a.Clearance),
            EditorSessionAccesses: MapGrants(ps?.EditorSessionAccesses, a => a.EditorSessionId, a => a.Clearance),
            AgentAccesses: MapGrants(ps?.AgentPermissions, a => a.AgentId, a => a.Clearance),
            TaskAccesses: MapGrants(ps?.TaskPermissions, a => a.ScheduledTaskId, a => a.Clearance),
            SkillAccesses: MapGrants(ps?.SkillPermissions, a => a.SkillId, a => a.Clearance),
            AgentHeaderAccesses: MapGrants(ps?.AgentHeaderAccesses, a => a.AgentId, a => a.Clearance),
            ChannelHeaderAccesses: MapGrants(ps?.ChannelHeaderAccesses, a => a.ChannelId, a => a.Clearance),
            DocumentSessionAccesses: MapGrants(ps?.DocumentSessionAccesses, a => a.DocumentSessionId, a => a.Clearance),
            NativeApplicationAccesses: MapGrants(ps?.NativeApplicationAccesses, a => a.NativeApplicationId, a => a.Clearance));

    private static IReadOnlyList<ResourceGrant> MapGrants<T>(
        ICollection<T>? accesses,
        Func<T, Guid> resourceSelector,
        Func<T, PermissionClearance> clearanceSelector)
    {
        if (accesses is null or { Count: 0 })
            return [];

        return accesses
            .Select(a => new ResourceGrant(resourceSelector(a), clearanceSelector(a)))
            .ToList();
    }
}
