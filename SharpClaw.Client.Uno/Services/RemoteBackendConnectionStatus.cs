namespace SharpClaw.Services;

/// <summary>Safe connection metadata for the UI; credentials never enter this projection.</summary>
internal sealed record RemoteBackendConnectionStatus(
    bool IsConfigured,
    bool IsConnected,
    Uri? GatewayAddress,
    bool HasError);
