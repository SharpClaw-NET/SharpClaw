using SharpClaw.Contracts.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace SharpClaw.Runtime.BLL.Kernel;

/// <summary>Dispatches Runtime module lifecycle work through the singleton kernel.</summary>
public interface IRuntimeModuleActionBoundary
{
    ValueTask<TResult> RunModuleActionAsync<TResult>(
        SharpClawActionKey actionKey,
        object? payload,
        Func<object?, CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken cancellationToken = default);
}

public sealed record RuntimeModuleActionInvocation(string ModuleId, string Operation);

/// <summary>Resolves the module action boundary only after graph construction completes.</summary>
public interface IRuntimeModuleActionBoundaryAccessor
{
    IRuntimeModuleActionBoundary GetRequiredBoundary();
}

public sealed class RuntimeModuleActionBoundaryAccessor(IServiceProvider services)
    : IRuntimeModuleActionBoundaryAccessor
{
    public IRuntimeModuleActionBoundary GetRequiredBoundary() =>
        services.GetRequiredService<IRuntimeModuleActionBoundary>();
}
