using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;


public sealed record RuntimeEventPublishResult(
    Guid EventId,
    RuntimeEventPayload Payload,
    EventDelivery Delivery);
