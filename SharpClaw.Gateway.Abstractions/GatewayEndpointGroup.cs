namespace SharpClaw.Gateway.Abstractions;

/// <summary>
/// Metadata that describes a single endpoint group contributed by a module
/// to the gateway. A group owns one path prefix beneath the module
/// (<c>/api/modules/{ModuleId}/{slug}</c>), one rate-limit policy, and one
/// operator-visible enable/disable toggle.
/// </summary>
/// <param name="GroupId">
/// Stable identifier (for example <c>"webaccess.searchengines"</c>) used both
/// for configuration keys and for the catalog look-up.
/// </param>
/// <param name="DisplayName">Human-readable name surfaced in OpenAPI metadata.</param>
/// <param name="Description">Optional longer description for documentation.</param>
/// <param name="RateLimitPolicy">
/// Optional rate-limit policy name resolved against the gateway's
/// <c>RateLimiterConfiguration</c>. <c>null</c> means the global policy.
/// </param>
/// <param name="DefaultEnabled">
/// Whether the group should be documented as <c>true</c> in the bundled
/// <c>.env.template</c>. Runtime activation always requires an explicit
/// configuration entry; this field controls documentation defaults only.
/// </param>
public sealed record GatewayEndpointGroup(
    string GroupId,
    string DisplayName,
    string? Description = null,
    string? RateLimitPolicy = null,
    bool DefaultEnabled = false);
