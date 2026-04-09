namespace SharpClaw.Contracts.Modules;

/// <summary>
/// Result of a module health check. Modules return this from
/// <see cref="ISharpClawModule.HealthCheckAsync"/>.
/// </summary>
public sealed record ModuleHealthStatus(
    /// <summary>Whether the module considers itself healthy.</summary>
    bool IsHealthy,

    /// <summary>
    /// Optional diagnostic message. Always logged; shown in admin UI
    /// and CLI. Should not contain secrets or PII.
    /// </summary>
    string? Message = null,

    /// <summary>
    /// Optional structured diagnostics (e.g. connection pool size,
    /// queue depth, cache hit rate). Serialized to JSON for the
    /// <c>/modules/{id}/health</c> endpoint.
    /// </summary>
    IReadOnlyDictionary<string, object>? Details = null
);
