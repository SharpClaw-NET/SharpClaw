namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// Abstracts audio capture from a device. Implementations buffer
/// recorded PCM into WAV-formatted chunks of a configurable duration
/// and push them through a callback.
/// </summary>
public interface IAudioCaptureProvider : IDisposable
{
    /// <summary>
    /// Starts recording from the specified device and delivers WAV-encoded
    /// audio chunks to <paramref name="onChunkReady"/> at roughly
    /// <paramref name="chunkDuration"/> intervals.
    /// <para>
    /// <b>Threading contract:</b> implementations MUST call
    /// <paramref name="onChunkReady"/> sequentially on a single task.
    /// The callback MUST NEVER run concurrently with itself. Callers
    /// rely on this guarantee for safe access to captured shared state
    /// (e.g. error counters, stream offsets). Violating this invariant
    /// causes silent data races.
    /// </para>
    /// </summary>
    /// <param name="deviceIdentifier">
    /// OS-level device identifier. Null or "default" selects the system
    /// default input device.
    /// </param>
    /// <param name="chunkDuration">Target duration of each audio chunk.</param>
    /// <param name="onChunkReady">
    /// Called with (wavBytes, chunkIndex) each time a chunk is ready.
    /// Must be invoked sequentially — never from parallel tasks.
    /// </param>
    /// <param name="ct">Stops recording when cancelled.</param>
    Task CaptureAsync(
        string? deviceIdentifier,
        TimeSpan chunkDuration,
        Func<byte[], int, Task> onChunkReady,
        CancellationToken ct);

    /// <summary>
    /// Lists available input (recording) devices on the current system.
    /// Returns tuples of (deviceIdentifier, friendlyName).
    /// </summary>
    IReadOnlyList<(string Id, string Name)> ListDevices();
}
