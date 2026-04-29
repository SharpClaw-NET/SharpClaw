using SharpClaw.Contracts.DTOs.Providers;

namespace SharpClaw.Contracts.Providers;

/// <summary>
/// Optional device-code authentication flow surfaced by a provider
/// plugin. Only providers that authenticate via OAuth device-code
/// (currently GitHub Copilot) implement this; other plugins return
/// <see langword="null"/> from <c>IProviderPlugin.DeviceCodeFlow</c>.
/// </summary>
public interface IDeviceCodeFlow
{
    /// <summary>
    /// Starts a new device-code session and returns the user-facing
    /// verification URI plus the polling parameters needed to complete
    /// the flow.
    /// </summary>
    Task<DeviceCodeSession> StartAsync(HttpClient httpClient, CancellationToken ct = default);

    /// <summary>
    /// Polls the provider's token endpoint with the device code from a
    /// previously started session. Returns the access token when the
    /// user has authorised the device, <see langword="null"/> while the
    /// session is still pending.
    /// </summary>
    Task<string?> PollAsync(
        HttpClient httpClient,
        DeviceCodeSession session,
        CancellationToken ct = default);
}
