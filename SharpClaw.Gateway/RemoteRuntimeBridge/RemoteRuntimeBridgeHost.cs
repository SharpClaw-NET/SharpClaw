using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.RemoteRuntimeBridge;
using Yarp.ReverseProxy.Forwarder;

namespace SharpClaw.Gateway.RemoteRuntimeBridge;

internal interface IRemoteRuntimeBridgeListener;

internal sealed class RemoteRuntimeBridgeListener : IRemoteRuntimeBridgeListener;

internal sealed record RemoteRuntimeBridgeTarget(
    string GatewayInstanceId,
    string AuthoritativeRuntimeInstanceId,
    string TargetBaseUrl,
    string AuthoritativeApiKey);

internal static class RemoteRuntimeBridgeHost
{
    public static void RegisterServices(
        IServiceCollection services,
        RemoteRuntimeBridgeOptions options,
        SharpClawInstancePaths gatewayPaths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(gatewayPaths);

        if (!options.Enabled)
            return;

        services.AddSingleton(gatewayPaths);
        services.AddSingleton<RemoteRuntimePairingStore>(
            _ => RemoteRuntimePairingStore.Create(gatewayPaths));
        services.AddSingleton<IRemoteRuntimeBridgeListener, RemoteRuntimeBridgeListener>();
        services.AddHostedService<RemoteRuntimeBridgeHostedService>();
    }

    public static WebApplication Build(
        string[] args,
        RemoteRuntimeBridgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        throw new InvalidOperationException(
            "The Runtime bridge cannot bind from configuration alone. An active approved pairing and selected Runtime are required.");
    }

    internal static WebApplication Build(
        string[] args,
        RemoteRuntimeBridgeOptions options,
        RemoteRuntimePairingStore pairingStore,
        RemoteRuntimeBridgeTarget target)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pairingStore);
        ArgumentNullException.ThrowIfNull(target);

        if (!options.Enabled)
            throw new InvalidOperationException("The Runtime bridge is disabled.");

        if (string.IsNullOrWhiteSpace(options.ServerCertificatePath)
            || !File.Exists(options.ServerCertificatePath))
        {
            throw new InvalidOperationException(
                "The Runtime bridge requires a configured server certificate before binding.");
        }

        var listenUri = new Uri(options.ListenUrl, UriKind.Absolute);
        if (!string.Equals(listenUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The Runtime bridge listener must use HTTPS.");
        }

        var builder = WebApplication.CreateSlimBuilder(args);
        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.ListenAnyIP(
                listenUri.Port,
                listenOptions =>
                {
                    listenOptions.UseHttps(options.ServerCertificatePath, null, httpsOptions =>
                    {
                        httpsOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                        httpsOptions.ClientCertificateValidation = static (_, _, _) => true;
                    });
                });
        });
        builder.Services.AddReverseProxy();

        var bridgeApp = builder.Build();
        bridgeApp.Use(async (context, next) =>
        {
            var certificate = await context.Connection.GetClientCertificateAsync(
                context.RequestAborted);
            if (certificate is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            try
            {
                await pairingStore.RequireActiveCertificateAsync(
                    certificate,
                    target.GatewayInstanceId,
                    target.AuthoritativeRuntimeInstanceId,
                    context.RequestAborted);
            }
            catch (RemoteRuntimePairingException)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context);
        });

        bridgeApp.MapForwarder(
            "/{**catch-all}",
            target.TargetBaseUrl,
            ForwarderRequestConfig.Empty,
            new RemoteRuntimeBridgeTransformer(target.AuthoritativeApiKey));
        return bridgeApp;
    }

    private sealed class RemoteRuntimeBridgeTransformer(string authoritativeApiKey) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(
            HttpContext httpContext,
            HttpRequestMessage proxyRequest,
            string destinationPrefix,
            CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(
                httpContext,
                proxyRequest,
                destinationPrefix,
                cancellationToken);
            proxyRequest.Headers.Remove("X-Api-Key");
            proxyRequest.Headers.Remove("X-Gateway-Token");
            proxyRequest.Headers.Remove("X-Forwarded-For");
            proxyRequest.Headers.Remove("X-Forwarded-Host");
            proxyRequest.Headers.Remove("X-Forwarded-Proto");
            proxyRequest.Headers.Remove("Forwarded");
            proxyRequest.Headers.TryAddWithoutValidation("X-Api-Key", authoritativeApiKey);
        }
    }
}

