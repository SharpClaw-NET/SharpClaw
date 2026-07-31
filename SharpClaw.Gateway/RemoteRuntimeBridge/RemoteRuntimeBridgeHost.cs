using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
    string AuthoritativeRuntimeInstallFingerprint,
    string TargetBaseUrl,
    string AuthoritativeApiKey,
    string AuthoritativeGatewayToken);

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

    internal static async Task<WebApplication> BuildAsync(
        string[] args,
        RemoteRuntimeBridgeOptions options,
        IRemoteRuntimePairingRegistryClient registryClient,
        RemoteRuntimeBridgeTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(registryClient);
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

        var concurrencyLimiter = new RemoteRuntimeBridgeConcurrencyLimiter(options);
        var builder = WebApplication.CreateSlimBuilder(args);
        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.ListenAnyIP(
                listenUri.Port,
                listenOptions =>
                {
                    listenOptions.UseHttps(options.ServerCertificatePath, null, httpsOptions =>
                    {
                        httpsOptions.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                        httpsOptions.ClientCertificateValidation = (_, _, _) => true;
                    });
                });
        });
        builder.Services.AddReverseProxy();

        var bridgeApp = builder.Build();
        bridgeApp.UseWebSockets();
        bridgeApp.Use(async (context, next) =>
        {
            var certificate = await context.Connection.GetClientCertificateAsync(
                context.RequestAborted);
            RemoteRuntimePairingRegistrySnapshot? activePair = null;
            if (certificate is null && !IsUnauthenticatedControlPath(context.Request.Path))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (certificate is not null)
            {
                try
                {
                    if (!HasClientAuthenticationUsage(certificate))
                        throw new RemoteRuntimePairingException(
                            "PairNotAuthorized",
                            "The client certificate is not authorized for bridge access.");

                    activePair = await registryClient.RequireActiveCertificateAsync(
                        certificate,
                        target.GatewayInstanceId,
                        target.AuthoritativeRuntimeInstanceId,
                        context.RequestAborted);
                }
                catch (RemoteRuntimePairingException)
                {
                    if (!IsUnauthenticatedControlPath(context.Request.Path))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return;
                    }
                }
            }

            var workKind = GetWorkKind(context);
            var lease = activePair is null
                ? null
                : concurrencyLimiter.TryAcquire(activePair.PairId, workKind);
            if (activePair is not null && lease is null)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.RetryAfter = "0";
                return;
            }

            try
            {
                await next(context);
            }
            finally
            {
                lease?.Dispose();
            }
        });

            bridgeApp.MapPost(
            RemoteRuntimeBridgePaths.PairingClaim,
            async (
                RemoteRuntimePairingClaimRequest request,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var record = await registryClient.ClaimAsync(request, cancellationToken);
                    return Results.Ok(record);
                }
                catch (RemoteRuntimePairingException exception)
                {
                    return Results.Json(
                        new { code = exception.Code, error = exception.Message },
                        statusCode: StatusCodes.Status400BadRequest);
                }
                catch (ArgumentException exception)
                {
                    return Results.Json(
                        new { error = exception.Message },
                        statusCode: StatusCodes.Status400BadRequest);
                }
            });

        bridgeApp.MapPost(
            RemoteRuntimeBridgePaths.PairingCertificate,
            async (
                RemoteRuntimePairingCertificateRequest request,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var certificate = await registryClient.IssueClientCertificateAsync(
                        request,
                        cancellationToken);
                    return Results.Ok(certificate);
                }
                catch (RemoteRuntimePairingException exception)
                {
                    return Results.Json(
                        new { code = exception.Code, error = exception.Message },
                        statusCode: StatusCodes.Status400BadRequest);
                }
                catch (ArgumentException exception)
                {
                    return Results.Json(
                        new { error = exception.Message },
                        statusCode: StatusCodes.Status400BadRequest);
                }
            });

        bridgeApp.MapGet(
            RemoteRuntimeBridgePaths.RegistryActive,
            async (CancellationToken cancellationToken) =>
            {
                try
                {
                    var active = await registryClient.FindActiveAsync(
                        target.GatewayInstanceId,
                        target.AuthoritativeRuntimeInstanceId,
                        cancellationToken);
                    return active is null
                        ? Results.StatusCode(StatusCodes.Status403Forbidden)
                        : Results.Ok(active);
                }
                catch (RemoteRuntimePairingException exception)
                {
                    return Results.Json(
                        new { code = exception.Code, error = exception.Message },
                        statusCode: StatusCodes.Status400BadRequest);
                }
            });

        bridgeApp.MapPost(
            RemoteRuntimeBridgePaths.AdminInvitation,
            async (
                HttpContext context,
                RemoteRuntimePairingAdminInvitationRequest request,
                CancellationToken cancellationToken) =>
            {
                if (!HasLocalAdministrationAccess(context, options.AdministrationKey))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);

                try
                {
                    var invitation = await registryClient.CreateInvitationAsync(
                        new RemoteRuntimeRegistryInvitationRequest(
                            target.GatewayInstanceId,
                            ComputeServerPublicKeyHash(options.ServerCertificatePath!),
                            target.AuthoritativeRuntimeInstanceId,
                            target.AuthoritativeRuntimeInstallFingerprint,
                            request.LifetimeSeconds),
                        cancellationToken);
                    return Results.Ok(invitation);
                }
                catch (ArgumentOutOfRangeException exception)
                {
                    return Results.Json(
                        new { error = exception.Message },
                        statusCode: StatusCodes.Status400BadRequest);
                }
            });

        bridgeApp.MapPost(
            RemoteRuntimeBridgePaths.AdminApprove,
            async (
                HttpContext context,
                RemoteRuntimePairingAdminApprovalRequest request,
                CancellationToken cancellationToken) =>
            {
                if (!HasLocalAdministrationAccess(context, options.AdministrationKey))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);

                try
                {
                    var record = await registryClient.ApproveAsync(
                        new RemoteRuntimeRegistryApprovalRequest(
                            request.PairId,
                            request.ProxyRuntimeInstanceId,
                            request.AuthoritativeRuntimeInstanceId,
                            null),
                        cancellationToken);
                    return Results.Ok(record);
                }
                catch (RemoteRuntimePairingException exception)
                {
                    return Results.Json(
                        new { code = exception.Code, error = exception.Message },
                        statusCode: StatusCodes.Status400BadRequest);
                }
                catch (ArgumentException exception)
                {
                    return Results.Json(
                        new { error = exception.Message },
                        statusCode: StatusCodes.Status400BadRequest);
                }
            });

        bridgeApp.MapPost(
            RemoteRuntimeBridgePaths.AdminRevoke,
            async (
                HttpContext context,
                RemoteRuntimePairingAdminRevocationRequest request,
                CancellationToken cancellationToken) =>
            {
                if (!HasLocalAdministrationAccess(context, options.AdministrationKey))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);

                try
                {
                    var record = await registryClient.RevokeAsync(
                        new RemoteRuntimeRegistryRevocationRequest(
                            request.PairId,
                            "Administrator request"),
                        cancellationToken);
                    return Results.Ok(record);
                }
                catch (RemoteRuntimePairingException exception)
                {
                    return Results.Json(
                        new { code = exception.Code, error = exception.Message },
                        statusCode: StatusCodes.Status400BadRequest);
                }
                catch (ArgumentException exception)
                {
                    return Results.Json(
                        new { error = exception.Message },
                        statusCode: StatusCodes.Status400BadRequest);
                }
            });

        bridgeApp.MapGet(
            RemoteRuntimeBridgePaths.AdminPairings,
            async (HttpContext context, CancellationToken cancellationToken) =>
            {
                if (!HasLocalAdministrationAccess(context, options.AdministrationKey))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);

                var query = context.Request.Query;
                var take = 50;
                if (query.TryGetValue("take", out var takeValue)
                    && (!int.TryParse(takeValue.ToString(), out take) || take < 1 || take > 200))
                {
                    return Results.BadRequest(new { error = "The pairing page size is invalid." });
                }

                RemoteRuntimePairStatus? status = null;
                if (query.TryGetValue("status", out var statusValue)
                    && !string.IsNullOrWhiteSpace(statusValue))
                {
                    if (!Enum.TryParse<RemoteRuntimePairStatus>(
                            statusValue.ToString(),
                            ignoreCase: true,
                            out var parsedStatus))
                    {
                        return Results.BadRequest(new { error = "The pairing status filter is invalid." });
                    }

                    status = parsedStatus;
                }

                RemoteRuntimeRegistryPageCursor? cursor = null;
                var cursorCreated = query["cursorCreatedAtUtc"].ToString();
                var cursorId = query["cursorId"].ToString();
                if (!string.IsNullOrWhiteSpace(cursorCreated) || !string.IsNullOrWhiteSpace(cursorId))
                {
                    if (!DateTimeOffset.TryParse(cursorCreated, out var createdAtUtc)
                        || !Guid.TryParse(cursorId, out var id))
                    {
                        return Results.BadRequest(new { error = "The pairing page cursor is invalid." });
                    }

                    cursor = new RemoteRuntimeRegistryPageCursor(createdAtUtc, id);
                }

                try
                {
                    return Results.Ok(await registryClient.ListAsync(
                        query["gatewayInstanceId"].ToString(),
                        query["authoritativeRuntimeInstanceId"].ToString(),
                        query["proxyRuntimeInstanceId"].ToString(),
                        status,
                        query["search"].ToString(),
                        take,
                        cursor,
                        cancellationToken));
                }
                catch (RemoteRuntimePairingException exception)
                {
                    return Results.Json(
                        new { code = exception.Code, error = exception.Message },
                        statusCode: StatusCodes.Status400BadRequest);
                }
            });

        bridgeApp.MapGet(
            RemoteRuntimeBridgePaths.AdminPairing,
            async (
                HttpContext context,
                Guid pairId,
                CancellationToken cancellationToken) =>
            {
                if (!HasLocalAdministrationAccess(context, options.AdministrationKey))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);

                try
                {
                    var entry = await registryClient.FindAsync(pairId, cancellationToken);
                    return entry is null ? Results.NotFound() : Results.Ok(entry);
                }
                catch (RemoteRuntimePairingException exception)
                {
                    return Results.Json(
                        new { code = exception.Code, error = exception.Message },
                        statusCode: StatusCodes.Status400BadRequest);
                }
            });

        bridgeApp.MapPut(
            RemoteRuntimeBridgePaths.AdminPairing,
            async (
                HttpContext context,
                Guid pairId,
                RemoteRuntimeRegistryDetailsRequest request,
                CancellationToken cancellationToken) =>
            {
                if (!HasLocalAdministrationAccess(context, options.AdministrationKey))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);

                try
                {
                    return Results.Ok(await registryClient.UpdateAsync(
                        pairId,
                        request,
                        cancellationToken));
                }
                catch (RemoteRuntimePairingException exception)
                {
                    return Results.Json(
                        new { code = exception.Code, error = exception.Message },
                        statusCode: StatusCodes.Status400BadRequest);
                }
            });

        bridgeApp.MapPost(
            RemoteRuntimeBridgePaths.AdminPairingRenew,
            async (
                HttpContext context,
                Guid pairId,
                RemoteRuntimeRegistryRenewalRequest request,
                CancellationToken cancellationToken) =>
            {
                if (!HasLocalAdministrationAccess(context, options.AdministrationKey))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);

                try
                {
                    return Results.Ok(await registryClient.RenewAsync(
                        pairId,
                        request,
                        cancellationToken));
                }
                catch (RemoteRuntimePairingException exception)
                {
                    return Results.Json(
                        new { code = exception.Code, error = exception.Message },
                        statusCode: StatusCodes.Status400BadRequest);
                }
            });

        bridgeApp.MapPost(
            RemoteRuntimeBridgePaths.AdminPairingReject,
            async (
                HttpContext context,
                Guid pairId,
                RemoteRuntimeRegistryReasonRequest request,
                CancellationToken cancellationToken) =>
            {
                if (!HasLocalAdministrationAccess(context, options.AdministrationKey))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);

                try
                {
                    return Results.Ok(await registryClient.RejectAsync(
                        pairId,
                        request.Reason,
                        cancellationToken));
                }
                catch (RemoteRuntimePairingException exception)
                {
                    return Results.Json(
                        new { code = exception.Code, error = exception.Message },
                        statusCode: StatusCodes.Status400BadRequest);
                }
            });

        bridgeApp.MapDelete(
            RemoteRuntimeBridgePaths.AdminPairing,
            async (
                HttpContext context,
                Guid pairId,
                CancellationToken cancellationToken) =>
            {
                if (!HasLocalAdministrationAccess(context, options.AdministrationKey))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);

                try
                {
                    await registryClient.DeleteAsync(pairId, cancellationToken);
                    return Results.NoContent();
                }
                catch (RemoteRuntimePairingException exception)
                {
                    return Results.Json(
                        new { code = exception.Code, error = exception.Message },
                        statusCode: StatusCodes.Status400BadRequest);
                }
            });

            bridgeApp.MapForwarder(
                "/{**catch-all}",
            target.TargetBaseUrl,
            new ForwarderRequestConfig
            {
                AllowResponseBuffering = false,
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            },
            new RemoteRuntimeBridgeTransformer(
                target.AuthoritativeApiKey,
                target.AuthoritativeGatewayToken));
        return bridgeApp;
    }

    private static bool HasClientAuthenticationUsage(X509Certificate2 certificate)
    {
        var keyUsage = certificate.Extensions
            .OfType<X509KeyUsageExtension>()
            .SingleOrDefault();
        if (keyUsage is null
            || (keyUsage.KeyUsages & X509KeyUsageFlags.DigitalSignature) == 0)
        {
            return false;
        }

        var enhancedKeyUsage = certificate.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .SingleOrDefault();
        return enhancedKeyUsage is not null
            && enhancedKeyUsage.EnhancedKeyUsages
                .Cast<Oid>()
                .Any(usage => usage.Value == "1.3.6.1.5.5.7.3.2");
    }

    private static bool IsUnauthenticatedControlPath(PathString path)
        => path.Equals(RemoteRuntimeBridgePaths.PairingClaim, StringComparison.Ordinal)
            || path.Equals(RemoteRuntimeBridgePaths.PairingCertificate, StringComparison.Ordinal)
            || path.Equals(RemoteRuntimeBridgePaths.AdminInvitation, StringComparison.Ordinal)
            || path.Equals(RemoteRuntimeBridgePaths.AdminApprove, StringComparison.Ordinal)
            || path.Equals(RemoteRuntimeBridgePaths.AdminRevoke, StringComparison.Ordinal)
            || path.Value?.StartsWith(
                RemoteRuntimeBridgePaths.AdminPairings,
                StringComparison.Ordinal) == true;

    private static RemoteRuntimeBridgeWorkKind GetWorkKind(HttpContext context)
    {
        if (context.WebSockets.IsWebSocketRequest)
            return RemoteRuntimeBridgeWorkKind.WebSocket;

        return context.Request.Headers["Accept"].Any(value =>
                value?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
            ? RemoteRuntimeBridgeWorkKind.Stream
            : RemoteRuntimeBridgeWorkKind.Request;
    }

    private static bool HasLocalAdministrationAccess(
        HttpContext context,
        string? expectedKey)
    {
        if (string.IsNullOrWhiteSpace(expectedKey)
            || context.Connection.RemoteIpAddress is not { } remoteAddress
            || !System.Net.IPAddress.IsLoopback(remoteAddress)
            || context.Request.Headers.Keys.Any(IsForwardedHeader))
        {
            return false;
        }

        var suppliedKey = context.Request.Headers[RemoteRuntimeBridgePaths.AdministrationKeyHeader].ToString();
        var suppliedBytes = System.Text.Encoding.UTF8.GetBytes(suppliedKey);
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expectedKey);
        try
        {
            return CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(suppliedBytes);
            CryptographicOperations.ZeroMemory(expectedBytes);
        }
    }

    private static bool IsForwardedHeader(string header)
        => header.Equals("Forwarded", StringComparison.OrdinalIgnoreCase)
            || header.Equals("X-Forwarded-For", StringComparison.OrdinalIgnoreCase)
            || header.Equals("X-Forwarded-Host", StringComparison.OrdinalIgnoreCase)
            || header.Equals("X-Forwarded-Proto", StringComparison.OrdinalIgnoreCase);

    private static string ComputeServerPublicKeyHash(string certificatePath)
    {
        var certificateBytes = File.ReadAllBytes(certificatePath);
        try
        {
            using var certificate = X509CertificateLoader.LoadPkcs12(
                certificateBytes,
                password: null,
                keyStorageFlags: X509KeyStorageFlags.EphemeralKeySet);
            return RemoteRuntimeCertificateHash.Compute(certificate);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(certificateBytes);
        }
    }

    private sealed class RemoteRuntimeBridgeTransformer(
        string authoritativeApiKey,
        string authoritativeGatewayToken) : HttpTransformer
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
            if (httpContext.Request.Path.Equals(
                    RemoteRuntimeBridgePaths.CliControl,
                    StringComparison.Ordinal))
            {
                proxyRequest.Headers.TryAddWithoutValidation(
                    "X-Gateway-Token",
                    authoritativeGatewayToken);
            }
            else
            {
                proxyRequest.Headers.Remove("X-Gateway-Token");
            }
        }
    }
}

