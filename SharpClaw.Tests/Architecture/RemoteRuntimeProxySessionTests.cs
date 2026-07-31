using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Runtime.Host;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Tests.Architecture;

[TestFixture]
[NonParallelizable]
public sealed class RemoteRuntimeProxySessionTests
{
    [Test]
    public void Proxy_session_creates_loopback_key_and_discovery_without_gateway_token()
    {
        var root = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "proxy-session-" + Guid.NewGuid().ToString("N"));
        var paths = new SharpClawInstancePaths(
            SharpClawInstanceKind.Backend,
            Path.Combine(root, "backend"),
            Path.Combine(root, "shared"));
        using var certificate = CreateClientCertificate();
        using var session = RemoteRuntimeProxyConnection.Create(
            paths,
            "http://127.0.0.1:0",
            "https://127.0.0.1:48925",
            certificate);

        try
        {
            session.LocalApiKey.Should().NotBeNullOrWhiteSpace();
            File.ReadAllText(paths.ApiKeyFilePath).Trim().Should().Be(session.LocalApiKey);

            session.PublishDiscovery();
            var entry = JsonSerializer.Deserialize<SharpClawDiscoveryEntry>(
                File.ReadAllText(paths.DiscoveryEntryPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            entry.Should().NotBeNull();
            entry!.InstanceKind.Should().Be(SharpClawInstanceKind.Backend);
            entry.BaseUrl.Should().Be("http://127.0.0.1:0");
            entry.ApiKeyFilePath.Should().Be(paths.ApiKeyFilePath);
            entry.GatewayTokenFilePath.Should().BeNull();
        }
        finally
        {
            session.Dispose();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }

        File.Exists(paths.ApiKeyFilePath).Should().BeFalse();
        File.Exists(paths.DiscoveryEntryPath).Should().BeFalse();
    }

    [Test]
    public void Proxy_session_rejects_non_loopback_or_non_https_bridge_urls()
    {
        var root = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "proxy-session-validation-" + Guid.NewGuid().ToString("N"));
        var paths = new SharpClawInstancePaths(
            SharpClawInstanceKind.Backend,
            Path.Combine(root, "backend"),
            Path.Combine(root, "shared"));
        using var certificate = CreateClientCertificate();

        var publicBind = () => new RemoteRuntimeProxyConnection(
            paths,
            "http://0.0.0.0:48923",
            "https://127.0.0.1:48925",
            "local-key",
            certificate);
        var nonTlsBridge = () => new RemoteRuntimeProxyConnection(
            paths,
            "http://127.0.0.1:48923",
            "http://127.0.0.1:48925",
            "local-key",
            certificate);

        publicBind.Should().Throw<InvalidOperationException>();
        nonTlsBridge.Should().Throw<InvalidOperationException>();
    }

    private static X509Certificate2 CreateClientCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            "CN=proxy-session-test",
            key,
            HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(5));
    }
}
