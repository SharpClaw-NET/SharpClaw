namespace SharpClaw.Gateway.Modules;

/// <summary>
/// Per-module / per-group enable toggles for gateway-side module extensions.
/// Loaded from the <c>Gateway:Modules</c> configuration section. Defaults to
/// empty: every module-contributed endpoint group is disabled until the
/// operator explicitly opts in.
/// </summary>
public sealed class GatewayModuleOptions
{
    public const string SectionName = "Gateway:Modules";

    /// <summary>
    /// Map of <c>{moduleId}/{groupId}</c> keys to enable flags. The catalog
    /// composes the lookup key; case-insensitive on both segments.
    /// </summary>
    public Dictionary<string, bool> Groups { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when the supplied module + group combination is explicitly
    /// enabled in configuration. Missing entries are treated as disabled.
    /// </summary>
    public bool IsGroupEnabled(string moduleId, string groupId)
    {
        var key = $"{moduleId}/{groupId}";
        return Groups.TryGetValue(key, out var enabled) && enabled;
    }
}
