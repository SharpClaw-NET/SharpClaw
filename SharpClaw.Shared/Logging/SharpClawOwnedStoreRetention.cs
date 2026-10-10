using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SharpClaw.Shared.DurableStorage;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.Security;
using MsLogger = Microsoft.Extensions.Logging.ILogger;

namespace SharpClaw.Shared.Logging;


public sealed class SharpClawOwnedStoreRetention : IAsyncDisposable
{
    private readonly DurableSegmentStore _records;
    private readonly SharpClawOwnedStoreRetentionOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _firstRun = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _loop;
    private Exception? _failure;
    private readonly Lock _disposeGate = new();
    private Task? _disposeTask;

    public SharpClawOwnedStoreRetention(
        DurableSegmentStore records,
        SharpClawOwnedStoreRetentionOptions? options = null)
    {
        _records = records ?? throw new ArgumentNullException(nameof(records));
        _options = options ?? new SharpClawOwnedStoreRetentionOptions();
        if (_options.Interval <= TimeSpan.Zero
            || _options.Interval > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        _loop = Task.Run(RunAsync, CancellationToken.None);
    }

    public Task FirstRun => _firstRun.Task;
    public Task Completion => _loop;
    public Exception? Failure => Volatile.Read(ref _failure);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "Retention is a best-effort background sweep. Every failure is retained in the public Failure property and FirstRun always settles; disposal joins the owned loop.")]
    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await _records.ApplyRetentionAsync(
                            _options.Retention,
                            _stop.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    Interlocked.CompareExchange(ref _failure, exception, null);
                }
                finally
                {
                    _firstRun.TrySetResult();
                }

                try
                {
                    await Task.Delay(_options.Interval, _stop.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            Interlocked.CompareExchange(ref _failure, exception, null);
            _firstRun.TrySetResult();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            return new ValueTask(_disposeTask ??= StopAsync());
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD003", Justification = "This object starts and retains its background loop with Task.Run; no UI synchronization context or JoinableTaskFactory is involved.")]
    private async Task StopAsync()
    {
        try
        {
            var cancellation = _stop.CancelAsync();
            await Task.WhenAll(cancellation, _loop).ConfigureAwait(false);
        }
        finally
        {
            _stop.Dispose();
        }
    }
}
