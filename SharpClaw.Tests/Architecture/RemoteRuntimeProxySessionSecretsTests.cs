using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentAssertions;
using SharpClaw.Runtime.Host;
using SharpClaw.Runtime.INF.Configuration;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.RemoteRuntimeBridge;
using Supprocom.Secrets;

namespace SharpClaw.Tests.Architecture;

[TestFixture]
[NonParallelizable]
public sealed class RemoteRuntimeProxySessionSecretsTests
{
    [Test]
    public async Task Session_secrets_protect_state_reload_certificate_and_preserve_environment_settings()
    {
        using var workspace = SessionWorkspace.Create();
        using var certificate = CreateClientCertificate();
        var pfx = certificate.Export(X509ContentType.Pfx);
        try
        {
            var state = CreateState(pfx, DateTimeOffset.UtcNow.AddMinutes(5));
            var secrets = RemoteRuntimeProxySessionSecrets.Create(
                workspace.EnvironmentDirectory,
                workspace.Paths);

            await secrets.SaveAsync(state);

            var activeBytes = await File.ReadAllBytesAsync(workspace.ActivePath);
            activeBytes.Should().NotBeEmpty();
            activeBytes[0].Should().Be(0x01);
            Encoding.UTF8.GetString(activeBytes).Should().NotContain(state.ClientCertificatePfxBase64);
            Directory.Exists(workspace.LegacyProxyDirectory).Should().BeFalse();

            var store = new SupprocomSecretFileStore(
                LocalEnvironment.CreateSecretsOptions(
                    workspace.EnvironmentDirectory,
                    isDevelopment: false,
                    workspace.Paths));
            var document = SupprocomSecretDocument.Parse(
                await store.ReadDocumentAsync());
            document.Settings.Should().Contain(setting =>
                setting.Key == "Test:Unrelated" && setting.Value == "keep");

            var restarted = RemoteRuntimeProxySessionSecrets.Create(
                workspace.EnvironmentDirectory,
                workspace.Paths);
            var restored = await restarted.ReadAsync();
            restored.Should().Be(state);

            using var restoredCertificate =
                await restarted.LoadClientCertificateAsync(restored!);
            restoredCertificate.HasPrivateKey.Should().BeTrue();
            restoredCertificate.Subject.Should().Be(certificate.Subject);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    [Test]
    public async Task Session_secrets_reject_expired_state_before_writing()
    {
        using var workspace = SessionWorkspace.Create();
        var state = CreateState(
            Encoding.UTF8.GetBytes("not-a-certificate"),
            DateTimeOffset.UtcNow.AddMinutes(-1));
        var secrets = RemoteRuntimeProxySessionSecrets.Create(
            workspace.EnvironmentDirectory,
            workspace.Paths);

        var action = () => secrets.SaveAsync(state);

        await action.Should().ThrowAsync<InvalidOperationException>();
        File.Exists(workspace.ActivePath).Should().BeFalse();
    }

    [Test]
    public async Task Session_secrets_use_configured_private_key_and_certificate_selectors()
    {
        using var workspace = SessionWorkspace.Create();
        using var certificate = CreateClientCertificate();
        var pfx = certificate.Export(X509ContentType.Pfx);
        try
        {
            var state = CreateState(pfx, DateTimeOffset.UtcNow.AddMinutes(5));
            var secrets = RemoteRuntimeProxySessionSecrets.Create(
                workspace.EnvironmentDirectory,
                workspace.Paths,
                "Proxy:PrivateKey",
                "Proxy:Certificate");

            await secrets.SaveAsync(state);

            var document = SupprocomSecretDocument.Parse(
                await new SupprocomSecretFileStore(
                    LocalEnvironment.CreateSecretsOptions(
                        workspace.EnvironmentDirectory,
                        isDevelopment: false,
                        workspace.Paths))
                    .ReadDocumentAsync());
            document.Settings.Should().Contain(setting => setting.Key == "Proxy:PrivateKey");
            document.Settings.Should().Contain(setting => setting.Key == "Proxy:Certificate");

            var restarted = RemoteRuntimeProxySessionSecrets.Create(
                workspace.EnvironmentDirectory,
                workspace.Paths,
                "Proxy:PrivateKey",
                "Proxy:Certificate");
            using var privateKey = await restarted.LoadPrivateKeyAsync(state);
            privateKey.ExportPkcs8PrivateKey().Should().NotBeEmpty();
            (await restarted.ReadAsync()).Should().BeEquivalentTo(state);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    private static RemoteRuntimeProxySessionState CreateState(
        byte[] pfx,
        DateTimeOffset expiresAtUtc)
        => new(
            Guid.NewGuid(),
            "https://127.0.0.1:48925",
            "gateway-public-key-hash",
            "authoritative-runtime",
            "proxy-runtime",
            Convert.ToBase64String(pfx),
            expiresAtUtc,
            "runtime-install-fingerprint",
            "test-certificate-thumbprint",
            RemoteRuntimeBridgePaths.CurrentProtocolMajor);

    private static X509Certificate2 CreateClientCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            "CN=proxy-session-secrets-test",
            key,
            HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(5));
    }

    private sealed class SessionWorkspace : IDisposable
    {
        private SessionWorkspace(string root)
        {
            Root = root;
            EnvironmentDirectory = Path.Combine(root, "Environment");
            Paths = new SharpClawInstancePaths(
                SharpClawInstanceKind.Backend,
                Path.Combine(root, "backend"),
                Path.Combine(root, "shared"));
            Directory.CreateDirectory(EnvironmentDirectory);
            File.WriteAllText(
                Path.Combine(EnvironmentDirectory, ".env.template"),
                "Test__Unrelated=\"keep\"\n");
            File.WriteAllText(
                Path.Combine(EnvironmentDirectory, ".dev.env.template"),
                "Test__Unrelated=\"keep\"\n");
        }

        public string Root { get; }
        public string EnvironmentDirectory { get; }
        public SharpClawInstancePaths Paths { get; }
        public string ActivePath => Path.Combine(EnvironmentDirectory, ".env");
        public string LegacyProxyDirectory =>
            Path.Combine(Paths.SecretsDirectory, "remote-runtime-proxy");

        public static SessionWorkspace Create()
            => new(Path.Combine(
                Path.GetTempPath(),
                "SharpClaw.Tests",
                "remote-runtime-session-secrets",
                Guid.NewGuid().ToString("N")));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
