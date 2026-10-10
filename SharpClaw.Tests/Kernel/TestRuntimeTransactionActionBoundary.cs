using SharpClaw.Contracts.Kernel;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Tests.Kernel;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "The module registration supplies this type to Microsoft DI, which activates its constructor through reflection.")]
internal sealed class TestRuntimeTransactionActionBoundary : IRuntimeTransactionActionBoundary
{
    public ValueTask<RuntimeTransactionActionResult> RunTransactionActionAsync(
        RuntimeTransactionActionInvocation invocation,
        Func<CancellationToken, ValueTask<RuntimeTransactionActionResult>> terminal,
        CancellationToken cancellationToken = default) =>
        terminal(cancellationToken);
}
