using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Core.Modules;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.BLL.Modules;

/// <summary>
/// Host-owned service restrictions applied to in-process module execution.
/// </summary>
internal static class ModuleHostServiceAccess
{
    private static readonly Type[] BlockedTypes =
    [
        typeof(AgentJobService),
        typeof(AgentActionService),
        typeof(ChatService),
        typeof(ModuleService),
        typeof(ModuleRegistry),
        typeof(ModuleLoader),
        typeof(SharpClawDbContext),
        typeof(IServiceScopeFactory),
    ];

    public static IReadOnlyCollection<Type> BlockedServiceTypes => BlockedTypes;

    public static ModuleServiceScope CreateRestrictedScope(
        IServiceProvider inner,
        string moduleId) =>
        new(inner, moduleId, BlockedTypes);
}
