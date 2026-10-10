using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
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


public sealed class SharpClawLogDispatcher : IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan CapacityWait = TimeSpan.FromSeconds(2);

    private readonly DurableSegmentStore _records;
    private readonly SharpClawLoggingOptions _options;
    private readonly DurableStreamKey _processStream;
    private readonly Channel<DispatchItem> _channel;
    private readonly CancellationTokenSource _timerCancellation = new();
    private readonly ConcurrentDictionary<DurableStreamKey, long> _drops = new();
    private readonly ConcurrentDictionary<DurableStreamKey, byte> _knownStreams = new();
    private readonly Lock _intakeGate = new();
    private readonly Lock _consoleGate = new();
    private readonly Task _worker;
    private readonly Task _timer;
    private int _shutdown;
    private Task? _shutdownTask;
    private Task? _disposeTask;
    private long _droppedRecords;
    private string? _failure;

    public SharpClawLogDispatcher(
        DurableSegmentStore records,
        string appName,
        Guid bootId,
        SharpClawLoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(options);
        _records = records;
        _options = options;
        _processStream = DurableStreamKey.Process(appName, bootId);
        _knownStreams.TryAdd(_processStream, 0);
        _channel = Channel.CreateBounded<DispatchItem>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _worker = Task.Run(ProcessLoopAsync, CancellationToken.None);
        _timer = Task.Run(FlushLoopAsync, CancellationToken.None);
    }

    public int QueueDepth => _channel.Reader.Count;
    public long DroppedRecords => Volatile.Read(ref _droppedRecords);
    public string? Failure => Volatile.Read(ref _failure);

    [SuppressMessage("Design", "CA1031", Justification = "The synchronous logging sink is best effort; failures are surfaced through Failure instead of interrupting the application being logged.")]
    public void Emit(LogEvent logEvent)
    {
        if (Volatile.Read(ref _shutdown) != 0)
            return;

        try
        {
            var record = SharpClawLogNormalizer.Normalize(
                logEvent,
                _records.MaxRecordBytes);
            var ownership = SharpClawLogOwnership.Current;
            var stream = ownership is null
                ? _processStream
                : DurableStreamKey.Registration(ownership.SourceId, ownership.BootId);
            var item = new DispatchItem(
                stream,
                record,
                logEvent.Level,
                Completion: null);
            lock (_intakeGate)
            {
                if (Volatile.Read(ref _shutdown) != 0)
                    return;

                _knownStreams.TryAdd(stream, 0);
                if (!TryEnqueue(item))
                {
                    Interlocked.Increment(ref _droppedRecords);
                    _drops.AddOrUpdate(stream, 1, static (_, count) => count + 1);
                }
            }
        }
        catch (Exception ex)
        {
            RecordFailure(ex);
        }
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _shutdown) != 0)
            return;

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await _channel.Writer.WriteAsync(
                new DispatchItem(
                    _processStream,
                    null,
                    LogEventLevel.Information,
                    completion),
                cancellationToken)
            .ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task FlushAndSealAsync(CancellationToken cancellationToken = default)
    {
        Task shutdown;
        lock (_intakeGate)
        {
            if (_shutdownTask is null)
            {
                Volatile.Write(ref _shutdown, 1);
                _channel.Writer.TryComplete();
                _shutdownTask = StopLoopsAsync();
            }
            shutdown = _shutdownTask;
        }
        return shutdown.WaitAsync(cancellationToken);
    }

    [SuppressMessage("Usage", "VSTHRD003", Justification = "This dispatcher starts and retains both Task.Run loops; shutdown joins them and cancellation callbacks without a UI synchronization context or JoinableTaskFactory.")]
    private async Task StopLoopsAsync()
    {
        var cancellation = _timerCancellation.CancelAsync();
        await Task.WhenAll(cancellation, _timer, _worker).ConfigureAwait(false);
    }

    public void RecordFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Interlocked.CompareExchange(ref _failure, exception.Message, null);
    }

    public ValueTask DisposeAsync()
    {
        lock (_intakeGate)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await FlushAndSealAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _timerCancellation.Dispose();
        }
    }

    [SuppressMessage("Usage", "VSTHRD002", Justification = "IDisposable is a published synchronous contract. Owned worker loops and asynchronous shutdown use ConfigureAwait(false) and run without a UI synchronization context.")]
    public void Dispose() => DisposeAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();

    [SuppressMessage("Usage", "VSTHRD002", Justification = "ILogger.Emit is synchronous. Its bounded capacity wait observes the channel operation itself, cancels that operation at the two-second budget, and uses a worker running independently of the caller's synchronization context.")]
    private bool TryEnqueue(DispatchItem item)
    {
        if (_channel.Writer.TryWrite(item))
            return true;
        if (item.Record is null || item.Level < LogEventLevel.Warning)
            return false;

        using var capacity = CancellationTokenSource.CreateLinkedTokenSource(_timerCancellation.Token);
        capacity.CancelAfter(CapacityWait);
        try
        {
            var available = _channel.Writer.WaitToWriteAsync(capacity.Token)
                .AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
            return available && _channel.Writer.TryWrite(item);
        }
        catch (OperationCanceledException) when (capacity.IsCancellationRequested)
        {
            return false;
        }
    }

    [SuppressMessage("Design", "CA1031", Justification = "Each log write or flush is best effort. Failures are retained in Failure, flush receipts settle, and the loop continues draining remaining accepted records.")]
    private async Task ProcessLoopAsync()
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (item.Record is null)
            {
                try
                {
                    await FlushKnownStreamsAsync().ConfigureAwait(false);
                    item.Completion?.TrySetResult();
                }
                catch (Exception ex)
                {
                    RecordFailure(ex);
                    item.Completion?.TrySetException(ex);
                }

                continue;
            }

            try
            {
                if (_drops.TryRemove(item.Stream, out var dropped) && dropped > 0)
                    await AppendDropSummaryAsync(item.Stream, dropped)
                        .ConfigureAwait(false);

                var mode = item.Level >= LogEventLevel.Error
                    ? DurableWriteMode.Durable
                    : DurableWriteMode.Buffered;
                await _records.AppendAsync(item.Stream, item.Record, mode, CancellationToken.None)
                    .ConfigureAwait(false);
                RenderConsole(item.Record);
            }
            catch (Exception ex)
            {
                RecordFailure(ex);
            }
        }

        await CompleteStreamsAsync().ConfigureAwait(false);
    }

    [SuppressMessage("Design", "CA1031", Justification = "Final logging cleanup attempts drop summaries, flush and every stream seal even after an earlier failure; each failure is exposed through Failure.")]
    private async Task CompleteStreamsAsync()
    {
        try { await FlushDropSummariesAsync().ConfigureAwait(false); }
        catch (Exception failure) { RecordFailure(failure); }
        try { await FlushKnownStreamsAsync().ConfigureAwait(false); }
        catch (Exception failure) { RecordFailure(failure); }
        try { await CompleteKnownStreamsAsync(seal: true).ConfigureAwait(false); }
        catch (Exception failure) { RecordFailure(failure); }
    }

    [SuppressMessage("Design", "CA1031", Justification = "The logging timer reports failure through Failure; stopping the application must still drain the independently owned worker.")]
    private async Task FlushLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(_options.FlushInterval);
            while (await timer.WaitForNextTickAsync(_timerCancellation.Token).ConfigureAwait(false))
            {
                _channel.Writer.TryWrite(new DispatchItem(
                    _processStream,
                    null,
                    LogEventLevel.Information,
                    Completion: null));
            }
        }
        catch (OperationCanceledException) when (_timerCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RecordFailure(ex);
        }
    }

    private Task FlushKnownStreamsAsync() => CompleteKnownStreamsAsync(seal: false);

    [SuppressMessage("Design", "CA1031", Justification = "Every known stream is attempted before the first failure is rethrown and recorded by the caller.")]
    private async Task CompleteKnownStreamsAsync(bool seal)
    {
        Exception? failure = null;
        foreach (var stream in _knownStreams.Keys)
        {
            try
            {
                if (seal)
                    await _records.SealAsync(stream, CancellationToken.None).ConfigureAwait(false);
                else
                    await _records.FlushAsync(stream, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
                RecordFailure(exception);
            }
        }
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task FlushDropSummariesAsync()
    {
        foreach (var pair in _drops.ToArray())
        {
            if (pair.Value <= 0
                || !_drops.TryRemove(pair.Key, out var dropped)
                || dropped <= 0)
            {
                continue;
            }

            await AppendDropSummaryAsync(pair.Key, dropped)
                .ConfigureAwait(false);
        }
    }

    private ValueTask<DurableAppendReceipt> AppendDropSummaryAsync(
        DurableStreamKey stream,
        long dropped) =>
        _records.AppendAsync(
            stream,
            new DurableRecordWrite(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                "Warning",
                "RecordsDropped",
                $"Dropped {dropped} operational log record(s) because the bounded dispatcher was full.",
                Properties: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["DroppedCount"] = dropped.ToString(CultureInfo.InvariantCulture),
                }),
            DurableWriteMode.Durable, CancellationToken.None);

    private void RenderConsole(DurableRecordWrite record)
    {
        if (!_options.ConsoleEnabled)
            return;

        var output = $"[{record.Timestamp:O}] {record.Level} {record.Category ?? record.EventName}: {record.Message}";
        if (!string.IsNullOrWhiteSpace(record.ExceptionText))
            output += Environment.NewLine + record.ExceptionText;

        lock (_consoleGate)
        {
            if (Enum.TryParse<LogEventLevel>(record.Level, out var level)
                && level >= LogEventLevel.Error)
            {
                Console.Error.WriteLine(output);
            }
            else
            {
                Console.WriteLine(output);
            }
        }
    }

    private sealed record DispatchItem(
        DurableStreamKey Stream,
        DurableRecordWrite? Record,
        LogEventLevel Level,
        TaskCompletionSource? Completion);
}
