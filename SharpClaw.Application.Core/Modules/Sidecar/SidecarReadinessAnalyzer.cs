using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Contracts.Modules.Foreign;
using SharpClaw.Core.Modules.Sidecar;

namespace SharpClaw.Application.Core.Modules.Sidecar;

public sealed class SidecarReadinessAnalyzer
{
    private static readonly Type[] TaskRuntimeServiceTypes =
    [
        typeof(ITaskOperationExecutor),
        typeof(ITaskOperationDescriptorProvider),
        typeof(ITaskTriggerSource),
        typeof(ITaskMetricProvider),
        typeof(ITaskTriggerAttributeHandler),
        typeof(ITaskTriggerBindingSideEffect),
        typeof(IWebhookRouteRegistrar)
    ];

    private static readonly Type[] EventSinkServiceTypes =
    [
        typeof(ISharpClawEventSink)
    ];

    private readonly SidecarReadinessEvaluator _evaluator = new();

    public ModuleSidecarReadinessReport Analyze(ISharpClawCoreModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        return _evaluator.Evaluate(CollectFacts(module));
    }

    public IReadOnlyList<ModuleSidecarReadinessReport> AnalyzeAll(IEnumerable<ISharpClawCoreModule> modules) =>
        _evaluator.EvaluateAll(modules.Select(CollectFacts));

    private static ModuleSidecarReadinessFacts CollectFacts(ISharpClawCoreModule module)
    {
        var moduleType = module.GetType();
        var protocolModule = module as IForeignModuleProtocolContractModule;
        var runtimeModule = module as ISharpClawRuntimeModule;
        var requiredClrContracts = module.RequiredContracts;
        var requiredNonOptionalClrContracts = requiredClrContracts.Count(requirement => !requirement.Optional);
        var requiredOptionalClrContracts = requiredClrContracts.Count - requiredNonOptionalClrContracts;

        var contributionInventory = new ModuleContributionInventory(
            ToolCount: module.GetToolDefinitions().Count,
            InlineToolCount: module.GetInlineToolDefinitions().Count,
            ResourceTypeDescriptorCount: module.GetResourceTypeDescriptors().Count,
            GlobalFlagDescriptorCount: module.GetGlobalFlagDescriptors().Count,
            HeaderTagCount: module.GetHeaderTags()?.Count ?? 0,
            UiContributionCount: runtimeModule?.GetUiContributions().Count ?? 0,
            FrontendContributionCount: runtimeModule?.GetFrontendContributions().Count ?? 0,
            CliCommandCount: runtimeModule?.GetCliCommands()?.Count ?? 0,
            ExportedClrContractCount: module.ExportedContracts.Count,
            RequiredClrContractCount: module.RequiredContracts.Count,
            RequiredNonOptionalClrContractCount: requiredNonOptionalClrContracts,
            RequiredOptionalClrContractCount: requiredOptionalClrContracts,
            ExportedProtocolContractCount: protocolModule?.ExportedProtocolContracts.Count ?? 0,
            RequiredProtocolContractCount: protocolModule?.RequiredProtocolContracts.Count ?? 0,
            MapsEndpoints: runtimeModule is not null
                && DeclaresPublicInstanceMethod(moduleType, nameof(ISharpClawRuntimeModule.MapEndpoints)),
            OverridesInitialize: DeclaresPublicInstanceMethod(moduleType, nameof(ISharpClawCoreModule.InitializeAsync)),
            OverridesShutdown: DeclaresPublicInstanceMethod(moduleType, nameof(ISharpClawCoreModule.ShutdownAsync)),
            OverridesSeedData: DeclaresPublicInstanceMethod(moduleType, nameof(ISharpClawCoreModule.SeedDataAsync)),
            OverridesHealthCheck: DeclaresPublicInstanceMethod(moduleType, nameof(ISharpClawCoreModule.HealthCheckAsync)),
            OverridesStreamingTools: DeclaresPublicInstanceMethod(moduleType, nameof(ISharpClawCoreModule.ExecuteToolStreamingAsync)),
            OverridesJobCompletionBehavior: DeclaresPublicInstanceMethod(moduleType, nameof(ISharpClawCoreModule.GetJobCompletionBehavior)),
            IsTaskParserAware: module is ITaskParserAware);

        return new ModuleSidecarReadinessFacts(
            module.Id,
            module.DisplayName,
            module.ToolPrefix,
            moduleType.FullName ?? moduleType.Name,
            moduleType.Assembly.GetName().Name ?? "<unknown>",
            contributionInventory,
            InspectServices(module));
    }

