using SharpClaw.Core.Kernel;
using SharpClaw.Contracts.Modules;
using SharpClaw.Runtime.BLL.Kernel;

namespace SharpClaw.Runtime.Host;

/// <summary>Routes Runtime event outbox state changes through event actions.</summary>
public sealed class RuntimeEventOutboxService(
    IRuntimeEventActionBoundaryAccessor boundaryAccessor,
    IRuntimeEventOutboxStore store) : IRuntimeEventOutboxService
{
    public ValueTask<IReadOnlyList<RuntimeEventOutboxRecord>> ReadPendingAsync(
        int limit,
        CancellationToken cancellationToken = default) =>
        store.ReadPendingAsync(limit, cancellationToken);

    public ValueTask AcknowledgeAsync(
        RuntimeEventOutboxRecord record,
        CancellationToken cancellationToken = default) =>
        RunTransitionAsync(
            new SharpClawActionKey("event.acknowledge"),
            record,
            new RuntimeEventOutboxTransition(record.RecordKey, null, false),
            static (store, transition, ct) =>
                store.AcknowledgeAsync(transition.RecordKey, ct),
            cancellationToken);

    public ValueTask FailAsync(
        RuntimeEventOutboxRecord record,
        string error,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return RunTransitionAsync(
            new SharpClawActionKey("event.delivery.fail"),
            record,
            new RuntimeEventOutboxTransition(record.RecordKey, error, false),
            static (store, transition, ct) =>
                store.FailAsync(transition.RecordKey, transition.Error!, ct),
            cancellationToken);
    }

    public ValueTask CancelAsync(
        RuntimeEventOutboxRecord record,
        CancellationToken cancellationToken = default) =>
        RunTransitionAsync(
            new SharpClawActionKey("event.delivery.fail"),
            record,
            new RuntimeEventOutboxTransition(record.RecordKey, null, true),
            static (store, transition, ct) =>
                store.CancelAsync(transition.RecordKey, ct),
            cancellationToken);

    private async ValueTask RunTransitionAsync(
        SharpClawActionKey actionKey,
        RuntimeEventOutboxRecord record,
        RuntimeEventOutboxTransition transition,
        Func<IRuntimeEventOutboxStore, RuntimeEventOutboxTransition, CancellationToken, ValueTask> terminal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var invocation = new RuntimeEventActionInvocation(
            record.EventKey,
            record.EventId,
            record.Delivery,
            actionKey.Value,
            transition);
        var boundary = boundaryAccessor.GetRequiredBoundary();
        await boundary.RunEventActionAsync(
            actionKey,
            invocation,
            async (effective, ct) =>
            {
                if (effective.Payload is not RuntimeEventOutboxTransition effectiveTransition ||
                    !string.Equals(
                        effectiveTransition.RecordKey,
                        record.RecordKey,
                        StringComparison.Ordinal))
                {
                    throw new KernelActionExecutionException(
                        "The event outbox action returned an invalid record identity.");
                }

                await terminal(store, effectiveTransition, ct);
                return true;
            },
            cancellationToken);
    }
}
