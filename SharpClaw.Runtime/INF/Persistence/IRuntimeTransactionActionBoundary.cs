using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Runtime.INF.Persistence;


public interface IRuntimeTransactionActionBoundary
{
    ValueTask<RuntimeTransactionActionResult> RunTransactionActionAsync(
        RuntimeTransactionActionInvocation invocation,
        Func<CancellationToken, ValueTask<RuntimeTransactionActionResult>> terminal,
        CancellationToken cancellationToken = default);
}
