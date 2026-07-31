using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Shared.Instances;
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
        X509Certificate2 clientCertificate)
    {
        InstancePaths = instancePaths ?? throw new ArgumentNullException(nameof(instancePaths));
        LocalUrl = localUrl ?? throw new ArgumentNullException(nameof(localUrl));
        GatewayBridgeUrl = gatewayBridgeUrl ?? throw new ArgumentNullException(nameof(gatewayBridgeUrl));
        GatewayServerPublicKeyHash = gatewayServerPublicKeyHash
            ?? throw new ArgumentNullException(nameof(gatewayServerPublicKeyHash));
        LocalApiKey = localApiKey ?? throw new ArgumentNullException(nameof(localApiKey));
        ClientCertificate = clientCertificate ?? throw new ArgumentNullException(nameof(clientCertificate));
        Validate();
    }

    public SharpClawInstancePaths InstancePaths { get; }

    public string LocalUrl { get; }

    public string GatewayBridgeUrl { get; }

    public string GatewayServerPublicKeyHash { get; }

    public string LocalApiKey { get; }

    public X509Certificate2 ClientCertificate { get; }

    public static RemoteRuntimeProxyConnection Create(
        SharpClawInstancePaths instancePaths,
        string localUrl,
        string gatewayBridgeUrl,
        string gatewayServerPublicKeyHash,
        X509Certificate2 clientCertificate)
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
                clientCertificate);
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
            ClientCertificate.Dispose();
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

        if (!ClientCertificate.HasPrivateKey)
            throw new InvalidOperationException(
                "RemoteProxy mode requires the private key for its approved client certificate.");
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
        var clientCertificate = session.DetachClientCertificate();
        RemoteRuntimeProxyConnection? connection = null;
        try
        {
            connection = RemoteRuntimeProxyConnection.Create(
                session.InstancePaths,
                plan.LocalUrl ?? "http://127.0.0.1:48923",
                session.State.GatewayBridgeUrl,
                session.State.GatewayServerPublicKeyHash,
                clientCertificate);
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
                connection.GatewayServerPublicKeyHash));

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (!HasLocalSessionKey(context, connection.LocalApiKey))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context);
        });
        app.MapForwarder(
            "/{**catch-all}",
            connection.GatewayBridgeUrl,
            ForwarderRequestConfig.Empty,
            new RemoteProxyTransformer());
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

    private sealed class RemoteProxyTransformer : HttpTransformer
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
        }
    }
}

internal sealed class ClientCertificateForwarderHttpClientFactory(
    X509Certificate2 clientCertificate,
    string gatewayServerPublicKeyHash) : ForwarderHttpClientFactory
{
    protected override void ConfigureHandler(
        ForwarderHttpClientContext context,
        SocketsHttpHandler handler)
    {
        base.ConfigureHandler(context, handler);
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
            using var publicKeyAlgorithm = serverCertificate.GetECDsaPublicKey();
            if (publicKeyAlgorithm is null)
                return false;

            var publicKey = publicKeyAlgorithm.ExportSubjectPublicKeyInfo();
            try
            {
                var actualHash = Convert.ToBase64String(SHA256.HashData(publicKey))
                    .TrimEnd('=')
                    .Replace('+', '-')
                    .Replace('/', '_');
                return string.Equals(actualHash, expectedHash, StringComparison.Ordinal);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(publicKey);
            }
        }
        finally
        {
            if (ownsCertificate)
                serverCertificate.Dispose();
        }
    }
}
