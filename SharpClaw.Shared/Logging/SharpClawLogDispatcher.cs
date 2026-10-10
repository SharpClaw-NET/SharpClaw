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
    private readonly object _intakeGate = new();
    private readonly object _consoleGate = new();
    private readonly Task _worker;
    private readonly Task _timer;
    private int _shutdown;
    private long _droppedRecords;
    private string? _failure;

    public SharpClawLogDispatcher(
        DurableSegmentStore records,
        string appName,
        Guid bootId,
        SharpClawLoggingOptions options)
    {
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
        _worker = Task.Run(ProcessLoopAsync);
        _timer = Task.Run(FlushLoopAsync);
    }

    public int QueueDepth => _channel.Reader.Count;
    public long DroppedRecords => Volatile.Read(ref _droppedRecords);
    public string? Failure => Volatile.Read(ref _failure);

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

    public async Task FlushAndSealAsync(CancellationToken cancellationToken = default)
    {
        var firstShutdown = false;
        lock (_intakeGate)
        {
            if (Interlocked.Exchange(ref _shutdown, 1) == 0)
            {
                firstShutdown = true;
                _timerCancellation.Cancel();
                _channel.Writer.TryComplete();
            }
        }

        if (!firstShutdown)
        {
            await Task.WhenAll(_worker, _timer)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await Task.WhenAll(_timer, _worker)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public void RecordFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Interlocked.CompareExchange(ref _failure, exception.Message, null);
    }

    public async ValueTask DisposeAsync()
    {
        await FlushAndSealAsync().ConfigureAwait(false);
        _timerCancellation.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private bool TryEnqueue(DispatchItem item)
    {
        if (_channel.Writer.TryWrite(item))
            return true;
        if (item.Record is null || item.Level < LogEventLevel.Warning)
            return false;

        try
        {
            if (!_channel.Writer.WaitToWriteAsync(_timerCancellation.Token)
                    .AsTask()
                    .WaitAsync(CapacityWait)
                    .GetAwaiter()
                    .GetResult())
            {
                return false;
            }

            return _channel.Writer.TryWrite(item);
        }
        catch
        {
            return false;
        }
    }

    private async Task ProcessLoopAsync()
    {
        await foreach (var item in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
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
                await _records.AppendAsync(item.Stream, item.Record, mode)
                    .ConfigureAwait(false);
                RenderConsole(item.Record);
            }
            catch (Exception ex)
            {
                RecordFailure(ex);
            }
        }

        try
        {
            await FlushDropSummariesAsync().ConfigureAwait(false);
            await FlushKnownStreamsAsync().ConfigureAwait(false);
            foreach (var stream in _knownStreams.Keys)
                await _records.SealAsync(stream).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordFailure(ex);
        }
    }

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

    private async Task FlushKnownStreamsAsync()
    {
        foreach (var stream in _knownStreams.Keys)
            await _records.FlushAsync(stream).ConfigureAwait(false);
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
            DurableWriteMode.Durable);

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
