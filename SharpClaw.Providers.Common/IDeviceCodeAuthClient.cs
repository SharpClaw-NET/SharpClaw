using SharpClaw.Contracts.DTOs.Providers;

namespace SharpClaw.Providers.Common;

/// <summary>
/// Implemented by provider API clients that authenticate via the
/// OAuth 2.0 Device Authorization Grant (RFC 8628) instead of a static API key.
/// </summary>
public interface IDeviceCodeAuthClient
{
    /// <summary>
    /// Starts the device code flow and returns a session containing the user code
    /// and verification URI.
    /// </summary>
    Task<DeviceCodeSession> StartDeviceCodeFlowAsync(CancellationToken ct = default);

    /// <summary>
    /// Polls the authorization server until the user completes authentication.
    /// Returns the access token on success.
    /// </summary>
    Task<string> PollForAccessTokenAsync(DeviceCodeSession session, CancellationToken ct = default);
}
