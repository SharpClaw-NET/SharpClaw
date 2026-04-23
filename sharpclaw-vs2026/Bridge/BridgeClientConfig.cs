using System;

namespace SharpClaw.VS2026Extension;

/// <summary>
/// Immutable snapshot of the configuration values the bridge client
/// needs at connect time. Built from <see cref="SharpClawOptionsPage"/>
/// so the client never touches the <c>DialogPage</c> directly.
/// </summary>
internal sealed class BridgeClientConfig
{
    public BridgeClientConfig(
        Uri bridgeUri,
        string apiKeyFilePath,
        string backendInstanceId,
        int connectionTimeoutSeconds)
    {
        BridgeUri = bridgeUri;
        ApiKeyFilePath = apiKeyFilePath;
        BackendInstanceId = backendInstanceId;
        ConnectionTimeoutSeconds = Math.Max(1, connectionTimeoutSeconds);
    }

    /// <summary>WebSocket endpoint, e.g. <c>ws://127.0.0.1:48923/editor/ws</c>.</summary>
    public Uri BridgeUri { get; }

    /// <summary>Resolved path to the API key file.</summary>
    public string ApiKeyFilePath { get; }

    /// <summary>Explicit backend instance id to attach to when provided.</summary>
    public string BackendInstanceId { get; }

    /// <summary>Max seconds to wait for the connection handshake.</summary>
    public int ConnectionTimeoutSeconds { get; }
}
