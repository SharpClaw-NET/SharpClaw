using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
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
            "gateway-public-key-hash",
            "proxy-1",
            certificate,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(120));

        try
        {
            session.LocalApiKey.Should().NotBeNullOrWhiteSpace();
            session.ConnectTimeout.Should().Be(TimeSpan.FromSeconds(10));
            session.ActivityTimeout.Should().Be(TimeSpan.FromSeconds(120));
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
            "gateway-public-key-hash",
            "local-key",
            "proxy-1",
            certificate,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(120));
        var nonTlsBridge = () => new RemoteRuntimeProxyConnection(
            paths,
            "http://127.0.0.1:48923",
            "http://127.0.0.1:48925",
            "gateway-public-key-hash",
            "local-key",
            "proxy-1",
            certificate,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(120));

        publicBind.Should().Throw<InvalidOperationException>();
        nonTlsBridge.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void Proxy_session_rejects_excess_local_connections_without_queueing()
    {
        var root = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "proxy-session-limit-" + Guid.NewGuid().ToString("N"));
        var paths = new SharpClawInstancePaths(
            SharpClawInstanceKind.Backend,
            Path.Combine(root, "backend"),
            Path.Combine(root, "shared"));
        using var certificate = CreateClientCertificate();
        using var session = new RemoteRuntimeProxyConnection(
            paths,
            "http://127.0.0.1:48923",
            "https://127.0.0.1:48925",
            "gateway-public-key-hash",
            "local-key",
            "proxy-1",
            certificate,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(120),
            maxConcurrentConnections: 1);

        using var first = session.TryAcquireConnection();
        first.Should().NotBeNull();
        session.TryAcquireConnection().Should().BeNull();
        first!.Dispose();
        using var replacement = session.TryAcquireConnection();
        replacement.Should().NotBeNull();
    }

    [Test]
    public void Proxy_session_rejects_protocol_major_mismatch_before_startup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Runtime:RemoteProxy:Enabled"] = "true",
                ["Runtime:RemoteProxy:LocalUrl"] = "http://127.0.0.1:48923",
                ["Runtime:RemoteProxy:GatewayUrl"] = "https://127.0.0.1:48924",
                ["Runtime:RemoteProxy:GatewayInstanceId"] = "gateway",
                ["Runtime:RemoteProxy:AuthoritativeRuntimeInstanceId"] = "runtime",
                ["Runtime:RemoteProxy:ProxyRuntimeInstanceId"] = "proxy",
                ["Runtime:RemoteProxy:InvitationSecret"] = "secret",
                ["Runtime:RemoteProxy:PrivateKeySecret"] = "private-key",
                ["Runtime:RemoteProxy:ClientCertificateSecret"] = "certificate",
                ["Runtime:RemoteProxy:ConnectTimeoutSeconds"] = "10",
                ["Runtime:RemoteProxy:ActivityTimeoutSeconds"] = "60",
                ["Runtime:RemoteProxy:BridgeProtocolMajor"] = "2",
            })
            .Build();

        var action = () => RemoteProxyOptions.Bind(configuration);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*BridgeProtocolMajor*");
    }

    [Test]
    public async Task Incomplete_pairing_invitation_fails_closed_without_creating_local_api_key()
    {
        var root = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "proxy-session-missing-" + Guid.NewGuid().ToString("N"));
        var previousInstanceRoot = Environment.GetEnvironmentVariable("SHARPCLAW_INSTANCE_ROOT");
        var previousDataDirectory = Environment.GetEnvironmentVariable("SHARPCLAW_DATA_DIR");
        Environment.SetEnvironmentVariable("SHARPCLAW_INSTANCE_ROOT", Path.Combine(root, "backend"));
        Environment.SetEnvironmentVariable("SHARPCLAW_DATA_DIR", null);

        try
        {
            var plan = new RuntimeLaunchPlan(
                RuntimeLaunchMode.RemoteProxy,
                new RemoteProxyOptionsForTests().Create());

            var action = () => RemoteProxyHost.RunAsync(plan);

            await action.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("*invitation ID*");

            Directory.Exists(Path.Combine(root, "backend", "secrets")).Should().BeTrue();
            File.Exists(Path.Combine(root, "backend", "runtime", ".api-key")).Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPCLAW_INSTANCE_ROOT", previousInstanceRoot);
            Environment.SetEnvironmentVariable("SHARPCLAW_DATA_DIR", previousDataDirectory);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
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

    private sealed class RemoteProxyOptionsForTests
    {
        public RemoteProxyOptions Create()
        {
            var options = RemoteProxyOptions.Bind(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Runtime:RemoteProxy:Enabled"] = "true",
                        ["Runtime:RemoteProxy:LocalUrl"] = "http://127.0.0.1:48923",
                        ["Runtime:RemoteProxy:GatewayUrl"] = "https://gateway.example:48925",
                        ["Runtime:RemoteProxy:GatewayInstanceId"] = "gateway-1",
                        ["Runtime:RemoteProxy:AuthoritativeRuntimeInstanceId"] = "runtime-1",
                        ["Runtime:RemoteProxy:ProxyRuntimeInstanceId"] = "proxy-1",
                        ["Runtime:RemoteProxy:InvitationSecret"] = "secret-name",
                        ["Runtime:RemoteProxy:PrivateKeySecret"] = "private-key-name",
                        ["Runtime:RemoteProxy:ClientCertificateSecret"] = "certificate-name",
                        ["Runtime:RemoteProxy:ConnectTimeoutSeconds"] = "10",
                        ["Runtime:RemoteProxy:ActivityTimeoutSeconds"] = "120",
                        ["Runtime:RemoteProxy:MaxConcurrentConnections"] = "3",
                    })
                    .Build())!;
            options.MaxConcurrentConnections.Should().Be(3);
            return options;
        }
    }
}