internal sealed class RemoteRuntimeBridgeHostedService(
    RemoteRuntimeBridgeOptions options,
    SharpClawInstancePaths gatewayPaths,
    ILogger<RemoteRuntimeBridgeHostedService> logger) : IHostedService, IAsyncDisposable
{
    private WebApplication? _bridgeApp;
    private IRemoteRuntimePairingRegistryClient? _registryClient;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var target = RemoteRuntimeBridgeTargetResolver.Resolve(gatewayPaths);
        var registryClient = RemoteRuntimePairingRegistryClient.Create(target);
        try
        {
            _bridgeApp = await RemoteRuntimeBridgeHost.BuildAsync(
                [],
                options,
                registryClient,
                target,
                cancellationToken);
            await _bridgeApp.StartAsync(cancellationToken);
            _registryClient = registryClient;
        }
        catch
        {
            await registryClient.DisposeAsync();
            throw;
        }
        logger.LogInformation("Remote Runtime bridge listener started on {ListenUrl}.", options.ListenUrl);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_bridgeApp is null)
            return;

        await _bridgeApp.StopAsync(cancellationToken);
        await _bridgeApp.DisposeAsync();
        _bridgeApp = null;
        if (_registryClient is not null)
        {
            await _registryClient.DisposeAsync();
            _registryClient = null;
        }
    }

    public ValueTask DisposeAsync()
        => DisposeResourcesAsync();

    private async ValueTask DisposeResourcesAsync()
    {
        if (_bridgeApp is not null)
        {
            await _bridgeApp.DisposeAsync();
            _bridgeApp = null;
        }

        if (_registryClient is not null)
        {
            await _registryClient.DisposeAsync();
            _registryClient = null;
        }
    }
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

        if (string.IsNullOrWhiteSpace(entry.GatewayTokenFilePath)
            || !File.Exists(entry.GatewayTokenFilePath))
        {
            throw new InvalidOperationException(
                "The selected authoritative Runtime discovery entry has no Gateway service token.");
        }

        var gatewayToken = File.ReadAllText(entry.GatewayTokenFilePath).Trim();
        if (string.IsNullOrWhiteSpace(gatewayToken))
        {
            throw new InvalidOperationException(
                "The selected authoritative Runtime Gateway service token is empty.");
        }

        return new RemoteRuntimeBridgeTarget(
            manifest.InstanceId,
            entry.InstanceId,
            entry.InstallFingerprint,
            entry.BaseUrl,
            apiKey,
            gatewayToken);
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