    private static ModuleServiceInventory InspectServices(ISharpClawCoreModule module)
    {
        var services = new ServiceCollection();
        string? configureError = null;

        try
        {
            module.ConfigureServices(services);
        }
        catch (Exception ex)
        {
            configureError = $"{module.Id}: {ex.GetType().Name}: {ex.Message}";
        }

        var registrations = services
            .Select(descriptor => new ModuleServiceRegistration(
                FriendlyName(descriptor.ServiceType),
                FriendlyName(descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType()),
                descriptor.Lifetime.ToString(),
                descriptor.ImplementationFactory is not null,
                descriptor.ImplementationInstance is not null))
            .OrderBy(registration => registration.ServiceType, StringComparer.Ordinal)
            .ThenBy(registration => registration.ImplementationType, StringComparer.Ordinal)
            .ThenBy(registration => registration.Lifetime, StringComparer.Ordinal)
            .ToArray();

        var dbContexts = services
            .Where(descriptor => IsDbContextType(descriptor.ServiceType))
            .Select(descriptor => FriendlyName(descriptor.ServiceType))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var providerPlugins = services
            .Where(descriptor => descriptor.ServiceType == typeof(IProviderPlugin))
            .Select(DescribeServiceRegistration)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var taskRuntimeServices = services
            .Where(descriptor => TaskRuntimeServiceTypes.Any(type => descriptor.ServiceType == type))
            .Select(DescribeServiceRegistration)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var eventSinks = services
            .Where(descriptor => EventSinkServiceTypes.Any(type => descriptor.ServiceType == type))
            .Select(DescribeServiceRegistration)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var factories = services
            .Where(descriptor => descriptor.ImplementationFactory is not null)
            .Select(descriptor => FriendlyName(descriptor.ServiceType))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new ModuleServiceInventory(
            registrations,
            dbContexts,
            providerPlugins,
            taskRuntimeServices,
            eventSinks,
            factories,
            configureError);
    }

    private static bool DeclaresPublicInstanceMethod(Type type, string name) =>
        type.GetMethods(System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.DeclaredOnly)
            .Any(method => method.Name == name);

    private static string DescribeServiceRegistration(ServiceDescriptor descriptor)
    {
        var implementation = descriptor.ImplementationType
            ?? descriptor.ImplementationInstance?.GetType();

        return implementation is null
            ? $"{FriendlyName(descriptor.ServiceType)} via factory"
            : $"{FriendlyName(descriptor.ServiceType)} -> {FriendlyName(implementation)}";
    }

    private static string FriendlyName(Type? type)
    {
        if (type is null)
            return "<factory>";

        if (!type.IsGenericType)
            return type.FullName ?? type.Name;

        var genericTypeName = type.GetGenericTypeDefinition().FullName ?? type.Name;
        var tickIndex = genericTypeName.IndexOf('`', StringComparison.Ordinal);
        if (tickIndex >= 0)
            genericTypeName = genericTypeName[..tickIndex];

        return $"{genericTypeName}<{string.Join(", ", type.GetGenericArguments().Select(FriendlyName))}>";
    }

    private static bool IsDbContextType(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType!)
        {
            if (string.Equals(current.FullName, "Microsoft.EntityFrameworkCore.DbContext", StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
