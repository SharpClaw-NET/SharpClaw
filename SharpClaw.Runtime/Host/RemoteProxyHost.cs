using System.Net;
using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.Host.Api;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Runtime.Host;

internal static class RemoteProxyHost
{
    internal static async Task RunAsync(
        string[] args,
        IConfiguration configuration,
        SharpClawInstancePaths instancePaths,
        RemoteGatewayConnection connection,
        CancellationToken cancellationToken)
    {
        if (RuntimeCliCommandLine.IsRequested(args))
            throw new InvalidOperationException("Local command execution is unavailable while the remote backend is enabled.");
        var app = BuildApplication(args, configuration, instancePaths, connection);
        await using var appAsyncDisposal = app.ConfigureAwait(false);
        await RunApplicationAsync(app, instancePaths, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task RunApplicationAsync(
        WebApplication app,
        SharpClawInstancePaths instancePaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(instancePaths);
        var actions = app.Services.GetRequiredService<RemoteProxyActionBoundary>();
        var executionContext = CreateHostExecutionContext();
        ApiKeyProvider? keys = null;
        var startAttempted = false;
        var cleanup = new RuntimeHostCleanup(static () => { }, instancePaths.DeleteDiscoveryEntry,
            () => keys?.Cleanup(), () => startAttempted ? new ValueTask(app.StopAsync(CancellationToken.None)) : ValueTask.CompletedTask);
        ExceptionDispatchInfo? failure = null;
        try
        {
            await PrepareStartupAsync(app, actions, executionContext,
                provider => keys = provider, cancellationToken).ConfigureAwait(false);
            await actions.RunLifecycleAsync(executionContext, RuntimeLifecycleActionCatalog.StartBind,
                CreateLifecycleInvocation(RuntimeLifecycleActionCatalog.StartBind), async token =>
                {
                    startAttempted = true;
                    await app.StartAsync(token).ConfigureAwait(false);
                    instancePaths.PublishDiscoveryEntry(app.Urls.First());
                }, cancellationToken).ConfigureAwait(false);
            await WaitForStoppingAsync(app.Lifetime, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Preserve the startup/request failure while listener, discovery and local keys are all cleaned up; rethrow below.
        catch (Exception exception)
        {
            failure = ExceptionDispatchInfo.Capture(exception);
        }
#pragma warning restore CA1031
        finally
        {
            failure = await StopAndCleanUpAsync(actions, executionContext, cleanup, failure).ConfigureAwait(false);
        }
        failure?.Throw();
    }

    private static async ValueTask PrepareStartupAsync(
        WebApplication app,
        RemoteProxyActionBoundary actions,
        KernelActionExecutionContext executionContext,
        Action<ApiKeyProvider> retainKeys,
        CancellationToken cancellationToken)
    {
        await actions.RunLifecycleAsync(executionContext, RuntimeLifecycleActionCatalog.StartPrepare,
            CreateLifecycleInvocation(RuntimeLifecycleActionCatalog.StartPrepare), static token =>
            {
                token.ThrowIfCancellationRequested();
                return ValueTask.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
        await actions.RunLifecycleAsync(executionContext, RuntimeLifecycleActionCatalog.StartConfigure,
            CreateLifecycleInvocation(RuntimeLifecycleActionCatalog.StartConfigure), token =>
            {
                token.ThrowIfCancellationRequested();
                retainKeys(app.Services.GetRequiredService<ApiKeyProvider>());
                return ValueTask.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
    }

    internal static WebApplication BuildApplication(
        string[] args,
        IConfiguration configuration,
        SharpClawInstancePaths instancePaths,
        RemoteGatewayConnection connection,
        Action<IServiceCollection>? configureServices = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(instancePaths);
        ArgumentNullException.ThrowIfNull(connection);
        var listenUrls = configuration["ASPNETCORE_URLS"] ?? "http://127.0.0.1:48923";
        ValidateLoopbackListeners(listenUrls, configuration);
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddConfiguration(configuration);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        builder.WebHost.UseUrls(listenUrls);
        RegisterServices(builder.Services, instancePaths, connection);
        configureServices?.Invoke(builder.Services);
        var app = builder.Build();
        app.UseMiddleware<ExceptionHandlingMiddleware>();
        app.UseWebSockets();
        app.Run(context => app.Services.GetRequiredService<RemoteProxyRequestHandler>().InvokeAsync(context));
        return app;
    }

    private static void RegisterServices(
        IServiceCollection services,
        SharpClawInstancePaths instancePaths,
        RemoteGatewayConnection connection)
    {
        services.AddSingleton(instancePaths);
        services.AddSingleton(connection);
        services.AddSingleton<ApiKeyProvider>();
        services.AddSingleton(static provider => new KernelGraphBuilder().Compile(provider));
        services.AddSingleton(static provider => new KernelActionDispatcher(
            provider.GetRequiredService<KernelGraph>(), CreateHostExecutionContext()));
        services.AddSingleton<RemoteProxyActionBoundary>();
        services.AddSingleton(static provider => new RemoteGatewayProxy(
            provider.GetRequiredService<RemoteGatewayConnection>(),
            provider.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
        services.AddSingleton<RemoteProxyRequestHandler>();
    }

    private static void ValidateLoopbackListeners(string listenUrls, IConfiguration configuration)
    {
        var urls = listenUrls.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (urls.Length == 0 || configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
            throw new InvalidOperationException("The remote backend proxy requires explicit loopback listeners.");
        foreach (var url in urls)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || !(uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.Ordinal)
                    || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.Ordinal))
                || !(uri.DnsSafeHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                    || (IPAddress.TryParse(uri.DnsSafeHost, out var address) && IPAddress.IsLoopback(address)))
                || !uri.AbsolutePath.Equals("/", StringComparison.Ordinal) || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidOperationException("The remote backend proxy can bind only to loopback addresses.");
        }
    }

    private static async Task<ExceptionDispatchInfo?> StopAndCleanUpAsync(
        RemoteProxyActionBoundary actions,
        KernelActionExecutionContext executionContext,
        RuntimeHostCleanup cleanup,
        ExceptionDispatchInfo? failure)
    {
        failure = await CaptureCleanupFailureAsync(() => actions.RunLifecycleAsync(executionContext,
            RuntimeLifecycleActionCatalog.StopPrepare, CreateLifecycleInvocation(RuntimeLifecycleActionCatalog.StopPrepare),
            _ => cleanup.BeginAsync(), CancellationToken.None), failure).ConfigureAwait(false);
        failure = await CaptureCleanupFailureAsync(cleanup.BeginAsync, failure).ConfigureAwait(false);
        failure = await CaptureCleanupFailureAsync(() => actions.RunLifecycleAsync(executionContext,
            RuntimeLifecycleActionCatalog.StopComplete, CreateLifecycleInvocation(RuntimeLifecycleActionCatalog.StopComplete),
            _ => cleanup.CompleteAsync(), CancellationToken.None), failure).ConfigureAwait(false);
        return await CaptureCleanupFailureAsync(cleanup.CompleteAsync, failure).ConfigureAwait(false);
    }

    private static async ValueTask<ExceptionDispatchInfo?> CaptureCleanupFailureAsync(
        Func<ValueTask> operation,
        ExceptionDispatchInfo? failure)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Join all owned cleanup steps and preserve the first failure for rethrow by RunAsync.
        catch (Exception exception)
        {
            failure ??= ExceptionDispatchInfo.Capture(exception);
        }
#pragma warning restore CA1031
        return failure;
    }

    private static async Task WaitForStoppingAsync(IHostApplicationLifetime lifetime, CancellationToken cancellationToken)
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var stoppingRegistration = lifetime.ApplicationStopping.Register(() => stopped.TrySetResult()).ConfigureAwait(false);
        await using var cancellationRegistration = cancellationToken.Register(lifetime.StopApplication).ConfigureAwait(false);
#pragma warning disable VSTHRD003 // This completion belongs to process shutdown signals and has no UI or JoinableTask dependency.
        await stopped.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
    }

    private static KernelActionExecutionContext CreateHostExecutionContext() =>
        new(RequestPrincipal.Anonymous, ExtensionFeatureSet.Empty, Guid.NewGuid(), Guid.NewGuid());

    private static RemoteProxyLifecycleInvocation CreateLifecycleInvocation(SharpClawActionKey key) =>
        new(key.Value, "gateway-proxy");
}
