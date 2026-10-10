using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace SharpClaw.Shared.Instances;

/// <summary>A validated Gateway target and its independently supplied access credential.</summary>
public sealed class RemoteGatewayConnection
{
    private RemoteGatewayConnection(Uri gatewayBaseUri, string? accessToken)
    {
        GatewayBaseUri = gatewayBaseUri;
        AccessToken = accessToken;
    }

    /// <summary>Gets the absolute Gateway API prefix, including its trailing slash.</summary>
    public Uri GatewayBaseUri { get; }

    /// <summary>Gets the peer credential for protected configuration and transport only.</summary>
    [JsonIgnore]
    public string? AccessToken { get; }

    /// <summary>Validates a Gateway address and optional bearer credential.</summary>
    public static RemoteGatewayConnection Create(Uri gatewayUri, string? accessToken)
    {
        ArgumentNullException.ThrowIfNull(gatewayUri);
        if (!gatewayUri.IsAbsoluteUri ||
            !(string.Equals(gatewayUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
              string.Equals(gatewayUri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)) ||
            !string.IsNullOrEmpty(gatewayUri.UserInfo) ||
            !string.IsNullOrEmpty(gatewayUri.Query) ||
            !string.IsNullOrEmpty(gatewayUri.Fragment) ||
            gatewayUri.AbsoluteUri.Length > 2048)
        {
            throw new ArgumentException("Enter an HTTP or HTTPS Gateway address without embedded credentials, a query or a fragment.", nameof(gatewayUri));
        }

        accessToken = accessToken?.Trim(' ');
        accessToken = string.IsNullOrEmpty(accessToken) ? null : accessToken;
        if (accessToken is not null && !IsValidAccessToken(accessToken))
        {
            throw new ArgumentException("The Gateway access token is invalid.", nameof(accessToken));
        }

        if (accessToken is not null && string.Equals(gatewayUri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) && !gatewayUri.IsLoopback)
            throw new ArgumentException("Access tokens require HTTPS on remote Gateway hosts.", nameof(accessToken));

        var path = gatewayUri.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            path += "/api";
        var canonical = new UriBuilder(gatewayUri) { Path = path + "/" }.Uri;
        return new RemoteGatewayConnection(canonical, accessToken);
    }

    private static bool IsValidAccessToken(ReadOnlySpan<char> accessToken)
    {
        if (accessToken.Length > 8192)
            return false;
        foreach (ref readonly var character in accessToken)
        {
            if (!char.IsAsciiLetterOrDigit(character) &&
                character is not ('-' or '_' or '.' or '~' or '+' or '/' or '='))
                return false;
        }
        return true;
    }

    /// <summary>Reads the protected proxy configuration; an enabled invalid target fails closed.</summary>
    public static RemoteGatewayConnection? FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var enabledText = configuration["RemoteBackend:Enabled"];
        if (enabledText is null)
            return null;
        if (!bool.TryParse(enabledText, out var enabled))
            throw new InvalidDataException("The remote backend mode is invalid.");
        if (!enabled)
            return null;

        if (!Uri.TryCreate(configuration["RemoteBackend:GatewayUrl"], UriKind.Absolute, out var gatewayUri))
            throw new InvalidDataException("The enabled remote backend has no valid Gateway address.");
        return Create(gatewayUri, configuration["RemoteBackend:AccessToken"]);
    }
}
