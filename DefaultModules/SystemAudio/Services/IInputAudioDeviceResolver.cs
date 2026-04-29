namespace SharpClaw.Modules.SystemAudio.Services;

/// <summary>
/// Resolves input audio device details from the SystemAudio module's data store.
/// Public so that other modules (e.g. Transcription) can consume it across
/// the module boundary via DI.
/// </summary>
public interface IInputAudioDeviceResolver
{
    /// <summary>
    /// Returns the device identifier and name for the given input audio resource ID,
    /// or <c>null</c> if no matching device is found.
    /// </summary>
    Task<(string DeviceIdentifier, string Name)?> GetDeviceAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Returns the ID of the first available input audio device, or <see cref="Guid.Empty"/>
    /// if none are configured. Used by task step executors that need a default device.
    /// </summary>
    Task<Guid> GetDefaultDeviceIdAsync(CancellationToken ct = default);
}
