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
    private readonly Lock _retirement = new();
    private readonly Action _publish;
    private readonly Action<Exception> _observeFailure;
    private int _disposeState;
    private int _refreshing;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1054",
        Justification = "The existing published constructor accepts exact discovery endpoint text and forwards it unchanged to the versioned discovery file; its CLR signature and null-literal source compatibility are maintained.")]
    public SharpClawDiscoveryLease(
        SharpClawInstancePaths instancePaths,
        string baseUrl,
        TimeSpan refreshInterval) : this(instancePaths, baseUrl, refreshInterval, null, null) { }

    internal SharpClawDiscoveryLease(SharpClawInstancePaths instancePaths, string baseUrl,
        TimeSpan refreshInterval, Action? publish, Action<Exception>? observeFailure)
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
        _publish = publish ?? (() => _instancePaths.PublishDiscoveryEntry(_baseUrl, _startedAtUtc, _processId));
        _observeFailure = observeFailure ?? (failure => Trace.TraceWarning("SharpClaw discovery refresh failed ({0}).", failure.GetType().FullName));
        _timer = new Timer(_ => RefreshPeriodic(), null, refreshInterval, refreshInterval);
    }

    /// <summary>
    /// Publishes the current discovery entry immediately.
    /// </summary>
    public void PublishNow() => Refresh();

    public void Dispose()
    {
        lock (_retirement)
        {
            if (_disposeState != 0) return;
            Volatile.Write(ref _disposeState, 1);
            // Timer callbacks are context-free. The legacy synchronous owner must
            // join them; repeats acquire this lock only after actual retirement.
#pragma warning disable VSTHRD002
            _timer.DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
        }
    }

    private void Refresh()
    {
        if (Volatile.Read(ref _disposeState) != 0)
            return;

        _publish();
    }

    private void RefreshPeriodic()
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;
        try { Refresh(); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            try { _observeFailure(failure); }
            catch (Exception sinkFailure) when (sinkFailure is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // An ordinary unavailable diagnostic sink cannot escape the timer.
            }
        }
        finally { Volatile.Write(ref _refreshing, 0); }
    }
}
