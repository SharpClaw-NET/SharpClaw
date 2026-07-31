using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.RemoteRuntimeBridge;
using Yarp.ReverseProxy.Forwarder;

namespace SharpClaw.Runtime.Host;

internal sealed class RemoteRuntimeProxyConnection : IDisposable
{
    private int _disposed;

    public RemoteRuntimeProxyConnection(
        SharpClawInstancePaths instancePaths,
        string localUrl,
        string gatewayBridgeUrl,
        string gatewayServerPublicKeyHash,
        string localApiKey,
        string proxyRuntimeInstanceId,
        X509Certificate2 clientCertificate,
        TimeSpan connectTimeout,
        TimeSpan activityTimeout,
        int maxConcurrentConnections = 128)
    {
        InstancePaths = instancePaths ?? throw new ArgumentNullException(nameof(instancePaths));
        LocalUrl = localUrl ?? throw new ArgumentNullException(nameof(localUrl));
        GatewayBridgeUrl = gatewayBridgeUrl ?? throw new ArgumentNullException(nameof(gatewayBridgeUrl));
        GatewayServerPublicKeyHash = gatewayServerPublicKeyHash
            ?? throw new ArgumentNullException(nameof(gatewayServerPublicKeyHash));
        LocalApiKey = localApiKey ?? throw new ArgumentNullException(nameof(localApiKey));
        ProxyRuntimeInstanceId = proxyRuntimeInstanceId
            ?? throw new ArgumentNullException(nameof(proxyRuntimeInstanceId));
        ClientCertificate = clientCertificate ?? throw new ArgumentNullException(nameof(clientCertificate));
        ConnectTimeout = connectTimeout;
        ActivityTimeout = activityTimeout;
        if (maxConcurrentConnections is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrentConnections),
                "The proxy connection limit must be between 1 and 4096.");
        }

        MaxConcurrentConnections = maxConcurrentConnections;
        _connectionLimiter = new SemaphoreSlim(maxConcurrentConnections, maxConcurrentConnections);
        Validate();
    }

    public SharpClawInstancePaths InstancePaths { get; }

    public string LocalUrl { get; }

    public string GatewayBridgeUrl { get; }

    public string GatewayServerPublicKeyHash { get; }

    public string LocalApiKey { get; }

    public string ProxyRuntimeInstanceId { get; }

    public X509Certificate2 ClientCertificate { get; }

    public TimeSpan ConnectTimeout { get; }

    public TimeSpan ActivityTimeout { get; }

    public int MaxConcurrentConnections { get; }

    private readonly SemaphoreSlim _connectionLimiter;

    public static RemoteRuntimeProxyConnection Create(
        SharpClawInstancePaths instancePaths,
        string localUrl,
        string gatewayBridgeUrl,
        string gatewayServerPublicKeyHash,
        string proxyRuntimeInstanceId,
        X509Certificate2 clientCertificate,
        TimeSpan connectTimeout,
        TimeSpan activityTimeout,
        int maxConcurrentConnections = 128)
    {
        ArgumentNullException.ThrowIfNull(instancePaths);
        var keyBytes = RandomNumberGenerator.GetBytes(32);
        string? localApiKey = null;
        var keyFileWritten = false;
        try
        {
            localApiKey = Convert.ToBase64String(keyBytes);
            instancePaths.EnsureDirectories();
            File.WriteAllText(instancePaths.ApiKeyFilePath, localApiKey);
            keyFileWritten = true;
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    instancePaths.ApiKeyFilePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return new RemoteRuntimeProxyConnection(
                instancePaths,
                localUrl,
                gatewayBridgeUrl,
                gatewayServerPublicKeyHash,
                localApiKey,
                proxyRuntimeInstanceId,
                clientCertificate,
                connectTimeout,
                activityTimeout,
                maxConcurrentConnections);
        }
        catch
        {
            if (keyFileWritten
                && localApiKey is not null
                && File.Exists(instancePaths.ApiKeyFilePath)
                && string.Equals(
                    File.ReadAllText(instancePaths.ApiKeyFilePath).Trim(),
                    localApiKey,
                    StringComparison.Ordinal))
            {
                File.Delete(instancePaths.ApiKeyFilePath);
            }

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    public void PublishDiscovery()
        => InstancePaths.PublishDiscoveryEntry(
            LocalUrl,
            DateTimeOffset.UtcNow,
            Environment.ProcessId,
            gatewayTokenFilePath: null);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            if (File.Exists(InstancePaths.ApiKeyFilePath)
                && string.Equals(
                    File.ReadAllText(InstancePaths.ApiKeyFilePath).Trim(),
                    LocalApiKey,
                    StringComparison.Ordinal))
            {
                File.Delete(InstancePaths.ApiKeyFilePath);
            }

            InstancePaths.DeleteDiscoveryEntry();
        }
        finally
        {
            _connectionLimiter.Dispose();
            ClientCertificate.Dispose();
        }
    }

    internal IDisposable? TryAcquireConnection()
        => _connectionLimiter.Wait(0)
            ? new ConnectionLease(_connectionLimiter)
            : null;

    private sealed class ConnectionLease(SemaphoreSlim limiter) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                limiter.Release();
        }
    }

    private void Validate()
    {
        if (!Uri.TryCreate(LocalUrl, UriKind.Absolute, out var localUri)
            || (!string.Equals(localUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(localUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || !localUri.IsLoopback)
        {
            throw new InvalidOperationException(
                "RemoteProxy mode must bind its local session API to a loopback URL.");
        }

        if (!Uri.TryCreate(GatewayBridgeUrl, UriKind.Absolute, out var gatewayUri)
            || !string.Equals(gatewayUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "RemoteProxy mode requires an HTTPS Gateway bridge URL from approved pairing state.");
        }

        if (string.IsNullOrWhiteSpace(GatewayServerPublicKeyHash))
            throw new InvalidOperationException(
                "RemoteProxy mode requires the approved Gateway server public-key hash.");

        if (string.IsNullOrWhiteSpace(LocalApiKey))
            throw new InvalidOperationException("RemoteProxy mode requires a local session API key.");

        if (string.IsNullOrWhiteSpace(ProxyRuntimeInstanceId))
            throw new InvalidOperationException("RemoteProxy mode requires its approved proxy identity.");

        if (!ClientCertificate.HasPrivateKey)
            throw new InvalidOperationException(
                "RemoteProxy mode requires the private key for its approved client certificate.");

        if (ConnectTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("RemoteProxy mode requires a positive connection timeout.");

        if (ActivityTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("RemoteProxy mode requires a positive activity timeout.");
    }
}

public static class RemoteProxyHost
{
    public static async Task RunAsync(
        RuntimeLaunchPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Mode != RuntimeLaunchMode.RemoteProxy)
            throw new ArgumentException("The launch plan is not RemoteProxy mode.", nameof(plan));

        cancellationToken.ThrowIfCancellationRequested();
        using var session = await RemoteRuntimePairingAuthorization.LoadApprovedSessionAsync(
            plan,
            cancellationToken);
        var options = plan.RequireRemoteProxyOptions();
        var clientCertificate = session.DetachClientCertificate();
        RemoteRuntimeProxyConnection? connection = null;
        try
        {
            connection = RemoteRuntimeProxyConnection.Create(
                session.InstancePaths,
                options.LocalUrl,
                session.State.GatewayBridgeUrl,
                session.State.GatewayServerPublicKeyHash,
                session.State.ProxyRuntimeInstanceId,
                clientCertificate,
                TimeSpan.FromSeconds(options.ConnectTimeoutSeconds),
                TimeSpan.FromSeconds(options.ActivityTimeoutSeconds),
                options.MaxConcurrentConnections);
            await RunAsync(connection, cancellationToken);
        }
        catch
        {
            if (connection is null)
                clientCertificate.Dispose();
            throw;
        }
    }

    internal static async Task RunAsync(
        RemoteRuntimeProxyConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        connection.PublishDiscovery();
        await using var app = Build([], connection);
        try
        {
            await app.StartAsync(cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            connection.Dispose();
        }
    }

    public static WebApplication Build(string[] args, RuntimeLaunchPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Mode != RuntimeLaunchMode.RemoteProxy)
            throw new ArgumentException("The launch plan is not RemoteProxy mode.", nameof(plan));

        throw new InvalidOperationException(
            "RemoteProxy mode requires a loaded approved pairing session before binding.");
    }

    internal static WebApplication Build(
        string[] args,
        RemoteRuntimeProxyConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var builder = WebApplication.CreateSlimBuilder(args);
        builder.WebHost.UseUrls(connection.LocalUrl);
        builder.Services.AddReverseProxy();
        builder.Services.AddSingleton<IForwarderHttpClientFactory>(
            _ => new ClientCertificateForwarderHttpClientFactory(
                connection.ClientCertificate,
                connection.GatewayServerPublicKeyHash,
                connection.ConnectTimeout));

        var app = builder.Build();
        app.UseWebSockets();
        app.Use(async (context, next) =>
        {
            if (!HasLocalSessionKey(context, connection.LocalApiKey))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            using var lease = connection.TryAcquireConnection();
            if (lease is null)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.ContentType = "application/json";
                context.Response.Headers.RetryAfter = "0";
                await context.Response.WriteAsJsonAsync(
                    new
                    {
                        code = "ProxyConcurrencyLimit",
                        error = "The proxy connection limit is active.",
                    },
                    context.RequestAborted);
                return;
            }

            await next(context);
            await NormalizeForwarderErrorAsync(context);
        });
        app.MapForwarder(
            "/{**catch-all}",
            connection.GatewayBridgeUrl,
            new ForwarderRequestConfig
            {
                ActivityTimeout = connection.ActivityTimeout,
                AllowResponseBuffering = false,
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            },
            new RemoteProxyTransformer(connection.ProxyRuntimeInstanceId));
        return app;
    }

    private static bool HasLocalSessionKey(HttpContext context, string expectedKey)
    {
        var suppliedKey = context.Request.Headers["X-Api-Key"].ToString();
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedKey);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedKey);
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

    internal static Task NormalizeForwarderErrorAsync(HttpContext context)
    {
        if (context.Features.Get<IForwarderErrorFeature>() is null
            || context.Response.HasStarted
            || context.Response.StatusCode is not (StatusCodes.Status502BadGateway
                or StatusCodes.Status503ServiceUnavailable))
        {
            return Task.CompletedTask;
        }

        var statusCode = context.Response.StatusCode;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(
            new
            {
                code = statusCode == StatusCodes.Status502BadGateway
                    ? "ProxyBadGateway"
                    : "ProxyServiceUnavailable",
                error = statusCode == StatusCodes.Status502BadGateway
                    ? "The proxy could not reach Gateway."
                    : "Gateway is unavailable.",
            },
            context.RequestAborted);
    }

    internal sealed class RemoteProxyTransformer(string proxyRuntimeInstanceId) : HttpTransformer
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
            foreach (var header in proxyRequest.Headers
                         .Where(static header => header.Key.StartsWith(
                             "X-SharpClaw-Bridge-",
                             StringComparison.OrdinalIgnoreCase))
                         .Select(static header => header.Key)
                         .ToArray())
            {
                proxyRequest.Headers.Remove(header);
            }
            proxyRequest.Headers.TryAddWithoutValidation(
                RemoteRuntimeBridgePaths.ProxyIdentityHeader,
                proxyRuntimeInstanceId);
        }
    }
}

