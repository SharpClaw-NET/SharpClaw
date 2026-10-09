using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;


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
