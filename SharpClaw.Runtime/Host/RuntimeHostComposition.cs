using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.BLL.Kernel;
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
        IEnumerable<ServiceDescriptor> discoveredServices,
        IEnumerable<ScopedStorageContractDescriptor>? additionalStorageContracts = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(instancePaths);
        ArgumentNullException.ThrowIfNull(encryptionOptions);
        ArgumentNullException.ThrowIfNull(databaseOptions);
        ArgumentNullException.ThrowIfNull(discoveredServices);

        foreach (var descriptor in discoveredServices)
            services.Add(descriptor);
        foreach (var contract in KernelJobsStorage.Contracts)
            services.AddSingleton(contract);
        foreach (var contract in additionalStorageContracts ?? [])
            services.AddSingleton(contract);

        var jobs = new KernelJobsBindings();

        services.AddSingleton(configuration);
        services.AddSingleton(instancePaths);
        services.AddSingleton(encryptionOptions);
        services.AddHttpClient();
        services.AddInfrastructure(databaseOptions);
        services.AddSingleton<ApiKeyProvider>();
        services.AddSingleton<RuntimeReadinessState>();
        services.AddSingleton<RuntimeDatabaseReadiness>();
        services.AddSingleton<KernelExternalAuthoritySessionRegistry>();
        services.AddSingleton<RuntimeHostActionContextAccessor>();
        services.AddSingleton<IHostActionEntry, RuntimeHostActionEntry>();
        services.AddSingleton<IRuntimeProviderClientFactory, RuntimeProviderClientFactory>();
        services.AddSingleton<IActionDispatcher>(serviceProvider =>
            serviceProvider.GetRequiredService<RuntimeKernelAdapter>().ActionDispatcher);
        services.AddScoped<IModelRegistrar, RuntimeModelRegistrar>();

        services.AddSingleton(jobs);
        services.AddSingleton<IStorageContractProvider>(serviceProvider =>
            new RuntimeScopedStorageContractProvider(
                serviceProvider.GetServices<ScopedStorageContractDescriptor>()));
        services.AddScoped<IScopedStorageGateway, ScopedStorageGateway>();
        services.AddScoped<KernelJobsStore>();

        services.AddSingleton<RuntimeKernelAdapter>();
        services.AddScoped<KernelJobsCoordinator>(serviceProvider =>
        {
            var adapter = serviceProvider.GetRequiredService<RuntimeKernelAdapter>();
            return new KernelJobsCoordinator(
                adapter.Graph,
                adapter.CoreActionDispatcher,
                serviceProvider.GetRequiredService<KernelJobsStore>(),
                serviceProvider.GetServices<IJobHandler>());
        });
        services.AddSingleton<IRuntimePersistenceActionBoundary>(serviceProvider =>
            serviceProvider.GetRequiredService<RuntimeKernelAdapter>());
        services.AddSingleton<IRuntimeTransactionActionBoundary>(serviceProvider =>
            serviceProvider.GetRequiredService<RuntimeKernelAdapter>());
        services.AddSingleton<IRuntimeEventActionBoundary>(serviceProvider =>
            serviceProvider.GetRequiredService<RuntimeKernelAdapter>());
        services.AddSingleton<IRuntimeEventPublisher>(serviceProvider =>
            serviceProvider.GetRequiredService<RuntimeKernelAdapter>());
        services.AddSingleton<IRuntimeEventActionBoundaryAccessor,
            RuntimeEventActionBoundaryAccessor>();
        services.AddSingleton<IKernelEventDeliverySink, RuntimeEventDeliverySink>();
        services.AddScoped<IRuntimeEventOutboxStore, RuntimeScopedStorageEventOutboxStore>();
        services.AddScoped<IRuntimeEventOutboxService, RuntimeEventOutboxService>();
        services.AddScoped<RuntimePersistenceActionRunner>();
        services.AddScoped<IRuntimeTransactionActionRunnerAccessor,
            RuntimeTransactionActionRunnerAccessor>();
        services.AddScoped<RuntimeTransactionActionRunner>();
        services.AddScoped<IRuntimeTransactionActionRunner>(serviceProvider =>
            serviceProvider.GetRequiredService<RuntimeTransactionActionRunner>());
        services.AddSingleton<DirectChatKernel>(serviceProvider =>
            serviceProvider.GetRequiredService<RuntimeKernelAdapter>().Kernel);
    }
}