internal sealed class ClientCertificateForwarderHttpClientFactory(
    X509Certificate2 clientCertificate,
    string gatewayServerPublicKeyHash,
    TimeSpan connectTimeout) : ForwarderHttpClientFactory
{
    protected override void ConfigureHandler(
        ForwarderHttpClientContext context,
        SocketsHttpHandler handler)
    {
        base.ConfigureHandler(context, handler);
        handler.ConnectTimeout = connectTimeout;
        handler.SslOptions.ClientCertificates ??= new X509CertificateCollection();
        handler.SslOptions.ClientCertificates.Add(clientCertificate);
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            HasPinnedPublicKey(certificate, gatewayServerPublicKeyHash);
    }

    private static bool HasPinnedPublicKey(
        X509Certificate? certificate,
        string expectedHash)
    {
        if (certificate is null)
            return false;

        var serverCertificate = certificate as X509Certificate2;
        var ownsCertificate = serverCertificate is null;
        serverCertificate ??= new X509Certificate2(certificate);
        try
        {
            string actualHash;
            try
            {
                actualHash = RemoteRuntimeCertificateHash.Compute(serverCertificate);
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            return string.Equals(actualHash, expectedHash, StringComparison.Ordinal);
        }
        finally
        {
            if (ownsCertificate)
                serverCertificate.Dispose();
        }
    }
}
