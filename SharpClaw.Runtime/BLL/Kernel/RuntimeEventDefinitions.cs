using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;


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
