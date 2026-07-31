using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.RemoteRuntimeBridge;
using SharpClaw.Shared.RemoteRuntimeBridge;

namespace SharpClaw.Tests.Architecture;

[TestFixture]
[NonParallelizable]
public sealed class RemoteRuntimeBridgeRevocationTests
{
    [Test]
    public async Task Approved_certificate_can_reach_the_bridge_then_revocation_blocks_the_next_request()
    {
        var root = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "bridge-revocation-" + Guid.NewGuid().ToString("N"));
        var certificatePath = Path.Combine(root, "bridge.pfx");
        using var serverCertificate = CreateCertificate("CN=bridge-revocation-test");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(certificatePath, serverCertificate.Export(X509ContentType.Pfx));

        var target = new RemoteRuntimeBridgeTarget(
            "gateway-1",
            "runtime-1",
            "runtime-install-1",
            "http://127.0.0.1:1",
            "authoritative-api-key",
            "authoritative-gateway-token");
        await using var registryClient = new InMemoryRemoteRuntimePairingRegistryClient(target, active: true);
        using var clientCertificate = registryClient.ClientCertificate;
        clientCertificate.HasPrivateKey.Should().BeTrue();
        using (var clientKey = clientCertificate.GetECDsaPrivateKey())
        {
            clientKey.Should().NotBeNull();
        }
        clientCertificate.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .Single()
            .EnhancedKeyUsages
            .Cast<Oid>()
            .Should()
            .Contain(usage => usage.Value == "1.3.6.1.5.5.7.3.2");
        var options = new RemoteRuntimeBridgeOptions
        {
            Enabled = true,
            ListenUrl = $"https://127.0.0.1:{GetFreePort()}",
            ServerCertificatePath = certificatePath,
        };
        await using var app = await RemoteRuntimeBridgeHost.BuildAsync(
            [],
            options,
            registryClient,
            target);

        try
        {
            await app.StartAsync();
            var address = app.Urls.Single();
            var port = new Uri(address).Port;
            using var handler = new HttpClientHandler
            {
                ClientCertificateOptions = ClientCertificateOption.Manual,
                SslProtocols = SslProtocols.Tls12,
                ServerCertificateCustomValidationCallback = (_, presented, _, _) =>
                    HasPinnedPublicKey(presented, RemoteRuntimeCertificateHash.Compute(serverCertificate)),
            };
            handler.ClientCertificates.Add(clientCertificate);
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri($"https://127.0.0.1:{port}"),
                Timeout = TimeSpan.FromSeconds(5),
            };

            using var activeResponse = await client.GetAsync("/api/health");
            activeResponse.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
            activeResponse.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);

            var revoked = await registryClient.RevokeAsync(
                new RemoteRuntimeRegistryRevocationRequest(
                    registryClient.PairId,
                    "test revocation"),
                CancellationToken.None);
            revoked.Status.Should().Be(RemoteRuntimePairStatus.Revoked);

            using var revokedResponse = await client.GetAsync("/api/health");
            revokedResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            await app.StopAsync();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }


    private static X509Certificate2 CreateCertificate(string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(5));
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static bool HasPinnedPublicKey(
        X509Certificate? certificate,
        string expectedHash)
    {
        if (certificate is null)
            return false;

        var presented = certificate as X509Certificate2;
        var ownsCertificate = presented is null;
        presented ??= new X509Certificate2(certificate);
        try
        {
            return string.Equals(
                RemoteRuntimeCertificateHash.Compute(presented),
                expectedHash,
                StringComparison.Ordinal);
        }
        finally
        {
            if (ownsCertificate)
                presented.Dispose();
        }
    }
}
