using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;


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

        var selection = await inner.ResolveAsync(input, context, ct).ConfigureAwait(false);
        scope.Lease = await gate.EnterAsync(selection.ConversationId, ct).ConfigureAwait(false);
        return selection;
    }

    private async ValueTask EndRunAsync(RunScope scope)
    {
        if (ReferenceEquals(_scope.Value, scope))
            _scope.Value = null;
        if (scope.Lease is not null)
            await scope.Lease.DisposeAsync().ConfigureAwait(false);
    }

    internal sealed class RunScope(RunScopedConversationResolver owner) : IAsyncDisposable
    {
        internal IAsyncDisposable? Lease { get; set; }

        public ValueTask DisposeAsync() => owner.EndRunAsync(this);
    }
}
