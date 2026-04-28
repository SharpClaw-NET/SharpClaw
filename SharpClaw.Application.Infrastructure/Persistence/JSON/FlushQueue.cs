using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace SharpClaw.Infrastructure.Persistence.JSON;

/// <summary>
/// Bounded <see cref="Channel{T}"/>-backed queue that decouples
/// <see cref="SharpClawDbContext.SaveChangesAsync"/> from file I/O.
/// <para>
/// <b>Phase K (RGAP-4):</b> A write-through overlay
/// (<see cref="ConcurrentDictionary{TKey, TValue}"/>) holds serialised
/// entity bytes so that <see cref="ColdEntityStore.FindAsync{T}"/>
/// always sees the latest data even when async flush is lagging.
/// The overlay is cleared by the <see cref="FlushWorker"/> after a
/// successful disk write.
/// </para>
/// </summary>
public sealed class FlushQueue : IDisposable
{
    /// <summary>
    /// A single flush intent captured from <see cref="SharpClawDbContext.SaveChangesAsync"/>.
    /// </summary>
    internal sealed record FlushIntent(
        IReadOnlyList<(Type ClrType, Guid Id, EntityState State)> EntityChanges,
        IReadOnlySet<string> JoinTableChanges,
        /// <summary>
        /// Pre-serialised entity bytes keyed by <c>(TypeName, Id)</c>.
        /// Written to the overlay immediately on enqueue.
        /// </summary>
        IReadOnlyDictionary<(string TypeName, Guid Id), byte[]> SerializedEntities,
        IReadOnlyDictionary<(Type ClrType, Guid Id), byte[]> SerializedEntitiesByClrType);

    private readonly Channel<FlushIntent> _channel;
    private readonly ILogger<FlushQueue> _logger;

    /// <summary>
    /// Write-through overlay: serialised bytes keyed by <c>(TypeName, Id)</c>.
    /// <see cref="ColdEntityStore"/> checks this before disk.
    /// <see cref="FlushWorker"/> removes entries after successful flush.
    /// Entries with <c>null</c> value represent deletes (tombstones).
    /// </summary>
    internal ConcurrentDictionary<(string TypeName, Guid Id), byte[]?> Overlay { get; } = new();

    /// <summary>
    /// Bounded capacity for back-pressure.
    /// </summary>
    internal int Capacity { get; }

    internal FlushQueue(ILogger<FlushQueue> logger, int capacity = 256)
    {
        _logger = logger;
        Capacity = capacity;
        _channel = Channel.CreateBounded<FlushIntent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>
    /// Enqueues a flush intent and immediately writes serialised entities to the overlay.
    /// </summary>
    internal async ValueTask EnqueueAsync(FlushIntent intent, CancellationToken ct = default)
    {
        // Write overlay BEFORE enqueue so readers see the data immediately.
        foreach (var (key, bytes) in intent.SerializedEntities)
            Overlay[key] = bytes;

        // Mark deletes as tombstones.
        foreach (var (clrType, id, state) in intent.EntityChanges)
        {
            if (state == EntityState.Deleted)
                Overlay[(clrType.Name, id)] = null;
        }

        await _channel.Writer.WriteAsync(intent, ct);
        _logger.LogDebug("Enqueued flush intent ({EntityCount} entities, {JoinCount} joins)",
            intent.EntityChanges.Count, intent.JoinTableChanges.Count);
    }

    /// <summary>
    /// Reads the next flush intent. Used by <see cref="FlushWorker"/>.
    /// </summary>
    internal ValueTask<FlushIntent> DequeueAsync(CancellationToken ct)
        => _channel.Reader.ReadAsync(ct);

    /// <summary>
    /// Waits for all enqueued intents to be read. Does NOT guarantee they are flushed;
    /// pair with <see cref="FlushWorker"/> completion for that.
    /// </summary>
    internal async Task DrainAsync(CancellationToken ct = default)
    {
        // Complete the writer so the reader knows no more items are coming.
        // We DON'T complete here — DrainAsync should only wait for the queue
        // to become empty, not shut down the channel.
        // Instead, spin-wait until the channel is empty.
        while (_channel.Reader.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(10, ct);
        }

        // After the channel is empty, also wait a tiny bit for the current
        // in-flight flush to finish (FlushWorker will be mid-flush).
        // The worker sets _flushing via the DrainTcs mechanism below.
        if (_drainTcs is not null)
        {
            await _drainTcs.Task.WaitAsync(ct);
            _drainTcs = null;
        }
    }

    private volatile TaskCompletionSource? _drainTcs;

    /// <summary>
    /// Signals that a drain is requested. The worker calls <see cref="CompleteDrain"/>
    /// after finishing its current flush.
    /// </summary>
    internal TaskCompletionSource? RequestDrain()
    {
        if (_channel.Reader.Count == 0 && _drainTcs is null)
            return null;
        _drainTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _drainTcs;
    }

    /// <summary>
    /// Called by the worker after finishing a flush when the queue is empty
    /// and a drain was requested.
    /// </summary>
    internal void CompleteDrain()
    {
        _drainTcs?.TrySetResult();
        _drainTcs = null;
    }

    /// <summary>
    /// Removes overlay entries for the given flush intent. Called by
    /// <see cref="FlushWorker"/> after successful disk write.
    /// </summary>
    internal void RemoveOverlayEntries(FlushIntent intent)
    {
        foreach (var (key, _) in intent.SerializedEntities)
            Overlay.TryRemove(key, out _);

        foreach (var (clrType, id, state) in intent.EntityChanges)
        {
            if (state == EntityState.Deleted)
                Overlay.TryRemove((clrType.Name, id), out _);
        }
    }

    /// <summary>
    /// Signals the channel writer as complete. No more items can be enqueued.
    /// </summary>
    internal void Complete() => _channel.Writer.TryComplete();

    /// <summary>
    /// Whether the reader has completed (channel closed + empty).
    /// </summary>
    internal bool IsCompleted => _channel.Reader.Completion.IsCompleted;

    /// <summary>
    /// Tries to read without blocking. Used in shutdown/test paths.
    /// </summary>
    internal bool TryRead(out FlushIntent? intent)
        => _channel.Reader.TryRead(out intent);

    /// <summary>
    /// Number of items currently buffered.
    /// </summary>
    internal int Count => _channel.Reader.Count;

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        Overlay.Clear();
    }
}
