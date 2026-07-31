using System.Globalization;

namespace SharpClaw.Shared.RemoteRuntimeBridge;

public enum RemoteRuntimePairStatus
{
    InvitationIssued,
    ClaimPending,
    Active,
    Revoked,
    Expired,
}

public sealed record RemoteRuntimePairingInvitation(
    Guid PairId,
    string Secret,
    string GatewayInstanceId,
    string GatewayServerPublicKeyHash,
    string AuthoritativeRuntimeInstanceId,
    string AuthoritativeRuntimeInstallFingerprint,
    int BridgeProtocolMajor,
    DateTimeOffset ExpiresAtUtc);

public sealed record RemoteRuntimePairingRecord(
    Guid PairId,
    RemoteRuntimePairStatus Status,
    string GatewayInstanceId,
    string GatewayServerPublicKeyHash,
    string AuthoritativeRuntimeInstanceId,
    string AuthoritativeRuntimeInstallFingerprint,
    string InvitationHash,
    int BridgeProtocolMajor,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string? ProxyRuntimeInstanceId,
    string? ProxyRuntimePublicKeyHash,
    string? ProxyRuntimeCertificateSigningRequest,
    DateTimeOffset? ClaimedAtUtc,
    DateTimeOffset? ApprovedAtUtc,
    DateTimeOffset? RevokedAtUtc)
{
    public RemoteRuntimePairStatus GetEffectiveStatus(DateTimeOffset now)
        => Status is (RemoteRuntimePairStatus.InvitationIssued
            or RemoteRuntimePairStatus.ClaimPending
            or RemoteRuntimePairStatus.Active)
            && ExpiresAtUtc <= now
            ? RemoteRuntimePairStatus.Expired
            : Status;

    public bool IsActive(DateTimeOffset now)
        => GetEffectiveStatus(now) == RemoteRuntimePairStatus.Active;

    public static string FormatTimestamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

public sealed class RemoteRuntimePairingException : InvalidOperationException
{
    public RemoteRuntimePairingException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
