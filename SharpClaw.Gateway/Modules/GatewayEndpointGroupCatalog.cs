using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Contracts;

namespace SharpClaw.Gateway.Modules;

/// <summary>
/// Process-wide registry of module-contributed endpoint groups. Populated
/// during <c>MapGatewayModuleEndpoints</c> (Phase 3) and consulted by the
/// endpoint gate middleware and rate limiter to resolve a request path back
/// to its <c>{moduleId}/{groupId}</c> identity. Lookup is longest-prefix so
/// nested groups (e.g. <c>/api/modules/foo/bar/baz</c>) resolve to the most
/// specific registered route.
/// </summary>
public sealed class GatewayEndpointGroupCatalog(IOptionsMonitor<GatewayModuleOptions> options)
{
    private readonly ConcurrentDictionary<string, RegisteredGroup> _byPrefix
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Register a module endpoint group under its hard-coded prefix
    /// <c>/api/modules/{moduleId}/{group.GroupId}</c>. Returns <c>true</c>
    /// when the group was added; <c>false</c> when a different group already
    /// owned the prefix (the existing entry wins; the new one is dropped).
    /// </summary>
    public bool TryRegister(string moduleId, GatewayEndpointGroup group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentNullException.ThrowIfNull(group);

        var prefix = BuildPrefix(moduleId, group.GroupId);
        var entry = new RegisteredGroup(moduleId, group, prefix);
        return _byPrefix.TryAdd(prefix, entry);
    }

    /// <summary>
    /// Remove a previously registered group; used by the pipeline when a
    /// module's <c>MapEndpoints</c> throws so the gate yields 404 instead of
    /// 5xx for partially-mapped prefixes.
    /// </summary>
    public bool Unregister(string moduleId, string groupId)
    {
        var prefix = BuildPrefix(moduleId, groupId);
        return _byPrefix.TryRemove(prefix, out _);
    }

    /// <summary>All registered groups, in no particular order.</summary>
    public IReadOnlyCollection<RegisteredGroup> All => [.. _byPrefix.Values];

    /// <summary>
    /// Resolve a request path to its registered group via longest-prefix
    /// match. Returns <c>null</c> when no module owns the path.
    /// </summary>
    public RegisteredGroup? Resolve(string requestPath)
    {
        if (string.IsNullOrEmpty(requestPath))
            return null;

        RegisteredGroup? best = null;
        foreach (var (prefix, entry) in _byPrefix)
        {
            if (!requestPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (requestPath.Length != prefix.Length
                && requestPath[prefix.Length] != '/')
                continue;

            if (best is null || prefix.Length > best.Prefix.Length)
                best = entry;
        }

        return best;
    }

    /// <summary>
    /// Whether the supplied module + group combination is enabled in the
    /// current <see cref="GatewayModuleOptions"/> snapshot.
    /// </summary>
    public bool IsEnabled(string moduleId, string groupId)
        => options.CurrentValue.IsGroupEnabled(moduleId, groupId);

    private static string BuildPrefix(string moduleId, string groupId)
    {
        var trimmed = (groupId ?? string.Empty).Trim('/');
        return string.IsNullOrEmpty(trimmed)
            ? $"/api/modules/{moduleId}"
            : $"/api/modules/{moduleId}/{trimmed}";
    }

    /// <summary>One entry in the catalog: module + group + computed prefix.</summary>
    public sealed record RegisteredGroup(string ModuleId, GatewayEndpointGroup Group, string Prefix);
}
