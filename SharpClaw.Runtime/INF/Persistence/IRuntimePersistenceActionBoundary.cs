using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Persistence;

namespace SharpClaw.Runtime.INF.Persistence;


public interface IRuntimePersistenceActionBoundary
{
    ValueTask RunPersistenceActionAsync(
        RuntimePersistenceActionInvocation invocation,
        Func<CancellationToken, ValueTask<int>> terminal,
        CancellationToken cancellationToken = default);
}
