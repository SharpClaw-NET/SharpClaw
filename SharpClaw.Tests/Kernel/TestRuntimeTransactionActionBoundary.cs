using SharpClaw.Contracts.Modules;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Tests.Kernel;

internal sealed class TestRuntimeTransactionActionBoundary : IRuntimeTransactionActionBoundary
{
    public ValueTask<RuntimeTransactionActionResult> RunTransactionActionAsync(
        RuntimeTransactionActionInvocation invocation,
        Func<CancellationToken, ValueTask<RuntimeTransactionActionResult>> terminal,
        CancellationToken cancellationToken = default) =>
        terminal(cancellationToken);
}

internal sealed class TestRuntimeModuleActionBoundary : IRuntimeModuleActionBoundary
{
    public ValueTask<TResult> RunModuleActionAsync<TResult>(
        SharpClawActionKey actionKey,
        object? payload,
        Func<object?, CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken cancellationToken = default) =>
        terminal(payload, cancellationToken);
}

internal sealed class TestRuntimeTransactionActionRunnerAccessor(
    RuntimeTransactionActionRunner runner) : IRuntimeTransactionActionRunnerAccessor
{
    public RuntimeTransactionActionRunner GetRequiredRunner() => runner;
}
