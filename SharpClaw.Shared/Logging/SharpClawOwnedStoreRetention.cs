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
    private int _disposed;

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

        _loop = Task.Run(RunAsync);
    }

    public Task FirstRun => _firstRun.Task;
    public Task Completion => _loop;
    public Exception? Failure => Volatile.Read(ref _failure);

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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            await _loop.ConfigureAwait(false);
            return;
        }

        _stop.Cancel();
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }
}
