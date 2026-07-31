namespace SharpClaw.Shared.RemoteRuntimeBridge;

public static class RemoteRuntimeBridgePaths
{
    public const string AdministrationKeyHeader = "X-SharpClaw-Bridge-Admin-Key";
    public const string PairingClaim = "/__sharpclaw/remote-runtime/pairing/claim";
    public const string PairingCertificate = "/__sharpclaw/remote-runtime/pairing/certificate";
    public const string AdminInvitation = "/__sharpclaw/remote-runtime/admin/invitation";
    public const string AdminApprove = "/__sharpclaw/remote-runtime/admin/approve";
    public const string AdminRevoke = "/__sharpclaw/remote-runtime/admin/revoke";
}

public sealed record RemoteRuntimePairingClaimRequest(
    Guid PairId,
    string Secret,
    string ProxyRuntimeInstanceId,
    string CertificateSigningRequestBase64,
    string ProofSignatureBase64);

public sealed record RemoteRuntimePairingCertificateRequest(Guid PairId, string Secret);

public sealed record RemoteRuntimePairingAdminInvitationRequest(int LifetimeSeconds = 300);

public sealed record RemoteRuntimePairingAdminApprovalRequest(
    Guid PairId,
    string ProxyRuntimeInstanceId,
    string AuthoritativeRuntimeInstanceId);

public sealed record RemoteRuntimePairingAdminRevocationRequest(Guid PairId);

public sealed record RemoteRuntimePairingClaimResponse(
    Guid PairId,
    string Status,
    string GatewayInstanceId,
    string AuthoritativeRuntimeInstanceId,
    string ProxyRuntimeInstanceId);

public sealed record RemoteRuntimePairingCertificateResponse(
    string CertificateDerBase64,
    string ProxyRuntimePublicKeyHash,
    string CertificateThumbprint,
    DateTimeOffset NotAfterUtc);
