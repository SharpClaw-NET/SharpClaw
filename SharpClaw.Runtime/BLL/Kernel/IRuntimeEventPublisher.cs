using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;


public interface IRuntimeEventPublisher
{
    ValueTask<RuntimeEventPublishResult> PublishAsync(
        RuntimeEventPayload payload,
        EventDelivery delivery = EventDelivery.Inline,
        CancellationToken cancellationToken = default);
}
