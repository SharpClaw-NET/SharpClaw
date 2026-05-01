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
    /// Per-module top-level enable flags, keyed by <c>moduleId</c>
    /// (case-insensitive). When a module is disabled here its
    /// <c>ConfigureGatewayServices</c> hook does not run and none of its
    /// groups are mapped, regardless of <see cref="Groups"/>.
    /// </summary>
    public Dictionary<string, bool> Modules { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-group enable flags, keyed by <c>{moduleId}/{groupId}</c>
    /// (case-insensitive). A missing entry is treated as disabled.
    /// </summary>
    public Dictionary<string, bool> Groups { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the module itself is explicitly enabled.</summary>
    public bool IsModuleEnabled(string moduleId)
        => Modules.TryGetValue(moduleId, out var enabled) && enabled;

    /// <summary>
    /// True when both the parent module and the specific group are explicitly
    /// enabled in configuration.
    /// </summary>
    public bool IsGroupEnabled(string moduleId, string groupId)
    {
        if (!IsModuleEnabled(moduleId)) return false;
        var key = $"{moduleId}/{groupId}";
        return Groups.TryGetValue(key, out var enabled) && enabled;
    }
}
