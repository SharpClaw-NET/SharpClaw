using Microsoft.Extensions.Logging;

using SharpClaw.Application.Core.Clients;

namespace SharpClaw.Modules.Transcription.Clients;

/// <summary>
/// Manages shared audio capture sessions per device. Multiple
/// transcription jobs targeting the same device share a single
/// WASAPI capture and <see cref="AudioRingBuffer"/> instead of each
/// opening its own.
/// <para>
/// Usage: call <see cref="Acquire"/> to get a buffer handle (starts
/// capture on first subscriber), call <see cref="ReleaseAsync"/> when
/// done (stops capture when the last subscriber releases).
/// </para>
/// <para>
/// <b>Singleton.</b> All access to the internal session dictionary is
/// serialised via <see cref="_lock"/>.
/// </para>
/// </summary>
public sealed class SharedAudioCaptureManager(
    IAudioCaptureProvider captureProvider,
    ILogger<SharedAudioCaptureManager> logger) : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, SharedCaptureSession> _sessions = new();
    private bool _disposed;

    private const string DefaultDeviceKey = "__default__";

    /// <summary>
    /// Acquires a shared <see cref="AudioRingBuffer"/> for the given
    /// device. If no capture session exists yet, one is started. If a
    /// session already exists the reference count is incremented and
    /// the existing buffer is returned.
    /// </summary>
    /// <param name="deviceIdentifier">
    /// OS-level device identifier, or <see langword="null"/>/"default"
    /// for the system default input device.
    /// </param>
    /// <param name="sampleRate">Audio sample rate (Hz).</param>
    /// <param name="bufferCapacitySeconds">Ring buffer capacity.</param>
    /// <returns>
    /// The shared ring buffer. The caller MUST call
    /// <see cref="ReleaseAsync"/> when it no longer needs audio.
    /// </returns>
    public AudioRingBuffer Acquire(string? deviceIdentifier, int sampleRate, int bufferCapacitySeconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = NormalizeKey(deviceIdentifier);

        lock (_lock)
        {
            if (_sessions.TryGetValue(key, out var existing))
            {
                existing.RefCount++;
                logger.LogDebug(
                    "Shared capture for device '{Device}': ref count → {Count}",
                    key, existing.RefCount);
                return existing.Buffer;
            }

            var buffer = new AudioRingBuffer(sampleRate, bufferCapacitySeconds);
            var cts = new CancellationTokenSource();

            var captureTask = Task.Run(async () =>
            {
                try
                {
                    await captureProvider.CaptureRawAsync(
                        deviceIdentifier,
                        samples => buffer.Write(samples),
                        cts.Token);
                }
                catch (OperationCanceledException) { /* expected on stop */ }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Shared audio capture for device '{Device}' crashed.", key);
                }
            });

            var session = new SharedCaptureSession(buffer, cts, captureTask);
            _sessions[key] = session;

            logger.LogInformation(
                "Started shared capture for device '{Device}'.", key);

            return buffer;
        }
    }

    /// <summary>
    /// Releases a reference to the shared capture session for the given
    /// device. When the last subscriber releases, the WASAPI capture is
    /// stopped and resources are cleaned up.
    /// </summary>
    public async Task ReleaseAsync(string? deviceIdentifier)
    {
        var key = NormalizeKey(deviceIdentifier);
        SharedCaptureSession? toStop = null;

        lock (_lock)
        {
            if (!_sessions.TryGetValue(key, out var session))
                return;

            session.RefCount--;
            logger.LogDebug(
                "Shared capture for device '{Device}': ref count → {Count}",
                key, session.RefCount);

            if (session.RefCount <= 0)
            {
                _sessions.Remove(key);
                toStop = session;
            }
        }

        if (toStop is not null)
        {
            logger.LogInformation(
                "Stopping shared capture for device '{Device}' (last subscriber released).",
                key);
            await StopSessionAsync(toStop);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        List<SharedCaptureSession> sessions;
        lock (_lock)
        {
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            session.Cts.Cancel();
            session.Cts.Dispose();
        }
    }

    private static async Task StopSessionAsync(SharedCaptureSession session)
    {
        await session.Cts.CancelAsync();
        try { await session.CaptureTask; }
        catch { /* swallow — errors already logged in the capture task */ }
        session.Cts.Dispose();
    }

    /// <summary>
    /// Checks whether the capture session for the given device is still
    /// healthy. Returns <c>(true, null)</c> when the capture task is
    /// running, or <c>(false, errorMessage)</c> when it has faulted or
    /// completed unexpectedly.
    /// </summary>
    public (bool IsHealthy, string? Error) GetCaptureStatus(string? deviceIdentifier)
    {
        var key = NormalizeKey(deviceIdentifier);

        lock (_lock)
        {
            if (!_sessions.TryGetValue(key, out var session))
                return (false, "No active capture session for this device.");

            if (session.CaptureTask.IsFaulted)
                return (false,
                    session.CaptureTask.Exception?.InnerException?.Message
                    ?? "Audio capture task faulted.");

            if (session.CaptureTask.IsCompleted)
                return (false, "Audio capture task completed unexpectedly.");

            return (true, null);
        }
    }

    private static string NormalizeKey(string? deviceIdentifier) =>
        string.IsNullOrEmpty(deviceIdentifier) || deviceIdentifier == "default"
            ? DefaultDeviceKey
            : deviceIdentifier;

    private sealed class SharedCaptureSession(
        AudioRingBuffer buffer,
        CancellationTokenSource cts,
        Task captureTask)
    {
        public AudioRingBuffer Buffer { get; } = buffer;
        public CancellationTokenSource Cts { get; } = cts;
        public Task CaptureTask { get; } = captureTask;
        public int RefCount { get; set; } = 1;
    }
}
