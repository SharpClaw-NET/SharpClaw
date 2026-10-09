using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;


public sealed record RuntimeEventActionInvocation(
    SharpClawEventKey EventKey,
    Guid EventId,
    EventDelivery Delivery,
    string Phase,
    object? Payload);
