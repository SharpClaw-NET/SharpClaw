using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;


public interface IRuntimeEventActionBoundary
{
    ValueTask<TResult> RunEventActionAsync<TResult>(
        SharpClawActionKey actionKey,
        RuntimeEventActionInvocation invocation,
        Func<RuntimeEventActionInvocation, CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken cancellationToken = default);
}
