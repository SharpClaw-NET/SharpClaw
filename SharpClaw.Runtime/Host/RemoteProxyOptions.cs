using Microsoft.Extensions.Configuration;
using SharpClaw.Shared.RemoteRuntimeBridge;

namespace SharpClaw.Runtime.Host;

public sealed record RemoteProxyOptions
{
    public const string ConfigurationSectionName = "Runtime:RemoteProxy";

    private RemoteProxyOptions(
        string localUrl,
        string gatewayUrl,
        string gatewayInstanceId,
        string authoritativeRuntimeInstanceId,
        string proxyRuntimeInstanceId,
        string invitationSecret,
        string privateKeySecret,
        string clientCertificateSecret,
        int connectTimeoutSeconds,
        int activityTimeoutSeconds,
        Guid? invitationPairId,
        string? gatewayServerPublicKeyHash,
        string? authoritativeRuntimeInstallFingerprint,
        DateTimeOffset? invitationExpiresAtUtc,
        int bridgeProtocolMajor)
    {
        LocalUrl = localUrl;
        GatewayUrl = gatewayUrl;
        GatewayInstanceId = gatewayInstanceId;
        AuthoritativeRuntimeInstanceId = authoritativeRuntimeInstanceId;
        ProxyRuntimeInstanceId = proxyRuntimeInstanceId;
        InvitationSecret = invitationSecret;
        PrivateKeySecret = privateKeySecret;
        ClientCertificateSecret = clientCertificateSecret;
        ConnectTimeoutSeconds = connectTimeoutSeconds;
        ActivityTimeoutSeconds = activityTimeoutSeconds;
        InvitationPairId = invitationPairId;
        GatewayServerPublicKeyHash = gatewayServerPublicKeyHash;
        AuthoritativeRuntimeInstallFingerprint = authoritativeRuntimeInstallFingerprint;
        InvitationExpiresAtUtc = invitationExpiresAtUtc;
        BridgeProtocolMajor = bridgeProtocolMajor;
    }

    public string LocalUrl { get; }

    public string GatewayUrl { get; }

    public string GatewayInstanceId { get; }

    public string AuthoritativeRuntimeInstanceId { get; }

    public string ProxyRuntimeInstanceId { get; }

    public string InvitationSecret { get; }

    public string PrivateKeySecret { get; }

    public string ClientCertificateSecret { get; }

    public int ConnectTimeoutSeconds { get; }

    public int ActivityTimeoutSeconds { get; }

    public Guid? InvitationPairId { get; }

    public string? GatewayServerPublicKeyHash { get; }

    public string? AuthoritativeRuntimeInstallFingerprint { get; }

    public DateTimeOffset? InvitationExpiresAtUtc { get; }

    public int BridgeProtocolMajor { get; }

