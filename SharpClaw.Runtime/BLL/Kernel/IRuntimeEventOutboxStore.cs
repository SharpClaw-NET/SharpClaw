using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;


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
