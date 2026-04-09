using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Application.Infrastructure.Models.Context;
using SharpClaw.Contracts;
using SharpClaw.Contracts.Attributes;
using SharpClaw.Contracts.DTOs.Editor;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;
using SharpClaw.Infrastructure.Models;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

/// <summary>
/// Expands <c>{{tag}}</c> placeholders inside custom chat header templates.
/// <para>
/// <b>Context tags</b> — single-value lookups:<br/>
/// <c>{{time}}</c>, <c>{{user}}</c>, <c>{{via}}</c>, <c>{{role}}</c>,
/// <c>{{bio}}</c>, <c>{{agent-role}}</c>, <c>{{clearance}}</c>,
/// <c>{{grants}}</c>, <c>{{agent-grants}}</c>, <c>{{editor}}</c>,
/// <c>{{accessible-threads}}</c>.
/// </para>
/// <para>
/// <b>Resource tags</b> — enumerate entities from the database:<br/>
/// <c>{{Agents}}</c> → comma-separated GUIDs.<br/>
/// <c>{{Agents:{Name} ({Id})}}</c> → per-item formatted string.
/// </para>
/// <para>
/// Fields decorated with <see cref="HeaderSensitiveAttribute"/> are
/// blocked from template expansion (replaced with <c>[redacted]</c>).
/// </para>
/// </summary>
public sealed partial class HeaderTagProcessor(SharpClawDbContext db)
{
    // ── Tag regex ────────────────────────────────────────────────
    // Matches {{TagName}} or {{TagName:{template with {Field} placeholders}}}
    [GeneratedRegex(@"\{\{(?<name>[A-Za-z][A-Za-z0-9\-_]*?)(?::(?<tpl>\{[^}]+\}(?:[^{}]*\{[^}]+\})*[^{}]*))?\}\}",
        RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    // Matches {FieldName} inside a per-item template
    [GeneratedRegex(@"\{(?<field>[A-Za-z][A-Za-z0-9_]*)\}",
        RegexOptions.CultureInvariant)]
    private static partial Regex FieldPattern();

    // ── Sensitive-field cache (thread-safe, lives for app lifetime) ─
    private static readonly ConcurrentDictionary<Type, HashSet<string>> SensitiveFieldCache = new();

    // ── Resource type → human label (used by CollectGrantNames) ───
    private static readonly (string ResourceType, string Label)[] ResourceGrantLabels =
    [
        (ResourceTypes.DsShell,         "DangerousShell"),
        (ResourceTypes.Mk8Shell,        "SafeShell"),
        (ResourceTypes.Container,       "ContainerAccess"),
        (ResourceTypes.WaWebsite,       "WebsiteAccess"),
        (ResourceTypes.WaSearch,        "SearchEngineAccess"),
        (ResourceTypes.DbInternal,      "InternalDatabase"),
        (ResourceTypes.DbExternal,      "ExternalDatabase"),
        (ResourceTypes.TrAudio,         "InputAudio"),
        (ResourceTypes.CuDisplay,       "DisplayDevice"),
        (ResourceTypes.EditorSession,   "EditorSession"),
        (ResourceTypes.AoAgent,         "ManageAgent"),
        (ResourceTypes.AoTask,          "EditTask"),
        (ResourceTypes.AoSkill,         "AccessSkill"),
        (ResourceTypes.AoAgentHeader,   "EditAgentHeader[res]"),
        (ResourceTypes.AoChannelHeader, "EditChannelHeader[res]"),
        (ResourceTypes.BiChannel,       "BotIntegration"),
        (ResourceTypes.OaDocument,      "DocumentSession"),
        (ResourceTypes.CuNativeApp,     "NativeApplication"),
    ];

    // ── Resource type → (label, wildcard ID loader) ──────────────
    private static readonly (string ResourceType, string Label,
        Func<SharpClawDbContext, CancellationToken, Task<List<Guid>>> LoadAllIds)[] ResourceGrantExpanders =
    [
        (ResourceTypes.DsShell,         "DangerousShell",        (d, ct) => d.SystemUsers.Select(s => s.Id).ToListAsync(ct)),
        (ResourceTypes.Mk8Shell,        "SafeShell",             (d, ct) => d.Containers.Select(c => c.Id).ToListAsync(ct)),
        (ResourceTypes.Container,       "ContainerAccess",       (d, ct) => d.Containers.Select(c => c.Id).ToListAsync(ct)),
        (ResourceTypes.WaWebsite,       "WebsiteAccess",         (d, ct) => d.Websites.Select(w => w.Id).ToListAsync(ct)),
        (ResourceTypes.WaSearch,        "SearchEngineAccess",    (d, ct) => d.SearchEngines.Select(s => s.Id).ToListAsync(ct)),
        (ResourceTypes.DbInternal,      "InternalDatabase",      (d, ct) => d.InternalDatabases.Select(l => l.Id).ToListAsync(ct)),
        (ResourceTypes.DbExternal,      "ExternalDatabase",      (d, ct) => d.ExternalDatabases.Select(e => e.Id).ToListAsync(ct)),
        (ResourceTypes.TrAudio,         "InputAudio",            (d, ct) => d.InputAudios.Select(a => a.Id).ToListAsync(ct)),
        (ResourceTypes.CuDisplay,       "DisplayDevice",         (d, ct) => d.DisplayDevices.Select(d2 => d2.Id).ToListAsync(ct)),
        (ResourceTypes.EditorSession,   "EditorSession",         (d, ct) => d.EditorSessions.Select(e => e.Id).ToListAsync(ct)),
        (ResourceTypes.AoAgent,         "ManageAgent",           (d, ct) => d.Agents.Select(a => a.Id).ToListAsync(ct)),
        (ResourceTypes.AoTask,          "EditTask",              (d, ct) => d.ScheduledTasks.Select(t => t.Id).ToListAsync(ct)),
        (ResourceTypes.AoSkill,         "AccessSkill",           (d, ct) => d.Skills.Select(s => s.Id).ToListAsync(ct)),
        (ResourceTypes.AoAgentHeader,   "EditAgentHeader",       (d, ct) => d.Agents.Select(a => a.Id).ToListAsync(ct)),
        (ResourceTypes.AoChannelHeader, "EditChannelHeader",     (d, ct) => d.Channels.Select(c => c.Id).ToListAsync(ct)),
        (ResourceTypes.BiChannel,       "BotIntegration",        (d, ct) => d.BotIntegrations.Select(b => b.Id).ToListAsync(ct)),
        (ResourceTypes.OaDocument,      "DocumentSession",       (d, ct) => d.DocumentSessions.Select(d2 => d2.Id).ToListAsync(ct)),
        (ResourceTypes.CuNativeApp,     "NativeApplication",     (d, ct) => d.NativeApplications.Select(n => n.Id).ToListAsync(ct)),
    ];

    /// <summary>
    /// Expands all <c>{{tag}}</c> placeholders in <paramref name="template"/>
    /// and returns the fully resolved header string.
    /// </summary>
    public async Task<string> ExpandAsync(
        string template,
        ChannelDB channel,
        AgentDB agent,
        ChatClientType clientType,
        EditorContext? editorContext,
        Guid? userId,
        CancellationToken ct)
    {
        var matches = TagPattern().Matches(template);
        if (matches.Count == 0)
            return template;

        var context = await BuildContextAsync(channel, agent, clientType, editorContext, userId, ct);

        var sb = new StringBuilder(template.Length * 2);
        var lastIdx = 0;

        foreach (Match match in matches)
        {
            sb.Append(template, lastIdx, match.Index - lastIdx);
            lastIdx = match.Index + match.Length;

            var tagName = match.Groups["name"].Value;
            var itemTemplate = match.Groups["tpl"].Success ? match.Groups["tpl"].Value : null;

            var expanded = await ExpandTagAsync(tagName, itemTemplate, context, ct);
            sb.Append(expanded);
        }

        sb.Append(template, lastIdx, template.Length - lastIdx);
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    // Context resolution
    // ═══════════════════════════════════════════════════════════════

    private async Task<HeaderContext> BuildContextAsync(
        ChannelDB channel, AgentDB agent, ChatClientType clientType,
        EditorContext? editorContext, Guid? userId, CancellationToken ct)
    {
        UserDB? user = null;
        PermissionSetDB? userPs = null;
        if (userId is not null)
        {
            user = await db.Users
                .Include(u => u.Role).ThenInclude(r => r!.PermissionSet)
                .FirstOrDefaultAsync(u => u.Id == userId, ct);

            if (user?.Role?.PermissionSetId is { } psId)
            {
                userPs = await db.PermissionSets
                        .Include(p => p.ResourceAccesses)
                        .AsSplitQuery()
                        .FirstOrDefaultAsync(p => p.Id == psId, ct);
            }
        }

        var agentWithRole = await db.Agents
            .Include(a => a.Role).ThenInclude(r => r!.PermissionSet)
            .FirstOrDefaultAsync(a => a.Id == agent.Id, ct);

        PermissionSetDB? agentPs = null;
        if (agentWithRole?.Role?.PermissionSetId is { } agentPsId)
        {
            agentPs = await db.PermissionSets
                .Include(p => p.ResourceAccesses)
                .AsSplitQuery()
                .FirstOrDefaultAsync(p => p.Id == agentPsId, ct);
        }

        return new HeaderContext(
            channel, agent, clientType, editorContext,
            user, userPs, agentWithRole?.Role, agentPs);
    }

    // ═══════════════════════════════════════════════════════════════
    // Tag expansion dispatch
    // ═══════════════════════════════════════════════════════════════

    private async Task<string> ExpandTagAsync(
        string tagName, string? itemTemplate, HeaderContext ctx, CancellationToken ct)
    {
        // ── Context tags (case-insensitive) ──────────────────────
        var expanded = tagName.ToLowerInvariant() switch
        {
            "time" => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"),
            "user" => ctx.User?.Username ?? "(unknown)",
            "via" => ctx.ClientType.ToString(),
            "role" => FormatUserRole(ctx),
            "bio" => ctx.User?.Bio ?? "",
            "agent-name" => ctx.Agent.Name,
            "agent-role" => await FormatAgentRoleAsync(ctx, ct),
            // clearance is intentionally omitted; see FormatAgentRoleAsync comment.
            "clearance" => "(per-action; see grants)",
            "grants" => FormatGrants(ctx.UserPs),
            "agent-grants" => await FormatAgentGrantsAsync(ctx, ct),
            "editor" => FormatEditor(ctx.EditorContext),
            "accessible-threads" => await FormatAccessibleThreadsAsync(ctx, ct),
            _ => await TryExpandResourceTagAsync(tagName, itemTemplate, ct)
        };

        return expanded;
    }

    // ═══════════════════════════════════════════════════════════════
    // Context tag formatters
    // ═══════════════════════════════════════════════════════════════

    private static string FormatUserRole(HeaderContext ctx)
    {
        if (ctx.User?.Role is null || ctx.UserPs is null)
            return "(none)";

        var grants = CollectGrantNames(ctx.UserPs);
        return grants.Count > 0
            ? $"{ctx.User.Role.Name} ({string.Join(", ", grants)})"
            : ctx.User.Role.Name;
    }

    // NOTE: DefaultClearance is intentionally NOT included in the header.
    // It is an internal fallback sentinel that agents misinterpret as "no
    // clearance" or "disabled." Effective clearance is resolved per-action
    // at runtime (AgentActionService.ResolveClearance). The grants list
    // already tells the agent what it can do. Do not re-add it.
    private async Task<string> FormatAgentRoleAsync(HeaderContext ctx, CancellationToken ct)
    {
        if (ctx.AgentRole is null)
            return "(none)";

        var sb = new StringBuilder();
        sb.Append(ctx.AgentRole.Name);
        if (ctx.AgentPs is not null)
        {
            var grants = await CollectGrantNamesWithResourcesAsync(ctx.AgentPs, ct);
            if (grants.Count > 0)
                sb.Append(" (").Append(string.Join(", ", grants)).Append(')');
        }
        return sb.ToString();
    }

    private async Task<string> FormatAgentGrantsAsync(HeaderContext ctx, CancellationToken ct)
    {
        if (ctx.AgentPs is null) return "(none)";
        var grants = await CollectGrantNamesWithResourcesAsync(ctx.AgentPs, ct);
        return grants.Count > 0 ? string.Join(", ", grants) : "(none)";
    }

    private static string FormatGrants(PermissionSetDB? ps)
    {
        if (ps is null) return "(none)";
        var grants = CollectGrantNames(ps);
        return grants.Count > 0 ? string.Join(", ", grants) : "(none)";
    }

    private static List<string> CollectGrantNames(PermissionSetDB ps)
    {
        var grants = new List<string>();
        if (ps.CanCreateSubAgents) grants.Add("CreateSubAgents");
        if (ps.CanCreateContainers) grants.Add("CreateContainers");
        if (ps.CanRegisterDatabases) grants.Add("RegisterDatabases");
        if (ps.CanAccessLocalhostInBrowser) grants.Add("LocalhostBrowser");
        if (ps.CanAccessLocalhostCli) grants.Add("LocalhostCli");
        if (ps.CanClickDesktop) grants.Add("ClickDesktop");
        if (ps.CanTypeOnDesktop) grants.Add("TypeOnDesktop");
        if (ps.CanReadCrossThreadHistory) grants.Add("ReadCrossThreadHistory");
        if (ps.CanEditAgentHeader) grants.Add("EditAgentHeader");
        if (ps.CanEditChannelHeader) grants.Add("EditChannelHeader");
        if (ps.CanCreateDocumentSessions) grants.Add("CreateDocumentSessions");
        if (ps.CanEnumerateWindows) grants.Add("EnumerateWindows");
        if (ps.CanFocusWindow) grants.Add("FocusWindow");
        if (ps.CanCloseWindow) grants.Add("CloseWindow");
        if (ps.CanResizeWindow) grants.Add("ResizeWindow");
        if (ps.CanSendHotkey) grants.Add("SendHotkey");
        if (ps.CanReadClipboard) grants.Add("ReadClipboard");
        if (ps.CanWriteClipboard) grants.Add("WriteClipboard");

        foreach (var (rt, label) in ResourceGrantLabels)
        {
            if (ps.ResourceAccesses.Any(a => a.ResourceType == rt))
                grants.Add(label);
        }

        return grants;
    }

    /// <summary>
    /// Collects grant names with enumerated resource IDs for agent
    /// self-awareness — mirrors <c>ChatService.CollectGrantsWithResourcesAsync</c>.
    /// When a wildcard grant is present, all resource IDs of that type
    /// are resolved from the database.
    /// </summary>
    private async Task<List<string>> CollectGrantNamesWithResourcesAsync(
        PermissionSetDB ps, CancellationToken ct)
    {
        var grants = new List<string>();
        if (ps.CanCreateSubAgents) grants.Add("CreateSubAgents");
        if (ps.CanCreateContainers) grants.Add("CreateContainers");
        if (ps.CanRegisterDatabases) grants.Add("RegisterDatabases");
        if (ps.CanAccessLocalhostInBrowser) grants.Add("LocalhostBrowser");
        if (ps.CanAccessLocalhostCli) grants.Add("LocalhostCli");
        if (ps.CanClickDesktop) grants.Add("ClickDesktop");
        if (ps.CanTypeOnDesktop) grants.Add("TypeOnDesktop");
        if (ps.CanReadCrossThreadHistory) grants.Add("ReadCrossThreadHistory");
        if (ps.CanEditAgentHeader) grants.Add("EditAgentHeader");
        if (ps.CanEditChannelHeader) grants.Add("EditChannelHeader");
        if (ps.CanCreateDocumentSessions) grants.Add("CreateDocumentSessions");
        if (ps.CanEnumerateWindows) grants.Add("EnumerateWindows");
        if (ps.CanFocusWindow) grants.Add("FocusWindow");
        if (ps.CanCloseWindow) grants.Add("CloseWindow");
        if (ps.CanResizeWindow) grants.Add("ResizeWindow");
        if (ps.CanSendHotkey) grants.Add("SendHotkey");
        if (ps.CanReadClipboard) grants.Add("ReadClipboard");
        if (ps.CanWriteClipboard) grants.Add("WriteClipboard");

        foreach (var (rt, label, loadAllIds) in ResourceGrantExpanders)
        {
            var grantedIds = ps.ResourceAccesses
                .Where(a => a.ResourceType == rt)
                .Select(a => a.ResourceId)
                .ToList();

            await AppendResourceGrantAsync(grants, label, grantedIds, () => loadAllIds(db, ct));
        }

        return grants;
    }

    private static async Task AppendResourceGrantAsync(
        List<string> grants, string grantName,
        IEnumerable<Guid> grantedIds, Func<Task<List<Guid>>> loadAllIdsAsync)
    {
        var ids = grantedIds.ToList();
        if (ids.Count == 0)
            return;

        List<Guid> resolved;
        if (ids.Any(id => id == WellKnownIds.AllResources))
            resolved = await loadAllIdsAsync();
        else
            resolved = ids;

        if (resolved.Count == 0)
        {
            grants.Add(grantName);
            return;
        }

        var idList = string.Join(",", resolved.Select(id => id.ToString("D")));
        grants.Add($"{grantName}[{idList}]");
    }

    private static string FormatEditor(EditorContext? ec)
    {
        if (ec is null) return "(none)";
        var sb = new StringBuilder();
        sb.Append(ec.EditorType);
        if (ec.EditorVersion is not null) sb.Append(' ').Append(ec.EditorVersion);
        if (ec.WorkspacePath is not null) sb.Append(" workspace=").Append(ec.WorkspacePath);
        if (ec.ActiveFilePath is not null) sb.Append(" file=").Append(ec.ActiveFilePath);
        if (ec.ActiveFileLanguage is not null) sb.Append(" lang=").Append(ec.ActiveFileLanguage);
        if (ec.SelectionStartLine is not null)
            sb.Append(" sel=").Append(ec.SelectionStartLine).Append('-').Append(ec.SelectionEndLine);
        if (ec.SelectedText is { Length: > 0 and <= 200 })
            sb.Append(" selection=\"").Append(ec.SelectedText).Append('"');
        return sb.ToString();
    }

    private async Task<string> FormatAccessibleThreadsAsync(HeaderContext ctx, CancellationToken ct)
    {
        // Reuse the same logic as ChatService — find threads from channels
        // where the agent is primary or allowed, that opt-in with ReadCrossThreadHistory.
        if (ctx.AgentPs is null || !ctx.AgentPs.CanReadCrossThreadHistory)
            return "(none)";

        var agentId = ctx.Agent.Id;
        var currentChannelId = ctx.Channel.Id;

        var accessibleChannels = await db.Channels
            .Include(c => c.Threads)
            .Where(c => c.Id != currentChannelId
                && (c.AgentId == agentId || c.AllowedAgents.Any(a => a.Id == agentId)))
            .ToListAsync(ct);

        var entries = new List<string>();
        foreach (var ch in accessibleChannels)
        {
            foreach (var thread in ch.Threads)
                entries.Add($"{thread.Name} [{ch.Title}] ({thread.Id:D})");
        }

        return entries.Count > 0 ? string.Join(", ", entries) : "(none)";
    }

    // ═══════════════════════════════════════════════════════════════
    // Resource tag expansion
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Maps tag names (case-insensitive) to DbSet query functions.
    /// Returns entities as <see cref="BaseEntity"/> so we can reflect
    /// their properties for template expansion.
    /// </summary>
    private async Task<string> TryExpandResourceTagAsync(
        string tagName, string? itemTemplate, CancellationToken ct)
    {
        var entities = await LoadEntitiesAsync(tagName, ct);
        if (entities is null)
            return $"{{{{unknown:{tagName}}}}}";

        if (entities.Count == 0)
            return "(none)";

        // No template: just emit comma-separated GUIDs
        if (itemTemplate is null)
            return string.Join(", ", entities.Select(e => e.Id.ToString("D")));

        // With template: expand {FieldName} placeholders per entity
        var entityType = entities[0].GetType();
        var sensitiveFields = GetSensitiveFields(entityType);

        var items = new List<string>(entities.Count);
        foreach (var entity in entities)
        {
            var formatted = FieldPattern().Replace(itemTemplate, m =>
            {
                var fieldName = m.Groups["field"].Value;

                if (sensitiveFields.Contains(fieldName))
                    return "[redacted]";

                var prop = entityType.GetProperty(fieldName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop is null)
                    return $"[{fieldName}?]";

                var value = prop.GetValue(entity);
                return value?.ToString() ?? "";
            });
            items.Add(formatted);
        }

        return string.Join(", ", items);
    }

    /// <summary>
    /// Loads all entities for a given resource tag name.
    /// Returns <see langword="null"/> if the tag name is not recognised.
    /// </summary>
    private async Task<List<BaseEntity>?> LoadEntitiesAsync(string tagName, CancellationToken ct)
    {
        // Normalise to lowercase for matching
        return tagName.ToLowerInvariant() switch
        {
            "agents" => Cast(await db.Agents.ToListAsync(ct)),
            "models" => Cast(await db.Models.Include(m => m.Provider).ToListAsync(ct)),
            "providers" => Cast(await db.Providers.ToListAsync(ct)),
            "channels" => Cast(await db.Channels.ToListAsync(ct)),
            "threads" => Cast(await db.ChatThreads.ToListAsync(ct)),
            "roles" => Cast(await db.Roles.ToListAsync(ct)),
            "users" => Cast(await db.Users.ToListAsync(ct)),
            "containers" => Cast(await db.Containers.ToListAsync(ct)),
            "websites" => Cast(await db.Websites.ToListAsync(ct)),
            "searchengines" => Cast(await db.SearchEngines.ToListAsync(ct)),
            "inputaudios" => Cast(await db.InputAudios.ToListAsync(ct)),
            "displaydevices" => Cast(await db.DisplayDevices.ToListAsync(ct)),
            "editorsessions" => Cast(await db.EditorSessions.ToListAsync(ct)),
            "skills" => Cast(await db.Skills.ToListAsync(ct)),
            "systemusers" => Cast(await db.SystemUsers.ToListAsync(ct)),
            "internaldatabases" => Cast(await db.InternalDatabases.ToListAsync(ct)),
            "externaldatabases" => Cast(await db.ExternalDatabases.ToListAsync(ct)),
            "scheduledtasks" or "scheduledjobs" => Cast(await db.ScheduledTasks.ToListAsync(ct)),
            "tasks" or "taskdefinitions" => Cast(await db.TaskDefinitions.ToListAsync(ct)),
            "botintegrations" => Cast(await db.BotIntegrations.ToListAsync(ct)),
            _ => null
        };

        static List<BaseEntity> Cast<T>(List<T> items) where T : BaseEntity
            => items.ConvertAll(static x => (BaseEntity)x);
    }

    // ═══════════════════════════════════════════════════════════════
    // Sensitive-field enforcement
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the set of property names on <paramref name="type"/> that
    /// are decorated with <see cref="HeaderSensitiveAttribute"/>.
    /// Results are cached per type for the lifetime of the process.
    /// </summary>
    private static HashSet<string> GetSensitiveFields(Type type)
    {
        return SensitiveFieldCache.GetOrAdd(type, static t =>
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetCustomAttribute<HeaderSensitiveAttribute>() is not null)
                    set.Add(prop.Name);
            }
            return set;
        });
    }

    // ── Internal context holder ──────────────────────────────────
    private sealed record HeaderContext(
        ChannelDB Channel,
        AgentDB Agent,
        ChatClientType ClientType,
        EditorContext? EditorContext,
        UserDB? User,
        PermissionSetDB? UserPs,
        RoleDB? AgentRole,
        PermissionSetDB? AgentPs);
}