    public static RemoteProxyOptions? Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigurationSectionName);
        if (!section.GetChildren().Any())
            return null;

        var enabledText = section["Enabled"];
        if (!bool.TryParse(enabledText, out var enabled))
        {
            throw new InvalidOperationException(
                "Runtime:RemoteProxy:Enabled must be true or false when remote options are configured.");
        }

        if (!enabled)
            return null;

        return new RemoteProxyOptions(
            RequireLoopbackUrl(section, "LocalUrl"),
            RequireHttpsUrl(section, "GatewayUrl"),
            RequireValue(section, "GatewayInstanceId"),
            RequireValue(section, "AuthoritativeRuntimeInstanceId"),
            RequireValue(section, "ProxyRuntimeInstanceId"),
            RequireValue(section, "InvitationSecret"),
            RequireValue(section, "PrivateKeySecret"),
            RequireValue(section, "ClientCertificateSecret"),
            ReadBoundedInteger(section, "ConnectTimeoutSeconds", 1, 300),
            ReadBoundedInteger(section, "ActivityTimeoutSeconds", 1, 3600),
            ReadOptionalGuid(section, "InvitationPairId"),
            ReadOptionalValue(section, "GatewayServerPublicKeyHash"),
            ReadOptionalValue(section, "AuthoritativeRuntimeInstallFingerprint"),
            ReadOptionalTimestamp(section, "InvitationExpiresAtUtc"),
            ReadExactProtocolMajor(section));
    }

    public RemoteRuntimePairingInvitation CreateInvitation()
    {
        if (InvitationPairId is not { } pairId
            || string.IsNullOrWhiteSpace(GatewayServerPublicKeyHash)
            || string.IsNullOrWhiteSpace(AuthoritativeRuntimeInstallFingerprint)
            || InvitationExpiresAtUtc is not { } expiresAtUtc)
        {
            throw new InvalidOperationException(
                "Remote pairing options require an invitation ID, expiry, Gateway certificate fingerprint, and Runtime install fingerprint.");
        }

        return new RemoteRuntimePairingInvitation(
            pairId,
            InvitationSecret,
            GatewayInstanceId,
            GatewayServerPublicKeyHash,
            AuthoritativeRuntimeInstanceId,
            AuthoritativeRuntimeInstallFingerprint,
            BridgeProtocolMajor,
            expiresAtUtc);
    }

    private static string RequireValue(IConfigurationSection section, string name)
    {
        var value = section[name];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Runtime:RemoteProxy:{name} is required when remote mode is enabled.");
        }

        return value.Trim();
    }

    private static string RequireLoopbackUrl(IConfigurationSection section, string name)
    {
        var value = RequireValue(section, name);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || !uri.IsLoopback)
        {
            throw new InvalidOperationException(
                "Runtime:RemoteProxy:LocalUrl must be an HTTP or HTTPS loopback URL.");
        }

        return value;
    }

    private static string RequireHttpsUrl(IConfigurationSection section, string name)
    {
        var value = RequireValue(section, name);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Runtime:RemoteProxy:GatewayUrl must be an HTTPS URL.");
        }

        return value;
    }

    private static int ReadBoundedInteger(
        IConfigurationSection section,
        string name,
        int minimum,
        int maximum)
    {
        var value = section[name];
        if (!int.TryParse(value, out var seconds)
            || seconds < minimum
            || seconds > maximum)
        {
            throw new InvalidOperationException(
                $"Runtime:RemoteProxy:{name} must be between {minimum} and {maximum}.");
        }

        return seconds;
    }

    private static int ReadOptionalBoundedInteger(
        IConfigurationSection section,
        string name,
        int minimum,
        int maximum,
        int defaultValue)
    {
        if (string.IsNullOrWhiteSpace(section[name]))
            return defaultValue;

        return ReadBoundedInteger(section, name, minimum, maximum);
    }

    private static int ReadExactProtocolMajor(IConfigurationSection section)
    {
        var value = ReadOptionalBoundedInteger(
            section,
            "BridgeProtocolMajor",
            RemoteRuntimeBridgePaths.CurrentProtocolMajor,
            RemoteRuntimeBridgePaths.CurrentProtocolMajor,
            RemoteRuntimeBridgePaths.CurrentProtocolMajor);
        if (value != RemoteRuntimeBridgePaths.CurrentProtocolMajor)
            throw new InvalidOperationException(
                $"Runtime:RemoteProxy:BridgeProtocolMajor must be {RemoteRuntimeBridgePaths.CurrentProtocolMajor}.");

        return value;
    }

    private static Guid? ReadOptionalGuid(IConfigurationSection section, string name)
    {
        var value = ReadOptionalValue(section, name);
        if (value is null)
            return null;

        if (!Guid.TryParse(value, out var result) || result == Guid.Empty)
        {
            throw new InvalidOperationException(
                $"Runtime:RemoteProxy:{name} must be a non-empty GUID when specified.");
        }

        return result;
    }

    private static DateTimeOffset? ReadOptionalTimestamp(
        IConfigurationSection section,
        string name)
    {
        var value = ReadOptionalValue(section, name);
        if (value is null)
            return null;

        if (!DateTimeOffset.TryParse(value, out var result))
        {
            throw new InvalidOperationException(
                $"Runtime:RemoteProxy:{name} must be a valid timestamp when specified.");
        }

        return result;
    }

    private static string? ReadOptionalValue(IConfigurationSection section, string name)
        => string.IsNullOrWhiteSpace(section[name]) ? null : section[name]!.Trim();
}
