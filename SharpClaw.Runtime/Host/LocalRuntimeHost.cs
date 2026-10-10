using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Contracts.Providers;
using SharpClaw.Persistence;
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
internal static class LocalRuntimeHost
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
        var registrationRoots = PackagedRegistrationRootResolver.Resolve(
            Path.Combine(AppContext.BaseDirectory, "contributions"),
            earlyConfiguration);
        var registrationSet = await PackagedDotNetRegistrationSet.LoadProductionAsync(
            registrationRoots,
            earlyConfiguration,
            cancellationToken).ConfigureAwait(false);
        await using var registrationSetAsyncDisposal = registrationSet.ConfigureAwait(false);

        var runtimeBaseUrl = earlyConfiguration["ASPNETCORE_URLS"]
            ?? "http://127.0.0.1:48923";
        var app = BuildApplication(args, earlyConfiguration, instancePaths, registrationSet);
        await using var appAsyncDisposal = app.ConfigureAwait(false);
        await RunApplicationAsync(args, instancePaths, registrationSet, app,
            runtimeBaseUrl, cancellationToken).ConfigureAwait(false);
    }

    private static WebApplication BuildApplication(
        string[] args,
        IConfiguration earlyConfiguration,
        SharpClawInstancePaths instancePaths,
        PackagedDotNetRegistrationSet registrationSet)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddConfiguration(earlyConfiguration);
        builder.WebHost.UseUrls(
            earlyConfiguration["ASPNETCORE_URLS"]
            ?? "http://127.0.0.1:48923");

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
            SharpClawPersistenceOptions.FromConfiguration(
                earlyConfiguration,
                Path.Combine(instancePaths.DataDirectory, "database")),
            registrationSet.Services);

        return builder.Build();
    }

    private static async Task RunApplicationAsync(
        string[] args,
        SharpClawInstancePaths instancePaths,
        PackagedDotNetRegistrationSet registrationSet,
        WebApplication app,
        string runtimeBaseUrl,
        CancellationToken cancellationToken)
    {
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
                ct => new ValueTask(databaseReadiness.ValidateAsync(ct)),
                cancellationToken).ConfigureAwait(false);
            await registrationSet.ConnectCapabilitiesAsync(app.Services, cancellationToken).ConfigureAwait(false);
            await kernel.StartAsync(
                typeof(LocalRuntimeHost).Assembly.GetName().Version?.ToString() ?? "0.5.0.0",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            runtimeStarted = true;

            await RunRuntimeAsync(args, instancePaths, registrationSet, app, kernel, readiness,
                runtimeBaseUrl, () => appStartAttempted = true, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Preserve the first startup/shutdown failure while all required cleanup operations settle; RunApplicationAsync rethrows it.
        catch (Exception exception)
        {
            failure = ExceptionDispatchInfo.Capture(exception);
        }
#pragma warning restore CA1031
        finally
        {
            failure = await StopAndCleanUpAsync(kernel, cleanup, runtimeStarted, failure).ConfigureAwait(false);
        }

        failure?.Throw();
    }

    private static async Task RunRuntimeAsync(
        string[] args,
        SharpClawInstancePaths instancePaths,
        PackagedDotNetRegistrationSet registrationSet,
        WebApplication app,
        RuntimeKernelAdapter kernel,
        RuntimeReadinessState readiness,
        string runtimeBaseUrl,
        Action markListenerStartAttempted,
        CancellationToken cancellationToken)
    {
        if (RuntimeCliCommandLine.IsRequested(args))
        {
            Environment.ExitCode = await RuntimeCliSession.RunAsync(
                args,
                kernel,
                kernel.Kernel,
                registrationSet.Application,
                Console.Out,
                Console.Error,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        app.UseMiddleware<ApiKeyMiddleware>();
        app.UseWebSockets();
        KernelHostEndpoints.Map(app);
        KernelHostEndpoints.MapModuleSettingsCatalog(app, registrationSet);
        registrationSet.Application.MapEndpoints(app, kernel);
        app.MapHandlers();

        await kernel.RunRuntimeLifecycleActionAsync(
            RuntimeLifecycleActionCatalog.StartBind,
            runtimeBaseUrl,
            async ct =>
            {
                markListenerStartAttempted();
                await app.StartAsync(ct).ConfigureAwait(false);
                readiness.MarkReady();
                instancePaths.PublishDiscoveryEntry(runtimeBaseUrl);
            }, cancellationToken).ConfigureAwait(false);

        await app.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ExceptionDispatchInfo?> StopAndCleanUpAsync(
        RuntimeKernelAdapter kernel,
        RuntimeHostCleanup cleanup,
        bool runtimeStarted,
        ExceptionDispatchInfo? failure)
    {
        if (runtimeStarted)
        {
            try
            {
                await kernel.StopAsync(
                    CancellationToken.None,
                    _ => cleanup.BeginAsync(),
                    _ => cleanup.CompleteAsync()).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Continue later cleanup operations and return the first captured failure for rethrow.
            catch (Exception exception)
            {
                failure ??= ExceptionDispatchInfo.Capture(exception);
            }
#pragma warning restore CA1031
        }

        if (!cleanup.PreparationAttempted)
        {
            try
            {
                await cleanup.BeginAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Continue later cleanup operations and return the first captured failure for rethrow.
            catch (Exception exception)
            {
                failure ??= ExceptionDispatchInfo.Capture(exception);
            }
#pragma warning restore CA1031
        }

        if (!cleanup.CompletionAttempted)
        {
            try
            {
                await cleanup.CompleteAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Return this cleanup failure for rethrow after the earlier required steps have settled.
            catch (Exception exception)
            {
                failure ??= ExceptionDispatchInfo.Capture(exception);
            }
#pragma warning restore CA1031
        }
        return failure;
    }

}
