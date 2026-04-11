using Microsoft.Extensions.DependencyInjection;

using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Core.Modules;

/// <summary>
/// A restricted service scope that only exposes services a module is allowed
/// to resolve. Blocks access to pipeline internals.
/// Implements <see cref="ISupportRequiredService"/> to prevent bypass via cast,
/// and <see cref="IKeyedServiceProvider"/> to block keyed resolution.
/// </summary>
internal sealed class ModuleServiceScope(
    IServiceProvider inner, string moduleId)
    : IServiceProvider, ISupportRequiredService, IKeyedServiceProvider
{
    private static readonly HashSet<Type> BlockedTypes =
    [
        typeof(Services.AgentJobService),
        typeof(Services.AgentActionService),
        typeof(Services.ChatService),
        typeof(Services.ModuleService),
        typeof(ModuleRegistry),
        typeof(ModuleLoader),
        typeof(SharpClawDbContext),         // Modules use their own DbContext (§3.8)
        typeof(IServiceScopeFactory),       // Prevents unrestricted child scope creation
    ];

    public object? GetService(Type serviceType)
    {
        ThrowIfBlocked(serviceType);
        return inner.GetService(serviceType);
    }

    public object GetRequiredService(Type serviceType)
    {
        ThrowIfBlocked(serviceType);
        if (inner is ISupportRequiredService required)
            return required.GetRequiredService(serviceType);
        return inner.GetService(serviceType)
            ?? throw new InvalidOperationException(
                $"No service for type '{serviceType.FullName}' has been registered.");
    }

    public object? GetKeyedService(Type serviceType, object? serviceKey)
    {
        ThrowIfBlocked(serviceType);
        if (inner is IKeyedServiceProvider keyed)
            return keyed.GetKeyedService(serviceType, serviceKey);
        throw new InvalidOperationException(
            "The inner service provider does not support keyed services.");
    }

    public object GetRequiredKeyedService(Type serviceType, object? serviceKey)
    {
        ThrowIfBlocked(serviceType);
        if (inner is IKeyedServiceProvider keyed)
            return keyed.GetRequiredKeyedService(serviceType, serviceKey);
        throw new InvalidOperationException(
            "The inner service provider does not support keyed services.");
    }

    private void ThrowIfBlocked(Type serviceType)
    {
        if (BlockedTypes.Contains(serviceType))
            throw new InvalidOperationException(
                $"Module '{moduleId}' attempted to resolve blocked service " +
                $"'{serviceType.Name}'. Modules cannot access pipeline internals.");
    }
}
