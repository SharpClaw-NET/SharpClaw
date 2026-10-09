using SharpClaw.Contracts.Kernel;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Tests.Kernel;


internal sealed class TestRuntimeTransactionActionRunnerAccessor(
    RuntimeTransactionActionRunner runner) : IRuntimeTransactionActionRunnerAccessor
{
    public RuntimeTransactionActionRunner GetRequiredRunner() => runner;
}
