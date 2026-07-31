namespace SharpClaw.Shared.RemoteRuntimeBridge;

public static class RemoteRuntimeBridgePaths
{
    public const string AdministrationKeyHeader = "X-SharpClaw-Bridge-Admin-Key";
    public const string PairingClaim = "/__sharpclaw/remote-runtime/pairing/claim";
    public const string PairingCertificate = "/__sharpclaw/remote-runtime/pairing/certificate";
    public const string AdminInvitation = "/__sharpclaw/remote-runtime/admin/invitation";
    public const string AdminApprove = "/__sharpclaw/remote-runtime/admin/approve";
    public const string AdminRevoke = "/__sharpclaw/remote-runtime/admin/revoke";
    public const string CliControl = "/__sharpclaw/remote-runtime/cli";
    public const string RegistryPrefix = "/__sharpclaw/remote-runtime/registry";
    public const string RegistryInvitation = RegistryPrefix + "/invitation";
    public const string RegistryClaim = RegistryPrefix + "/claim";
    public const string RegistryCertificate = RegistryPrefix + "/certificate";
    public const string RegistryApprove = RegistryPrefix + "/approve";
    public const string RegistryReject = RegistryPrefix + "/reject";
    public const string RegistryRevoke = RegistryPrefix + "/revoke";
    public const string RegistryActive = RegistryPrefix + "/active";
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

public sealed record RemoteRuntimeRegistryInvitationRequest(
    string GatewayInstanceId,
    string GatewayServerPublicKeyHash,
    string AuthoritativeRuntimeInstanceId,
    string AuthoritativeRuntimeInstallFingerprint,
    int LifetimeSeconds = 300,
    string? DisplayName = null,
    string? Description = null,
    string? CertificateAuthorityPfxBase64 = null);

public sealed record RemoteRuntimeRegistryApprovalRequest(
    Guid PairId,
    string ProxyRuntimeInstanceId,
    string AuthoritativeRuntimeInstanceId,
    string? ClientCertificateIdentity = null);

public sealed record RemoteRuntimeRegistryRejectionRequest(Guid PairId, string Reason);

public sealed record RemoteRuntimeRegistryRevocationRequest(Guid PairId, string Reason);

public sealed record RemoteRuntimePairingRegistrySnapshot(
    Guid Id,
    Guid PairId,
    RemoteRuntimePairStatus Status,
    string GatewayInstanceId,
    string GatewayServerPublicKeyHash,
    string AuthoritativeRuntimeInstanceId,
    string AuthoritativeRuntimeInstallFingerprint,
    int BridgeProtocolMajor,
    string? ProxyRuntimeInstanceId,
    string? ProxyRuntimePublicKeyHash,
    string? ClientCertificateIdentity,
    string? DisplayName,
    string? Description,
    string? StatusReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ClaimedAtUtc,
    DateTimeOffset? ApprovedAtUtc,
    DateTimeOffset? RenewedAtUtc,
    DateTimeOffset? RevokedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long Revision)
{
    public bool IsActive(DateTimeOffset now)
        => Status == RemoteRuntimePairStatus.Active && ExpiresAtUtc > now;
}
