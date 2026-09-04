using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;

/// <summary>Lists every published Runtime event action.</summary>
public static class RuntimeEventActionManifest
{
    public static IReadOnlyList<SharpClawActionKey> Required { get; } =
        SharpClawActionCatalog.Kernel
            .Where(static key => key.Value.StartsWith(
                "event.",
                StringComparison.Ordinal))
            .ToArray();

    public static bool Contains(SharpClawActionKey key) => Required.Contains(key);

    internal static void Validate(KernelGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var missing = Required
            .Where(key => !graph.ContainsAction(key))
            .Select(static key => key.Value)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                "The event action graph is incomplete. Missing actions: " +
                string.Join(", ", missing));
        }
    }
}

public sealed record RuntimeEventActionInvocation(
    SharpClawEventKey EventKey,
    Guid EventId,
    EventDelivery Delivery,
    string Phase,
    object? Payload);

public sealed record RuntimeEventPayload(
    string Name,
    string SourceId,
    string Summary,
    string? DataJson = null)
{
    public RuntimeEventPayload Validate()
    {
        ValidateText(Name, nameof(Name), 128);
        ValidateText(SourceId, nameof(SourceId), 256);
        ValidateText(Summary, nameof(Summary), 2_048);
        if (DataJson is { Length: > 65_536 })
            throw new ArgumentException(
                "The Runtime event data exceeds the 65536-byte limit.",
                nameof(DataJson));
        return this;
    }

    private static void ValidateText(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
            throw new ArgumentException(
                $"The Runtime event {name} is empty or exceeds {maxLength} characters.",
                name);
    }
}

public sealed record RuntimeEventPublishResult(
    Guid EventId,
    RuntimeEventPayload Payload,
    EventDelivery Delivery);

public sealed record RuntimeEventFailure(
    string Phase,
    string Code,
    bool IsCancellation);

public sealed record RuntimeEventOutboxMessage(
    Guid EventId,
    SharpClawEventKey EventKey,
    object Envelope,
    EventDelivery Delivery,
    string TargetListenerId);

public sealed record RuntimeEventOutboxRecord(
    string RecordKey,
    Guid EventId,
    SharpClawEventKey EventKey,
    string EnvelopeJson,
    EventDelivery Delivery,
    string TargetListenerId,
    string State,
    int Attempts,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record RuntimeEventOutboxTransition(
    string RecordKey,
    string? Error,
    bool IsCancellation);

public interface IRuntimeEventOutboxStore
{
    ValueTask EnqueueAsync(
        RuntimeEventOutboxMessage message,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<RuntimeEventOutboxRecord>> ReadPendingAsync(
        int limit,
        CancellationToken cancellationToken = default);

    ValueTask AcknowledgeAsync(
        string recordKey,
        CancellationToken cancellationToken = default);

    ValueTask FailAsync(
        string recordKey,
        string error,
        CancellationToken cancellationToken = default);

    ValueTask CancelAsync(
        string recordKey,
        CancellationToken cancellationToken = default);
}

public interface IRuntimeEventOutboxService
{
    ValueTask<IReadOnlyList<RuntimeEventOutboxRecord>> ReadPendingAsync(
        int limit,
        CancellationToken cancellationToken = default);

    ValueTask AcknowledgeAsync(
        RuntimeEventOutboxRecord record,
        CancellationToken cancellationToken = default);

    ValueTask FailAsync(
        RuntimeEventOutboxRecord record,
        string error,
        CancellationToken cancellationToken = default);

    ValueTask CancelAsync(
        RuntimeEventOutboxRecord record,
        CancellationToken cancellationToken = default);
}

public interface IRuntimeEventActionBoundary
{
    ValueTask<TResult> RunEventActionAsync<TResult>(
        SharpClawActionKey actionKey,
        RuntimeEventActionInvocation invocation,
        Func<RuntimeEventActionInvocation, CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken cancellationToken = default);
}

public interface IRuntimeEventActionBoundaryAccessor
{
    IRuntimeEventActionBoundary GetRequiredBoundary();
}

public interface IRuntimeEventPublisher
{
    ValueTask<RuntimeEventPublishResult> PublishAsync(
        RuntimeEventPayload payload,
        EventDelivery delivery = EventDelivery.Inline,
        CancellationToken cancellationToken = default);
}

internal static class RuntimeEventDefinitions
{
    public const string SourceId = "sharpclaw.runtime.events";
    public static readonly SharpClawEventKey CommittedKey = new("runtime.event");

    public static EventDescriptor<RuntimeEventPayload> Committed { get; } =
        new(
            CommittedKey,
            1,
            "runtime.event",
            EventInterceptionCapabilities.Inspect |
            EventInterceptionCapabilities.Replace |
            EventInterceptionCapabilities.Cancel |
            EventInterceptionCapabilities.Observe,
            false,
            false)
        {
            ProtocolVersionRange = ContractVersionRange.Exact(1),
            DeliveryClasses =
            [EventDelivery.Inline, EventDelivery.Queued, EventDelivery.Durable]
        };
}

internal static class RuntimeEventBindings
{
    public static void AddTo(KernelGraphBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddEvent(RuntimeEventDefinitions.Committed, RuntimeEventDefinitions.SourceId);
    }
}
