using System.Runtime.ExceptionServices;
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
    public static async Task RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
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
        await using var moduleSet = await PackagedDotNetModuleSet.LoadProductionAsync(
            Path.Combine(AppContext.BaseDirectory, "modules"),
            earlyConfiguration,
            cancellationToken);

        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddConfiguration(earlyConfiguration);
        builder.WebHost.UseUrls(
            earlyConfiguration["ASPNETCORE_URLS"]
            ?? "http://127.0.0.1:48923");

        var runtimeBaseUrl = earlyConfiguration["ASPNETCORE_URLS"]
            ?? "http://127.0.0.1:48923";

        var encryptionKey = EncryptionKeyResolver.ResolveKey(instancePaths)
            ?? throw new InvalidOperationException(
                "The Runtime application encryption key could not be resolved.");
        var encryptionOptions = new EncryptionOptions
        {
            Key = encryptionKey,
            EncryptProviderKeys = earlyConfiguration.GetValue(
                "Encryption:EncryptProviderKeys",
                defaultValue: true),
        };
        RuntimeHostComposition.RegisterServices(
            builder.Services,
            earlyConfiguration,
            instancePaths,
            encryptionOptions,
            DatabaseProviderOptions.FromConfiguration(
                earlyConfiguration,
                Path.Combine(instancePaths.DataDirectory, "database")),
            moduleSet.Modules);

        await using var app = builder.Build();
        var apiKeyProvider = app.Services.GetRequiredService<ApiKeyProvider>();
        var kernel = app.Services.GetRequiredService<RuntimeKernelAdapter>();
        var readiness = app.Services.GetRequiredService<RuntimeReadinessState>();
        var databaseReadiness = app.Services.GetRequiredService<RuntimeDatabaseReadiness>();
        var runtimeStarted = false;
        var appStartAttempted = false;
        ExceptionDispatchInfo? failure = null;
        var cleanup = new RuntimeHostCleanup(
            readiness.MarkNotReady,
            instancePaths.DeleteDiscoveryEntry,
            apiKeyProvider.Cleanup,
            () => appStartAttempted
                ? new ValueTask(app.StopAsync(CancellationToken.None))
                : ValueTask.CompletedTask);

        try
        {
            await kernel.RunRuntimeLifecycleActionAsync(
                RuntimeLifecycleActionCatalog.StartPrepare,
                null,
                cancellationToken => new ValueTask(
                    databaseReadiness.ValidateAsync(cancellationToken)));
            await moduleSet.ConnectCapabilitiesAsync(app.Services, cancellationToken);
            await kernel.StartAsync("0.1.0-beta");
            runtimeStarted = true;

            if (RuntimeCliCommandLine.IsRequested(args))
            {
                Environment.ExitCode = await RuntimeCliSession.RunAsync(
                    args,
                    kernel,
                    kernel.Kernel,
                    moduleSet.Application,
                    Console.Out,
                    Console.Error,
                    cancellationToken);
                return;
            }

            app.UseMiddleware<ApiKeyMiddleware>();
            app.UseWebSockets();
            KernelHostEndpoints.Map(app);
            moduleSet.Application.MapEndpoints(app, kernel);
            app.MapHandlers();

            await kernel.RunRuntimeLifecycleActionAsync(
                RuntimeLifecycleActionCatalog.StartBind,
                runtimeBaseUrl,
                async cancellationToken =>
                {
                    appStartAttempted = true;
                    await app.StartAsync(cancellationToken);
                    readiness.MarkReady();
                    instancePaths.PublishDiscoveryEntry(runtimeBaseUrl);
                });

            await app.WaitForShutdownAsync();
        }
        catch (Exception exception)
        {
            failure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            if (runtimeStarted)
            {
                try
                {
                    await kernel.StopAsync(
                        CancellationToken.None,
                        _ => cleanup.BeginAsync(),
                        _ => cleanup.CompleteAsync());
                }
                catch (Exception exception)
                {
                    failure ??= ExceptionDispatchInfo.Capture(exception);
                }
            }

            if (!cleanup.PreparationAttempted)
            {
                try
                {
                    await cleanup.BeginAsync();
                }
                catch (Exception exception)
                {
                    failure ??= ExceptionDispatchInfo.Capture(exception);
                }
            }

            if (!cleanup.CompletionAttempted)
            {
                try
                {
                    await cleanup.CompleteAsync();
                }
                catch (Exception exception)
                {
                    failure ??= ExceptionDispatchInfo.Capture(exception);
                }
            }
        }

        failure?.Throw();
    }

}
