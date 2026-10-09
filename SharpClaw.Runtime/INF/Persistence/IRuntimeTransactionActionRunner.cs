using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Runtime.INF.Persistence;


public interface IRuntimeTransactionActionRunner
{
    Task<IDbContextTransaction?> BeginSerializableAsync(CancellationToken cancellationToken = default);

    Task CommitAsync(
        IDbContextTransaction transaction,
        CancellationToken cancellationToken = default);

    Task RollbackAsync(
        IDbContextTransaction transaction,
        CancellationToken cancellationToken = default);
}
