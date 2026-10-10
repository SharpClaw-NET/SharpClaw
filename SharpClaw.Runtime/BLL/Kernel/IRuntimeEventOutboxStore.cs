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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716", Justification = "Preserve the published error parameter name for named-argument and implementation compatibility.")]
    ValueTask FailAsync(
        string recordKey,
        string error,
        CancellationToken cancellationToken = default);

    ValueTask CancelAsync(
        string recordKey,
        CancellationToken cancellationToken = default);
}
