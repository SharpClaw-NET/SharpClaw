using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;


internal static class RuntimeEventBindings
{
    public static void AddTo(KernelGraphBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddEvent(RuntimeEventDefinitions.Committed, RuntimeEventDefinitions.SourceId);
    }
}
