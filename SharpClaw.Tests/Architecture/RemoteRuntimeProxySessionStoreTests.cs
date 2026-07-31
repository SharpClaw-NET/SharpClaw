using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.RemoteRuntimeBridge;

namespace SharpClaw.Tests.Architecture;

[TestFixture]
[NonParallelizable]
public sealed class RemoteRuntimeProxySessionStoreTests
{
    [Test]
    public async Task Session_store_protects_state_and_reloads_the_client_certificate()
    {
        var root = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "proxy-session-store-" + Guid.NewGuid().ToString("N"));
        var paths = new SharpClawInstancePaths(
            SharpClawInstanceKind.Backend,
            Path.Combine(root, "backend"),
            Path.Combine(root, "shared"));
        using var certificate = CreateClientCertificate();
        var pfx = certificate.Export(X509ContentType.Pfx);
        try
        {
            var state = new RemoteRuntimeProxySessionState(
                Guid.NewGuid(),
                "https://127.0.0.1:48925",
                "gateway-public-key-hash",
                "authoritative-runtime",
                "proxy-runtime",
                Convert.ToBase64String(pfx),
                DateTimeOffset.UtcNow.AddMinutes(5));

            var store = RemoteRuntimeProxySessionStore.Create(paths);
            await store.SaveAsync(state);

            var protectedBytes = await File.ReadAllBytesAsync(
                Path.Combine(paths.RemoteRuntimeProxyStateDirectory, ".env"));
            protectedBytes.Should().NotBeEmpty();
            protectedBytes[0].Should().Be(0x01);
            Encoding.UTF8.GetString(protectedBytes)
                .Should()
                .NotContain("ClientCertificatePfx");

            var restartedStore = RemoteRuntimeProxySessionStore.Create(paths);
            var restored = await restartedStore.ReadAsync();
            restored.Should().Be(state);

            using var restoredCertificate =
                await restartedStore.LoadClientCertificateAsync(restored!);
            restoredCertificate.HasPrivateKey.Should().BeTrue();
            restoredCertificate.Subject.Should().Be(certificate.Subject);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Session_store_rejects_an_expired_client_certificate_state()
    {
        var root = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "proxy-session-store-expiry-" + Guid.NewGuid().ToString("N"));
        var paths = new SharpClawInstancePaths(
            SharpClawInstanceKind.Backend,
            Path.Combine(root, "backend"),
            Path.Combine(root, "shared"));
        try
        {
            var state = new RemoteRuntimeProxySessionState(
                Guid.NewGuid(),
                "https://127.0.0.1:48925",
                "gateway-public-key-hash",
                "authoritative-runtime",
                "proxy-runtime",
                "not-used",
                DateTimeOffset.UtcNow.AddMinutes(-1));

            var store = RemoteRuntimeProxySessionStore.Create(paths);
            var save = () => store.SaveAsync(state);

            await save.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static X509Certificate2 CreateClientCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            "CN=proxy-session-store-test",
            key,
            HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(5));
    }
}
