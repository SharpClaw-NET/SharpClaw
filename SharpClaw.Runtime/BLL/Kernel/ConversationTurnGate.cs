using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;

/// <summary>Serializes complete direct-chat turns for one conversation.</summary>
internal sealed class ConversationTurnGate
{
    private readonly Lock _sync = new();
    private readonly Dictionary<Guid, GateEntry> _entries = [];
    private readonly Action? _beforeFinalEntryRemoval;

    internal ConversationTurnGate(Action? beforeFinalEntryRemoval = null)
    {
        _beforeFinalEntryRemoval = beforeFinalEntryRemoval;
    }

    internal int ActiveEntryCount
    {
        get
        {
            lock (_sync)
                return _entries.Count;
        }
    }

    public async ValueTask<IAsyncDisposable> EnterAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        if (conversationId == Guid.Empty)
            throw new ArgumentException("The conversation identifier must not be empty.", nameof(conversationId));

        GateEntry entry;
        lock (_sync)
        {
            entry = _entries.TryGetValue(conversationId, out var existing)
                ? existing
                : _entries[conversationId] = new GateEntry();
            entry.References++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(this, conversationId, entry);
        }
        catch
        {
            ReleaseReference(conversationId, entry);
            throw;
        }
    }

    private void Release(Guid conversationId, GateEntry entry)
    {
        lock (_sync)
        {
            entry.Semaphore.Release();
            ReleaseReferenceLocked(conversationId, entry);
        }
    }

    private void ReleaseReference(Guid conversationId, GateEntry entry)
    {
        lock (_sync)
            ReleaseReferenceLocked(conversationId, entry);
    }

    private void ReleaseReferenceLocked(Guid conversationId, GateEntry entry)
    {
        if (entry.References == 1)
            _beforeFinalEntryRemoval?.Invoke();

        entry.References--;
        if (entry.References == 0 &&
            _entries.TryGetValue(conversationId, out var current) &&
            ReferenceEquals(current, entry))
            _entries.Remove(conversationId);
    }

    private sealed class GateEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int References;
    }

    private sealed class Lease(
        ConversationTurnGate owner,
        Guid conversationId,
        GateEntry entry) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                owner.Release(conversationId, entry);
            return ValueTask.CompletedTask;
        }
    }
}
