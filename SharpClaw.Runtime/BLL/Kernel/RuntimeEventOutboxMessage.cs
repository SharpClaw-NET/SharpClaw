using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;


public sealed record RuntimeEventOutboxMessage(
    Guid EventId,
    SharpClawEventKey EventKey,
    object Envelope,
    EventDelivery Delivery,
    string TargetListenerId);
