using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;


public interface IRuntimeEventActionBoundaryAccessor
{
    IRuntimeEventActionBoundary GetRequiredBoundary();
}
