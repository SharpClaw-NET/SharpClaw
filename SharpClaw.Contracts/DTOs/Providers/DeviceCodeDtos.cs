namespace SharpClaw.Contracts.DTOs.Providers;

/// <summary>
/// Returned when a device code flow is initiated for a provider.
/// The user must visit <see cref="VerificationUri"/> and enter <see cref="UserCode"/>.
/// All session fields are included so the client can pass them back for polling.
/// </summary>
public sealed record DeviceCodeResponse(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int ExpiresInSeconds,
    int IntervalSeconds);

/// <summary>
/// Holds internal state for an in-progress device code flow.
/// </summary>
public sealed record DeviceCodeSession(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int ExpiresInSeconds,
    int IntervalSeconds);

/// <summary>
/// Sent by the client to poll for the completion of a device code flow.
/// Contains the session state returned by the start endpoint.
/// </summary>
public sealed record DeviceCodePollRequest(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int ExpiresInSeconds,
    int IntervalSeconds);
