using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;

/// <summary>Serializes complete direct-chat turns for one conversation.</summary>
internal sealed class ConversationTurnGate
{
    private readonly object _sync = new();
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
            await entry.Semaphore.WaitAsync(cancellationToken);
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

/// <summary>Holds a conversation gate from Core resolution through persistence.</summary>
internal sealed class RunScopedConversationResolver(
    IConversationResolver inner,
    ConversationTurnGate gate) : IConversationResolver
{
    private readonly AsyncLocal<RunScope?> _scope = new();

    public RunScope BeginRun()
    {
        if (_scope.Value is not null)
            throw new InvalidOperationException("A direct-chat run is already active on this execution flow.");

        var scope = new RunScope(this);
        _scope.Value = scope;
        return scope;
    }

    public async ValueTask<ConversationSelection> ResolveAsync(
        ChatTurnInput input,
        ChatOperationContext context,
        CancellationToken ct)
    {
        var scope = _scope.Value
            ?? throw new InvalidOperationException("Conversation resolution requires an active direct-chat run.");
        if (scope.Lease is not null)
            throw new InvalidOperationException("A direct-chat run resolved more than one conversation.");

        var selection = await inner.ResolveAsync(input, context, ct);
        scope.Lease = await gate.EnterAsync(selection.ConversationId, ct);
        return selection;
    }

    private async ValueTask EndRunAsync(RunScope scope)
    {
        if (ReferenceEquals(_scope.Value, scope))
            _scope.Value = null;
        if (scope.Lease is not null)
            await scope.Lease.DisposeAsync();
    }

    internal sealed class RunScope(RunScopedConversationResolver owner) : IAsyncDisposable
    {
        internal IAsyncDisposable? Lease { get; set; }

        public ValueTask DisposeAsync() => owner.EndRunAsync(this);
    }
}
