using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Runtime.INF.Persistence;


public sealed class RuntimeTransactionActionRunnerAccessor(IServiceProvider services)
    : IRuntimeTransactionActionRunnerAccessor
{
    public RuntimeTransactionActionRunner GetRequiredRunner() =>
        services.GetRequiredService<RuntimeTransactionActionRunner>();
}
