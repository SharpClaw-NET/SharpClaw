using Microsoft.Extensions.DependencyInjection;

namespace SharpClaw.Runtime.BLL.Kernel;

public sealed class RuntimeEventActionBoundaryAccessor(IServiceProvider services)
    : IRuntimeEventActionBoundaryAccessor
{
    public IRuntimeEventActionBoundary GetRequiredBoundary() =>
        services.GetRequiredService<IRuntimeEventActionBoundary>();
}