internal sealed class RemoteRuntimeBridgeHostedService(
    RemoteRuntimeBridgeOptions options,
    RemoteRuntimePairingStore pairingStore,
    SharpClawInstancePaths gatewayPaths,
    ILogger<RemoteRuntimeBridgeHostedService> logger) : IHostedService, IAsyncDisposable
{
    private WebApplication? _bridgeApp;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var target = RemoteRuntimeBridgeTargetResolver.Resolve(gatewayPaths);
        await pairingStore.RequireActiveTargetAsync(
            target.GatewayInstanceId,
            target.AuthoritativeRuntimeInstanceId,
            cancellationToken);
        _bridgeApp = RemoteRuntimeBridgeHost.Build(
            [],
            options,
            pairingStore,
            target);
        await _bridgeApp.StartAsync(cancellationToken);
        logger.LogInformation("Remote Runtime bridge listener started on {ListenUrl}.", options.ListenUrl);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_bridgeApp is null)
            return;

        await _bridgeApp.StopAsync(cancellationToken);
        await _bridgeApp.DisposeAsync();
        _bridgeApp = null;
    }

    public ValueTask DisposeAsync()
        => _bridgeApp is null ? ValueTask.CompletedTask : _bridgeApp.DisposeAsync();
}

internal static class RemoteRuntimeBridgeTargetResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static RemoteRuntimeBridgeTarget Resolve(SharpClawInstancePaths gatewayPaths)
    {
        ArgumentNullException.ThrowIfNull(gatewayPaths);
        var manifest = gatewayPaths.Manifest;
        if (string.IsNullOrWhiteSpace(manifest.SelectedBackendInstanceId))
        {
            throw new InvalidOperationException(
                "The Runtime bridge requires a selected authoritative Runtime instance.");
        }

        var entry = EnumerateBackendEntries(gatewayPaths.SharedRoot)
            .SingleOrDefault(candidate => string.Equals(
                candidate.InstanceId,
                manifest.SelectedBackendInstanceId,
                StringComparison.Ordinal));
        if (entry is null)
        {
            throw new InvalidOperationException(
                "The selected authoritative Runtime has no valid discovery entry.");
        }

        if (string.IsNullOrWhiteSpace(entry.BaseUrl)
            || string.IsNullOrWhiteSpace(entry.ApiKeyFilePath)
            || !File.Exists(entry.ApiKeyFilePath))
        {
            throw new InvalidOperationException(
                "The selected authoritative Runtime discovery entry has no usable target credentials.");
        }

        var apiKey = File.ReadAllText(entry.ApiKeyFilePath).Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "The selected authoritative Runtime API key is empty.");
        }

        return new RemoteRuntimeBridgeTarget(
            manifest.InstanceId,
            entry.InstanceId,
            entry.BaseUrl,
            apiKey);
    }

    private static IEnumerable<SharpClawDiscoveryEntry> EnumerateBackendEntries(string sharedRoot)
    {
        var directory = Path.Combine(sharedRoot, "discovery", "instances");
        if (!Directory.Exists(directory))
            yield break;

        foreach (var path in Directory.EnumerateFiles(directory, "backend-*.json"))
        {
            SharpClawDiscoveryEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<SharpClawDiscoveryEntry>(
                    File.ReadAllText(path),
                    JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (entry is not null)
                yield return entry;
        }
    }
}
