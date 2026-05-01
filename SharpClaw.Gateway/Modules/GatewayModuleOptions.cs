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

    /// <summary>
    /// Phase 5a flag. When <c>true</c>, the gateway subscribes to
    /// <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}"/>
    /// changes and reconciles loaded module hosts at runtime — flipping a
    /// module or group flag in <c>.env</c> enables/disables real endpoint
    /// mappings without a process restart. When <c>false</c> (the default),
    /// the manager loads modules at startup and never re-syncs; runtime
    /// flag flips only affect the gate's 503 behaviour.
    /// </summary>
    public bool HotReloadEnabled { get; set; }

    /// <summary>
    /// How long, in seconds, the manager waits for in-flight requests
    /// routed to a module's endpoints to complete before disposing the
    /// host. Default 30. Phase 5a uses this for the route-count drain;
    /// Phase 5b will use it for ALC unload as well.
    /// </summary>
    public int DrainTimeoutSeconds { get; set; } = 30;

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
