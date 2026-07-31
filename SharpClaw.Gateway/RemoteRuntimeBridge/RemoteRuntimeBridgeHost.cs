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

    internal static async Task<WebApplication> BuildAsync(
        string[] args,
        RemoteRuntimeBridgeOptions options,
        RemoteRuntimePairingStore pairingStore,
        RemoteRuntimeBridgeTarget target,
        CancellationToken cancellationToken = default)
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

        var certificateAuthority =
            await pairingStore.GetCertificateAuthorityPublicCertificateAsync(cancellationToken);
        var builder = WebApplication.CreateSlimBuilder(args);
        try
        {
            builder.WebHost.ConfigureKestrel(serverOptions =>
            {
                serverOptions.ListenAnyIP(
                    listenUri.Port,
                    listenOptions =>
                    {
                        listenOptions.UseHttps(options.ServerCertificatePath, null, httpsOptions =>
                        {
                            httpsOptions.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                            httpsOptions.ClientCertificateValidation = (certificate, _, _) =>
                                ValidateClientCertificate(certificate, certificateAuthority);
                        });
                    });
            });
            builder.Services.AddReverseProxy();

            var bridgeApp = builder.Build();
            bridgeApp.Lifetime.ApplicationStopped.Register(certificateAuthority.Dispose);
            bridgeApp.Use(async (context, next) =>
            {
                var certificate = await context.Connection.GetClientCertificateAsync(
                    context.RequestAborted);
                if (certificate is null && !IsUnauthenticatedControlPath(context.Request.Path))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                if (certificate is not null)
                {
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
                        if (!IsUnauthenticatedControlPath(context.Request.Path))
                        {
                            context.Response.StatusCode = StatusCodes.Status403Forbidden;
                            return;
                        }
                    }
                }

                await next(context);
            });

            bridgeApp.MapPost(
            RemoteRuntimeBridgePaths.PairingClaim,
            async (
                RemoteRuntimePairingClaimRequest request,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var record = await pairingStore.ClaimInvitationAsync(
                        request.PairId,
                        request.Secret,
                        request.ProxyRuntimeInstanceId,
                        request.CertificateSigningRequestBase64,
                        request.ProofSignatureBase64,
                        cancellationToken);
                    return Results.Ok(ToClaimResponse(record));
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
                    var certificate = await pairingStore.IssueClientCertificateAsync(
                        request.PairId,
                        request.Secret,
                        cancellationToken);
                    return Results.Ok(new RemoteRuntimePairingCertificateResponse(
                        Convert.ToBase64String(certificate.CertificateDer),
                        certificate.ProxyRuntimePublicKeyHash,
                        certificate.CertificateThumbprint,
                        certificate.NotAfterUtc));
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
                    var invitation = await pairingStore.CreateInvitationAsync(
                        target.GatewayInstanceId,
                        ComputeServerPublicKeyHash(options.ServerCertificatePath!),
                        target.AuthoritativeRuntimeInstanceId,
                        target.AuthoritativeRuntimeInstallFingerprint,
                        TimeSpan.FromSeconds(request.LifetimeSeconds),
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
                    var record = await pairingStore.ApproveClaimAsync(
                        request.PairId,
                        request.ProxyRuntimeInstanceId,
                        request.AuthoritativeRuntimeInstanceId,
                        cancellationToken);
                    return Results.Ok(ToClaimResponse(record));
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
                    var record = await pairingStore.RevokeAsync(
                        request.PairId,
                        cancellationToken);
                    return Results.Ok(ToClaimResponse(record));
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

            bridgeApp.MapForwarder(
                "/{**catch-all}",
            target.TargetBaseUrl,
            ForwarderRequestConfig.Empty,
            new RemoteRuntimeBridgeTransformer(
                target.AuthoritativeApiKey,
                target.AuthoritativeGatewayToken));
            return bridgeApp;
        }
        catch
        {
            certificateAuthority.Dispose();
            throw;
        }
    }

    private static bool ValidateClientCertificate(
        X509Certificate2 certificate,
        X509Certificate2 certificateAuthority)
    {
        if (!HasClientAuthenticationUsage(certificate))
            return false;

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(certificateAuthority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate);
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
            || path.Equals(RemoteRuntimeBridgePaths.AdminRevoke, StringComparison.Ordinal);

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

    private static RemoteRuntimePairingClaimResponse ToClaimResponse(
        RemoteRuntimePairingRecord record)
        => new(
            record.PairId,
            record.GetEffectiveStatus(DateTimeOffset.UtcNow).ToString(),
            record.GatewayInstanceId,
            record.AuthoritativeRuntimeInstanceId,
            record.ProxyRuntimeInstanceId ?? string.Empty);

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
    RemoteRuntimePairingStore pairingStore,
    SharpClawInstancePaths gatewayPaths,
    ILogger<RemoteRuntimeBridgeHostedService> logger) : IHostedService, IAsyncDisposable
{
    private WebApplication? _bridgeApp;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var target = RemoteRuntimeBridgeTargetResolver.Resolve(gatewayPaths);
        _bridgeApp = await RemoteRuntimeBridgeHost.BuildAsync(
            [],
            options,
            pairingStore,
            target,
            cancellationToken);
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
