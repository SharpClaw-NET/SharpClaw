using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.BLL.Modules;
using SharpClaw.Runtime.Host.Api;
using SharpClaw.Runtime.INF;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.Security;

namespace SharpClaw.Runtime.Host;

/// <summary>Registers one authoritative Runtime service graph.</summary>
internal static class RuntimeHostComposition
{
    public static void RegisterServices(
        IServiceCollection services,
        IConfiguration configuration,
        SharpClawInstancePaths instancePaths,
        EncryptionOptions encryptionOptions,
        DatabaseProviderOptions databaseOptions,
        IEnumerable<ISharpClawModule> modules)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(instancePaths);
        ArgumentNullException.ThrowIfNull(encryptionOptions);
        ArgumentNullException.ThrowIfNull(databaseOptions);
        ArgumentNullException.ThrowIfNull(modules);

        var moduleArray = modules.ToArray();

        services.AddSingleton(configuration);
        services.AddSingleton(instancePaths);
        services.AddSingleton(encryptionOptions);
        services.AddHttpClient();
        services.AddInfrastructure(databaseOptions);
        services.AddSingleton<ApiKeyProvider>();
        services.AddSingleton<RuntimeReadinessState>();
        services.AddSingleton<RuntimeDatabaseReadiness>();
        services.AddSingleton<IRuntimeProviderClientFactory, RuntimeProviderClientFactory>();
        services.AddSingleton<IActionDispatcher>(serviceProvider =>
            serviceProvider.GetRequiredService<RuntimeKernelAdapter>().ActionDispatcher);
        services.AddScoped<IModelRegistrar, RuntimeModelRegistrar>();

        services.AddSingleton<IModuleStorageContractProvider>(
            new RuntimeModuleStorageContractProvider(moduleArray));
        services.AddScoped<IModuleStorageGateway, BundledModuleStorageGateway>();
        services.AddScoped<KernelJobsStore>();

        foreach (var module in moduleArray)
            services.AddSingleton<ISharpClawModule>(module);

        services.AddSingleton<RuntimeKernelAdapter>();
        services.AddScoped<KernelJobsCoordinator>(serviceProvider =>
        {
            var adapter = serviceProvider.GetRequiredService<RuntimeKernelAdapter>();
            var handlers = adapter.Graph.GetService(typeof(IEnumerable<IJobHandler>))
                as IEnumerable<IJobHandler>
                ?? [];
            return new KernelJobsCoordinator(
                adapter.Graph,
                adapter.CoreActionDispatcher,
                serviceProvider.GetRequiredService<KernelJobsStore>(),
                handlers);
        });
        services.AddSingleton<IRuntimePersistenceActionBoundary>(serviceProvider =>
            serviceProvider.GetRequiredService<RuntimeKernelAdapter>());
        services.AddSingleton<IRuntimeTransactionActionBoundary>(serviceProvider =>
            serviceProvider.GetRequiredService<RuntimeKernelAdapter>());
        services.AddSingleton<IRuntimeModuleActionBoundary>(serviceProvider =>
            serviceProvider.GetRequiredService<RuntimeKernelAdapter>());
        services.AddSingleton<IRuntimeEventActionBoundary>(serviceProvider =>
            serviceProvider.GetRequiredService<RuntimeKernelAdapter>());
        services.AddSingleton<IRuntimeEventPublisher>(serviceProvider =>
            serviceProvider.GetRequiredService<RuntimeKernelAdapter>());
        services.AddSingleton<IRuntimeEventActionBoundaryAccessor,
            RuntimeEventActionBoundaryAccessor>();
        services.AddSingleton<IKernelEventDeliverySink, RuntimeEventDeliverySink>();
        services.AddScoped<IRuntimeEventOutboxStore, RuntimeModuleStorageEventOutboxStore>();
        services.AddScoped<IRuntimeEventOutboxService, RuntimeEventOutboxService>();
        services.AddScoped<RuntimePersistenceActionRunner>();
        services.AddScoped<IRuntimeTransactionActionRunnerAccessor,
            RuntimeTransactionActionRunnerAccessor>();
        services.AddScoped<RuntimeTransactionActionRunner>();
        services.AddScoped<IRuntimeTransactionActionRunner>(serviceProvider =>
            serviceProvider.GetRequiredService<RuntimeTransactionActionRunner>());
        services.AddScoped<IRuntimeModuleActionBoundaryAccessor,
            RuntimeModuleActionBoundaryAccessor>();
        services.AddSingleton<DirectChatKernel>(serviceProvider =>
            serviceProvider.GetRequiredService<RuntimeKernelAdapter>().Kernel);
    }
}
