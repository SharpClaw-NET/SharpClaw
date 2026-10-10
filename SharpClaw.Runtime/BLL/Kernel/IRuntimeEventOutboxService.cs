using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;


public interface IRuntimeEventOutboxService
{
    ValueTask<IReadOnlyList<RuntimeEventOutboxRecord>> ReadPendingAsync(
        int limit,
        CancellationToken cancellationToken = default);

    ValueTask AcknowledgeAsync(
        RuntimeEventOutboxRecord record,
        CancellationToken cancellationToken = default);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716", Justification = "Preserve the published error parameter name for named-argument and implementation compatibility.")]
    ValueTask FailAsync(
        RuntimeEventOutboxRecord record,
        string error,
        CancellationToken cancellationToken = default);

    ValueTask CancelAsync(
        RuntimeEventOutboxRecord record,
        CancellationToken cancellationToken = default);
}
