using System.Collections.Concurrent;
using System.Threading.Channels;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Services;

/// <summary>
/// Per-thread pub/sub signal + sequential-processing lock.
/// Registered as a singleton so all scoped <see cref="ChatService"/>
/// instances share the same locks and subscriber sets.
/// </summary>
public sealed class ThreadActivitySignal
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentBag<Channel<ThreadActivityEvent>>> _subscribers = new();

    /// <summary>
    /// Acquires the per-thread processing lock. Only one caller can hold
    /// the lock at a time, ensuring sequential message processing within
    /// a single thread.
    /// </summary>
    public async Task<IDisposable> AcquireThreadLockAsync(Guid threadId, CancellationToken ct)
    {
        var sem = _locks.GetOrAdd(threadId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        return new SemaphoreReleaser(sem);
    }

    /// <summary>
    /// Subscribes to activity events for the given thread.
    /// Dispose the returned subscription to unsubscribe.
    /// </summary>
    public ThreadActivitySubscription Subscribe(Guid threadId)
    {
        var channel = Channel.CreateBounded<ThreadActivityEvent>(
            new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = false,
                SingleReader = true,
            });

        var bag = _subscribers.GetOrAdd(threadId, _ => []);
        bag.Add(channel);

        return new ThreadActivitySubscription(channel.Reader, () =>
        {
            // Remove this channel from the subscriber bag
            if (_subscribers.TryGetValue(threadId, out var b))
            {
                var remaining = new ConcurrentBag<Channel<ThreadActivityEvent>>();
                foreach (var ch in b)
                {
                    if (ch != channel) remaining.Add(ch);
                }
                _subscribers.TryUpdate(threadId, remaining, b);
            }
            channel.Writer.TryComplete();
        });
    }

    /// <summary>
    /// Publishes an event to all subscribers watching the given thread.
    /// </summary>
    public void Publish(Guid threadId, ThreadActivityEvent evt)
    {
        if (!_subscribers.TryGetValue(threadId, out var bag)) return;
        foreach (var ch in bag)
            ch.Writer.TryWrite(evt);
    }

    private sealed class SemaphoreReleaser(SemaphoreSlim sem) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                sem.Release();
        }
    }
}

/// <summary>Activity event types broadcast to watch subscribers.</summary>
public enum ThreadActivityEventType
{
    /// <summary>A message is being processed (lock acquired).</summary>
    Processing,
    /// <summary>New messages were saved — subscribers should refresh.</summary>
    NewMessages,
}

/// <summary>A single thread activity event.</summary>
public sealed record ThreadActivityEvent(ThreadActivityEventType Type, ChatClientType? ClientType = null);

/// <summary>
/// A subscription to thread activity events. Dispose to unsubscribe.
/// </summary>
public sealed class ThreadActivitySubscription : IDisposable
{
    private readonly Action _cleanup;
    private int _disposed;

    public ChannelReader<ThreadActivityEvent> Reader { get; }

    internal ThreadActivitySubscription(ChannelReader<ThreadActivityEvent> reader, Action cleanup)
    {
        Reader = reader;
        _cleanup = cleanup;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _cleanup();
    }
}
