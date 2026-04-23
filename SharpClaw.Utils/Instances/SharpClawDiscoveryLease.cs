using System.Diagnostics;

namespace SharpClaw.Utils.Instances;

/// <summary>
/// Publishes and refreshes a discovery lease for a running instance.
/// </summary>
public sealed class SharpClawDiscoveryLease : IDisposable
{
    private readonly SharpClawInstancePaths _instancePaths;
    private readonly string _baseUrl;
    private readonly DateTimeOffset _startedAtUtc;
    private readonly int _processId;
    private readonly Timer _timer;
    private int _disposeState;

    public SharpClawDiscoveryLease(
        SharpClawInstancePaths instancePaths,
        string baseUrl,
        TimeSpan refreshInterval)
    {
        ArgumentNullException.ThrowIfNull(instancePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        if (refreshInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(refreshInterval), "Refresh interval must be greater than zero.");

        _instancePaths = instancePaths;
        _baseUrl = baseUrl;
        _startedAtUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        _processId = Environment.ProcessId;
        _timer = new Timer(_ => Refresh(), null, refreshInterval, refreshInterval);
    }

    /// <summary>
    /// Publishes the current discovery entry immediately.
    /// </summary>
    public void PublishNow() => Refresh();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        _timer.Dispose();
    }

    private void Refresh()
    {
        if (_disposeState != 0)
            return;

        _instancePaths.PublishDiscoveryEntry(_baseUrl, _startedAtUtc, _processId);
    }
}
