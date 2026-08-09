using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
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
        IEnumerable<ISharpClawModule> modules)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(instancePaths);
        ArgumentNullException.ThrowIfNull(encryptionOptions);
        ArgumentNullException.ThrowIfNull(databaseOptions);
        ArgumentNullException.ThrowIfNull(modules);

        services.AddSingleton(configuration);
        services.AddSingleton(instancePaths);
        services.AddSingleton(encryptionOptions);
        services.AddInfrastructure(databaseOptions);
        services.AddSingleton<ApiKeyProvider>();
        services.AddSingleton<RuntimeReadinessState>();
        services.AddSingleton<RuntimeDatabaseReadiness>();
        services.AddSingleton<IRuntimeProviderClientFactory, RuntimeProviderClientFactory>();
        services.AddSingleton<IConversationStore, EfConversationStore>();

        foreach (var module in modules)
            services.AddSingleton<ISharpClawModule>(module);

        services.AddSingleton<RuntimeKernelAdapter>();
        services.AddSingleton<DirectChatKernel>(serviceProvider =>
            serviceProvider.GetRequiredService<RuntimeKernelAdapter>().Kernel);
    }
}
