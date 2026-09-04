using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;

/// <summary>
/// Delivers Runtime events through the action boundary and the existing registration
/// storage record collection. Action lifecycle observations stay bounded in memory.
/// </summary>
public sealed class RuntimeEventDeliverySink(IServiceScopeFactory scopeFactory)
    : IKernelEventDeliverySink
{
    private const int ObservationCapacity = 1_024;
    private readonly ConcurrentQueue<KernelQueuedEvent> _observations = new();
    private int _observationCount;

    public bool SupportsDurable => true;

    public ValueTask EnqueueAsync(
        SharpClawEventKey eventKey,
        object envelope,
        EventDelivery delivery,
        CancellationToken cancellationToken) =>
        EnqueueAsync(eventKey, envelope, delivery, cancellationToken, "unknown");

    public async ValueTask EnqueueAsync(
        SharpClawEventKey eventKey,
        object envelope,
        EventDelivery delivery,
        CancellationToken cancellationToken,
        string targetListenerId)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetListenerId);
        cancellationToken.ThrowIfCancellationRequested();

        if (eventKey.Value.StartsWith("action.", StringComparison.Ordinal))
        {
            if (Interlocked.Increment(ref _observationCount) > ObservationCapacity)
            {
                Interlocked.Decrement(ref _observationCount);
                throw new KernelActionExecutionException(
                    "Runtime event observation capacity is full.");
            }

            _observations.Enqueue(new KernelQueuedEvent(
                eventKey,
                envelope,
                delivery,
                DateTimeOffset.UtcNow,
                targetListenerId));
            return;
        }

        var eventId = ReadEventId(envelope);
        using var scope = scopeFactory.CreateScope();
        var boundary = scope.ServiceProvider
            .GetRequiredService<IRuntimeEventActionBoundary>();
        var store = scope.ServiceProvider
            .GetRequiredService<IRuntimeEventOutboxStore>();
        var invocation = new RuntimeEventActionInvocation(
            eventKey,
            eventId,
            delivery,
            "enqueue",
            envelope);

        await boundary.RunEventActionAsync(
            new SharpClawActionKey("event.enqueue"),
            invocation,
            async (effective, ct) =>
            {
                if (effective.Payload is null)
                {
                    throw new KernelActionExecutionException(
                        "The event enqueue action returned no event envelope.");
                }

                await store.EnqueueAsync(
                    new RuntimeEventOutboxMessage(
                        effective.EventId,
                        effective.EventKey,
                        effective.Payload,
                        effective.Delivery,
                        targetListenerId),
                    ct);
                return true;
            },
            cancellationToken);
    }

    internal IReadOnlyList<KernelQueuedEvent> DrainObservations()
    {
        var result = new List<KernelQueuedEvent>();
        while (_observations.TryDequeue(out var item))
        {
            Interlocked.Decrement(ref _observationCount);
            result.Add(item);
        }

        return result;
    }

    private static Guid ReadEventId(object envelope)
    {
        var property = envelope.GetType().GetProperty(nameof(EventEnvelope<object>.EventId));
        if (property?.GetValue(envelope) is Guid eventId && eventId != Guid.Empty)
            return eventId;

        throw new KernelActionExecutionException(
            "The event envelope has no valid event identity.");
    }
}
