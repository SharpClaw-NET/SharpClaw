using System.Globalization;
using System.Text;

namespace SharpClaw.Shared.RemoteRuntimeBridge;

public static class RemoteRuntimePairingProof
{
    public static byte[] CreateClaimProofPayload(
        RemoteRuntimePairingInvitation invitation,
        string proxyRuntimeInstanceId,
        string proxyRuntimePublicKeyHash)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        return CreateClaimProofPayload(
            invitation.PairId,
            invitation.GatewayInstanceId,
            invitation.AuthoritativeRuntimeInstanceId,
            proxyRuntimeInstanceId,
            proxyRuntimePublicKeyHash,
            invitation.Secret,
            invitation.BridgeProtocolMajor);
    }

    public static byte[] CreateClaimProofPayload(
        Guid pairId,
        string gatewayInstanceId,
        string authoritativeRuntimeInstanceId,
        string proxyRuntimeInstanceId,
        string proxyRuntimePublicKeyHash,
        string invitationSecret,
        int bridgeProtocolMajor = RemoteRuntimeBridgePaths.CurrentProtocolMajor)
    {
        RequireText(gatewayInstanceId, nameof(gatewayInstanceId));
        RequireText(authoritativeRuntimeInstanceId, nameof(authoritativeRuntimeInstanceId));
        RequireText(proxyRuntimeInstanceId, nameof(proxyRuntimeInstanceId));
        RequireText(proxyRuntimePublicKeyHash, nameof(proxyRuntimePublicKeyHash));
        RequireText(invitationSecret, nameof(invitationSecret));
        if (bridgeProtocolMajor != RemoteRuntimeBridgePaths.CurrentProtocolMajor)
            throw new ArgumentOutOfRangeException(nameof(bridgeProtocolMajor));
        return Encoding.UTF8.GetBytes(
            string.Join(
                '|',
                pairId.ToString("D", CultureInfo.InvariantCulture),
                gatewayInstanceId,
                authoritativeRuntimeInstanceId,
                proxyRuntimeInstanceId,
                proxyRuntimePublicKeyHash,
                bridgeProtocolMajor.ToString(CultureInfo.InvariantCulture),
                invitationSecret));
    }

    public static byte[] CreateCertificateProofPayload(
        Guid pairId,
        string proxyRuntimePublicKeyHash,
        int bridgeProtocolMajor = RemoteRuntimeBridgePaths.CurrentProtocolMajor)
    {
        RequireText(proxyRuntimePublicKeyHash, nameof(proxyRuntimePublicKeyHash));
        RequireProtocolMajor(bridgeProtocolMajor);
        return Encoding.UTF8.GetBytes(
            string.Join(
                '|',
                "certificate",
                pairId.ToString("D", CultureInfo.InvariantCulture),
                proxyRuntimePublicKeyHash,
                bridgeProtocolMajor.ToString(CultureInfo.InvariantCulture)));
    }

    public static byte[] CreateRenewalProofPayload(
        Guid pairId,
        string proxyRuntimePublicKeyHash,
        DateTimeOffset expiresAtUtc,
        int bridgeProtocolMajor = RemoteRuntimeBridgePaths.CurrentProtocolMajor)
    {
        RequireText(proxyRuntimePublicKeyHash, nameof(proxyRuntimePublicKeyHash));
        RequireProtocolMajor(bridgeProtocolMajor);
        return Encoding.UTF8.GetBytes(
            string.Join(
                '|',
                "renew",
                pairId.ToString("D", CultureInfo.InvariantCulture),
                proxyRuntimePublicKeyHash,
                expiresAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                bridgeProtocolMajor.ToString(CultureInfo.InvariantCulture)));
    }

    private static void RequireProtocolMajor(int bridgeProtocolMajor)
    {
        if (bridgeProtocolMajor != RemoteRuntimeBridgePaths.CurrentProtocolMajor)
            throw new ArgumentOutOfRangeException(nameof(bridgeProtocolMajor));
    }

    private static void RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A nonblank value is required.", parameterName);
    }
}
