using SharpClaw.Shared.RemoteRuntimeBridge;

namespace SharpClaw.Runtime.INF.Persistence;

public sealed class RemoteRuntimePairingDB
{
    public Guid Id { get; set; }
    public Guid PairId { get; set; }
    public RemoteRuntimePairStatus Status { get; set; }
    public required string GatewayInstanceId { get; set; }
    public required string GatewayServerPublicKeyHash { get; set; }
    public required string AuthoritativeRuntimeInstanceId { get; set; }
    public required string AuthoritativeRuntimeInstallFingerprint { get; set; }
    public required string InvitationHash { get; set; }
    public int BridgeProtocolMajor { get; set; }
    public string? ProxyRuntimeInstanceId { get; set; }
    public string? ProxyRuntimePublicKeyHash { get; set; }
    public string? ProxyRuntimeCertificateSigningRequest { get; set; }
    public string? ClientCertificateIdentity { get; set; }
    public string? EncryptedCertificateAuthorityPfx { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public string? StatusReason { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ClaimedAtUtc { get; set; }
    public DateTimeOffset? ApprovedAtUtc { get; set; }
    public DateTimeOffset? RenewedAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? LastSeenAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public long Revision { get; set; }
}

public sealed record RemoteRuntimePairingRegistryFilter(
    string? GatewayInstanceId = null,
    string? AuthoritativeRuntimeInstanceId = null,
    string? ProxyRuntimeInstanceId = null,
    RemoteRuntimePairStatus? Status = null,
    string? Search = null);

public sealed record RemoteRuntimePairingPageCursor(
    DateTimeOffset CreatedAtUtc,
    Guid Id);

public sealed record RemoteRuntimePairingRegistryPage(
    IReadOnlyList<RemoteRuntimePairingRegistryEntry> Items,
    bool HasMore,
    RemoteRuntimePairingPageCursor? Next);

public sealed record RemoteRuntimePairingRegistryEntry(
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
    public RemoteRuntimePairStatus GetEffectiveStatus(DateTimeOffset now)
        => Status is (RemoteRuntimePairStatus.InvitationIssued
            or RemoteRuntimePairStatus.ClaimPending
            or RemoteRuntimePairStatus.Active)
            && ExpiresAtUtc <= now
            ? RemoteRuntimePairStatus.Expired
            : Status;

    public bool IsActive(DateTimeOffset now) => GetEffectiveStatus(now) == RemoteRuntimePairStatus.Active;
}

public sealed record RemoteRuntimePairingClaim(
    Guid PairId,
    string InvitationSecret,
    string ProxyRuntimeInstanceId,
    string ProxyRuntimePublicKeyHash,
    string CertificateSigningRequestBase64);

public sealed class RemoteRuntimePairingRegistryException : InvalidOperationException
{
    public RemoteRuntimePairingRegistryException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
