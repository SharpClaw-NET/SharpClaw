using System.Diagnostics;

namespace SharpClaw.Shared.Instances;

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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1054",
        Justification = "The existing published constructor accepts exact discovery endpoint text and forwards it unchanged to the versioned discovery file; its CLR signature and null-literal source compatibility are maintained.")]
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
        using var process = Process.GetCurrentProcess();
        _startedAtUtc = new DateTimeOffset(process.StartTime.ToUniversalTime());
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
