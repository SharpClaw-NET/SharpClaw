using Microsoft.Extensions.DependencyInjection;

using SharpClaw.Application.Core.Modules.Foreign;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Application.Core.Modules.Sidecar;

public sealed class SidecarReadinessAnalyzer
{
    private static readonly Type[] TaskRuntimeServiceTypes =
    [
        typeof(ITaskStepExecutorExtension),
        typeof(ITaskStepDescriptorProvider),
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

    public ModuleSidecarReadinessReport Analyze(ISharpClawCoreModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        var moduleType = module.GetType();
        var protocolModule = module as IForeignModuleProtocolContractModule;
        var runtimeModule = module as ISharpClawRuntimeModule;
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

        var serviceInventory = InspectServices(module);
        var findings = BuildFindings(contributionInventory, serviceInventory, module);

        return new ModuleSidecarReadinessReport(
            module.Id,
            module.DisplayName,
            module.ToolPrefix,
            moduleType.FullName ?? moduleType.Name,
            moduleType.Assembly.GetName().Name ?? "<unknown>",
            contributionInventory,
            serviceInventory,
            findings);
    }

    public IReadOnlyList<ModuleSidecarReadinessReport> AnalyzeAll(IEnumerable<ISharpClawCoreModule> modules) =>
        [.. modules.Select(Analyze).OrderBy(report => report.ModuleId, StringComparer.Ordinal)];

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
            configureError = $"{ex.GetType().Name}: {ex.Message}";
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

    private static IReadOnlyList<SidecarReadinessFinding> BuildFindings(
        ModuleContributionInventory contributions,
        ModuleServiceInventory services,
        ISharpClawCoreModule module)
    {
        var findings = new List<SidecarReadinessFinding>();

        AddCovered(findings, contributions.ToolCount, "tools.job", "Job-pipeline tools are covered by the current foreign protocol.");
        AddCovered(findings, contributions.InlineToolCount, "tools.inline", "Inline tools are covered by the current foreign protocol.");
        AddCovered(findings, contributions.MapsEndpoints ? 1 : 0, "endpoints.http", "HTTP endpoint proxying is covered by the current foreign protocol.");
        AddCovered(findings, contributions.OverridesHealthCheck ? 1 : 0, "health", "Health checks are covered by the current foreign protocol.");
        AddCovered(findings, contributions.OverridesInitialize ? 1 : 0, "lifecycle.initialize", "Initialize is covered by the current foreign protocol.");
        AddCovered(findings, contributions.OverridesShutdown ? 1 : 0, "lifecycle.shutdown", "Shutdown is covered by the current foreign protocol.");
        AddCovered(findings, contributions.ExportedProtocolContractCount, "contracts.protocol.exports", "Protocol contract exports are covered by the current foreign protocol.");
        AddCovered(findings, contributions.RequiredProtocolContractCount, "contracts.protocol.requirements", "Protocol contract requirements are covered by the current foreign protocol.");
        AddCovered(findings, contributions.ResourceTypeDescriptorCount, "module.resource_descriptors", "Resource descriptors, id lookup, and lookup items are covered by the current foreign protocol.");
        AddCovered(findings, contributions.GlobalFlagDescriptorCount, "module.global_flags", "Global flag descriptors are covered by the current foreign protocol.");
        AddCovered(findings, contributions.HeaderTagCount, "module.header_tags", "Header tag discovery and invocation are covered by the current foreign protocol.");
        AddCovered(findings, contributions.UiContributionCount, "module.ui_contributions", "UI contribution descriptors are covered by the current foreign protocol.");
        AddCovered(findings, contributions.FrontendContributionCount, "module.frontend_contributions", "Frontend contribution descriptors are covered by the current foreign protocol.");
        AddCovered(findings, contributions.CliCommandCount, "module.cli_commands", "CLI command discovery and invocation are covered by the current foreign protocol.");

        if (contributions.ExportedClrContractCount > 0)
            findings.Add(new(
                SidecarReadinessFindingKind.RequiresClrContractBridge,
                "contracts.clr.exports",
                $"{contributions.ExportedClrContractCount} CLR contract export(s) need protocol contract equivalents."));

        var requiredClrContracts = module.RequiredContracts;
        var requiredNonOptionalClrContracts = requiredClrContracts.Count(requirement => !requirement.Optional);
        var requiredOptionalClrContracts = requiredClrContracts.Count - requiredNonOptionalClrContracts;

        if (requiredNonOptionalClrContracts > 0)
            findings.Add(new(
                SidecarReadinessFindingKind.RequiresClrContractBridge,
                "contracts.clr.requirements",
                $"{requiredNonOptionalClrContracts} non-optional CLR contract requirement(s) need protocol contract equivalents."));

        AddCovered(
            findings,
            requiredOptionalClrContracts,
            "contracts.clr.optional_requirements",
            "Optional CLR contract requirements do not block sidecar loading.");

        AddCovered(
            findings,
            contributions.IsTaskParserAware ? 1 : 0,
            "tasks.parser_extension",
            "Task parser extensions are covered by the current foreign protocol.");

        AddCovered(
            findings,
            services.TaskRuntimeServiceRegistrations.Count,
            "tasks.runtime_services",
            "Task runtime services are covered by the current foreign protocol: "
            + string.Join(", ", services.TaskRuntimeServiceRegistrations));

        AddCovered(
            findings,
            services.EventSinkRegistrations.Count,
            "events.sinks",
            "Host event sinks are covered by the current foreign protocol: "
            + string.Join(", ", services.EventSinkRegistrations));

        AddCovered(
            findings,
            services.ProviderPluginRegistrations.Count,
            "providers.plugins",
            "Provider plugin discovery and invocation are covered by the current foreign protocol.");

        if (services.ModuleDbContextTypes.Count > 0)
            findings.Add(new(
                SidecarReadinessFindingKind.RequiresStoragePlan,
                "storage.module_dbcontexts",
                "Module-owned EF contexts need a host-backed storage capability or explicit data migration: "
                + string.Join(", ", services.ModuleDbContextTypes)));

        AddCovered(findings, contributions.OverridesJobCompletionBehavior ? 1 : 0, "jobs.completion_behavior", "Dynamic job completion behavior is covered by the current foreign protocol.");

        if (contributions.OverridesSeedData)
            findings.Add(new(
                SidecarReadinessFindingKind.RequiresManualReview,
                "lifecycle.seed_data",
                "SeedDataAsync needs a sidecar execution point with data-safety review."));

        if (services.ConfigureServicesError is not null)
            findings.Add(new(
                SidecarReadinessFindingKind.RequiresManualReview,
                "di.configure_services",
                $"ConfigureServices inspection failed for module '{module.Id}': {services.ConfigureServicesError}"));

        return [.. findings.OrderBy(finding => finding.Kind).ThenBy(finding => finding.Key, StringComparer.Ordinal)];
    }

    private static void AddCovered(
        List<SidecarReadinessFinding> findings,
        int count,
        string key,
        string detail)
    {
        if (count <= 0)
            return;

        findings.Add(new(SidecarReadinessFindingKind.CoveredByCurrentProtocol, key, detail));
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
