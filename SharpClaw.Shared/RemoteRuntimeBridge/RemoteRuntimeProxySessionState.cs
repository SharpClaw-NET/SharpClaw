namespace SharpClaw.Shared.RemoteRuntimeBridge;

public sealed record RemoteRuntimeProxySessionState(
    Guid PairId,
    string GatewayBridgeUrl,
    string GatewayServerPublicKeyHash,
    string AuthoritativeRuntimeInstanceId,
    string ProxyRuntimeInstanceId,
    string ClientCertificatePfxBase64,
    DateTimeOffset CertificateNotAfterUtc,
    string? AuthoritativeRuntimeInstallFingerprint = null,
    string? CertificateThumbprint = null,
    int BridgeProtocolMajor = RemoteRuntimeBridgePaths.CurrentProtocolMajor);
