namespace SharpClaw.Gateway.Infrastructure;

/// <summary>
/// Request priority for the gateway queue. Higher priorities are processed
/// first. Clients set this via the <c>X-Priority</c> request header.
/// </summary>
public enum RequestPriority
{
    /// <summary>Processed before all other priorities.</summary>
    High = 0,

    /// <summary>Default priority.</summary>
    Normal = 1,

    /// <summary>Processed only when no higher-priority items are pending.</summary>
    Low = 2,
}
