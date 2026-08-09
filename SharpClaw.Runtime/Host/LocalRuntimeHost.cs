using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Contracts.Providers;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.Host.Api;
using SharpClaw.Runtime.Host.Routing;
using SharpClaw.Runtime.INF.Configuration;
using SharpClaw.Runtime.INF;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.Security;

namespace SharpClaw.Runtime.Host;

/// <summary>Builds and runs the authoritative local Runtime composition.</summary>
public static class LocalRuntimeHost
{
    public static async Task RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var instancePaths = RuntimeInstancePathResolver.CreateBackend();
        instancePaths.EnsureDirectories();
        instancePaths.CleanupStaleDiscoveryEntries(TimeSpan.FromMinutes(2));
        using var instanceLock = new SharpClawInstanceLock(instancePaths);

        var earlyConfiguration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddLocalEnvironment(isDevelopment: false, instancePaths)
            .Build();

        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddConfiguration(earlyConfiguration);
        builder.WebHost.UseUrls(
            earlyConfiguration["ASPNETCORE_URLS"]
            ?? "http://127.0.0.1:48923");

        var runtimeBaseUrl = earlyConfiguration["ASPNETCORE_URLS"]
            ?? "http://127.0.0.1:48923";

        builder.Services.AddSingleton(instancePaths);
        var encryptionKey = EncryptionKeyResolver.ResolveKey(instancePaths)
            ?? throw new InvalidOperationException(
                "The Runtime application encryption key could not be resolved.");
        builder.Services.AddSingleton(new EncryptionOptions
        {
            Key = encryptionKey,
            EncryptProviderKeys = earlyConfiguration.GetValue(
                "Encryption:EncryptProviderKeys",
                defaultValue: true),
        });
        builder.Services.AddInfrastructure(
            DatabaseProviderOptions.FromConfiguration(
                earlyConfiguration,
                Path.Combine(instancePaths.DataDirectory, "database")));
        builder.Services.AddSingleton<ApiKeyProvider>();
        builder.Services.AddSingleton<IRuntimeProviderClientFactory, RuntimeProviderClientFactory>();
        builder.Services.AddSingleton<IConversationStore, EfConversationStore>();
        builder.Services.AddSingleton<RuntimeKernelAdapter>();
        builder.Services.AddSingleton<DirectChatKernel>(services =>
            services.GetRequiredService<RuntimeKernelAdapter>().Kernel);

        var app = builder.Build();
        var apiKeyProvider = app.Services.GetRequiredService<ApiKeyProvider>();
        var kernel = app.Services.GetRequiredService<RuntimeKernelAdapter>();
        await kernel.StartAsync("0.1.0-beta");
        instancePaths.PublishDiscoveryEntry(runtimeBaseUrl);
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            kernel.StopAsync().AsTask().GetAwaiter().GetResult();
            apiKeyProvider.Cleanup();
            instancePaths.DeleteDiscoveryEntry();
        });

        app.UseMiddleware<ApiKeyMiddleware>();
        KernelHostEndpoints.Map(app);
        app.MapHandlers();

        await app.RunAsync();
    }

}
