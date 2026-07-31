using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.RemoteRuntimeBridge;
using SharpClaw.Shared.Instances;
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
        var paths = new SharpClawInstancePaths(
            SharpClawInstanceKind.Gateway,
            Path.Combine(root, "gateway"),
            Path.Combine(root, "shared"));
        var certificatePath = Path.Combine(root, "bridge.pfx");
        using var serverCertificate = CreateCertificate("CN=bridge-revocation-test");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(certificatePath, serverCertificate.Export(X509ContentType.Pfx));

        var pairingStore = RemoteRuntimePairingStore.Create(paths);
        var invitation = await pairingStore.CreateInvitationAsync(
            "gateway-1",
            RemoteRuntimeCertificateHash.Compute(serverCertificate),
            "runtime-1",
            "runtime-install-1",
            TimeSpan.FromMinutes(5));
        using var clientCertificate = await ClaimAndIssueCertificateAsync(
            pairingStore,
            invitation,
            "proxy-1");
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
        var target = new RemoteRuntimeBridgeTarget(
            "gateway-1",
            "runtime-1",
            "runtime-install-1",
            "http://127.0.0.1:1",
            "authoritative-api-key");
        var options = new RemoteRuntimeBridgeOptions
        {
            Enabled = true,
            ListenUrl = $"https://127.0.0.1:{GetFreePort()}",
            ServerCertificatePath = certificatePath,
        };
        await using var app = await RemoteRuntimeBridgeHost.BuildAsync(
            [],
            options,
            pairingStore,
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

            var revoked = await pairingStore.RevokeAsync(invitation.PairId);
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

    private static async Task<X509Certificate2> ClaimAndIssueCertificateAsync(
        RemoteRuntimePairingStore pairingStore,
        RemoteRuntimePairingInvitation invitation,
        string proxyRuntimeInstanceId)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            $"CN={proxyRuntimeInstanceId}",
            key,
            HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.2")],
                true));
        var csr = request.CreateSigningRequest();
        var publicKeyHash = RemoteRuntimeCertificateHash.Compute(key);
        var proofPayload = RemoteRuntimePairingStore.CreateClaimProofPayload(
            invitation,
            proxyRuntimeInstanceId,
            publicKeyHash);
        var proof = key.SignData(proofPayload, HashAlgorithmName.SHA256);
        try
        {
            await pairingStore.ClaimInvitationAsync(
                invitation.PairId,
                invitation.Secret,
                proxyRuntimeInstanceId,
                Convert.ToBase64String(csr),
                Convert.ToBase64String(proof));
            await pairingStore.ApproveClaimAsync(
                invitation.PairId,
                proxyRuntimeInstanceId,
                invitation.AuthoritativeRuntimeInstanceId);
            var issued = await pairingStore.IssueClientCertificateAsync(invitation.PairId);
            using var publicCertificate = X509CertificateLoader.LoadCertificate(issued.CertificateDer);
            using var certificateWithKey = publicCertificate.CopyWithPrivateKey(key);
            var pfx = certificateWithKey.Export(X509ContentType.Pfx);
            try
            {
                return X509CertificateLoader.LoadPkcs12(
                    pfx,
                    password: null,
                    keyStorageFlags: X509KeyStorageFlags.UserKeySet
                        | X509KeyStorageFlags.PersistKeySet
                        | X509KeyStorageFlags.Exportable);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfx);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(csr);
            CryptographicOperations.ZeroMemory(proofPayload);
            CryptographicOperations.ZeroMemory(proof);
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
