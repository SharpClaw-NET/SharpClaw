using System.Text;
using Microsoft.Extensions.Configuration;
using SharpClaw.Runtime.INF.Configuration;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.Security;
using Supprocom.Secrets;

namespace SharpClaw.Tests.Persistence;

[TestFixture]
[NonParallelizable]
public sealed class InstallationKeyCompatibilityTests
{
    [Test]
    public async Task FreshHostOptions_CreateRawPackageKeyAndShareItWithSharpClawConsumers()
    {
        using var environment = EnvironmentOverride.Clear();
        using var workspace = TestWorkspace.Create();
        workspace.Write(".env.template", "Admin__Username=FreshAdmin\n");

        var configuration = new ConfigurationBuilder()
            .AddLocalEnvironmentFrom(workspace.EnvironmentDirectory, false, workspace.Paths)
            .Build();

        configuration["Admin:Username"].Should().Be("FreshAdmin");
        var rawKey = File.ReadAllBytes(workspace.KeyPath);
        rawKey.Should().HaveCount(32);
        (await new SharpClawInstallationKeyStore(workspace.KeyPath).GetOrCreateKeyAsync())
            .Should().Equal(rawKey);
        Convert.FromBase64String(PersistentKeyStore.GetOrCreate("encryption-key", workspace.Paths))
            .Should().Equal(rawKey);
        EncryptionKeyResolver.ResolveKey(workspace.Paths).Should().Equal(rawKey);
    }

    [Test]
    public async Task ExistingBase64KeyAndEncryptedJsonImport_MigrateAndKeepSharedKeyIdentity()
    {
        using var environment = EnvironmentOverride.Clear();
        using var workspace = TestWorkspace.Create();
        workspace.Write(".env.template", "Admin__Username=TemplateAdmin\n");
        var expectedKey = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(workspace.KeyPath)!);
        File.WriteAllText(workspace.KeyPath, Convert.ToBase64String(expectedKey));
        var legacyJson = Encoding.UTF8.GetBytes(
            "{\n" +
            "  // legacy installation document\n" +
            "  \"Admin\": { \"Username\": \"ImportedAdmin\" },\n" +
            "}\n");
        File.WriteAllBytes(workspace.Path(".env"), ApiKeyEncryptor.EncryptBytes(legacyJson, expectedKey));

        var configuration = new ConfigurationBuilder()
            .AddLocalEnvironmentFrom(workspace.EnvironmentDirectory, false, workspace.Paths)
            .Build();
        var store = new SupprocomSecretFileStore(LocalEnvironment.CreateSecretsOptions(
            workspace.EnvironmentDirectory,
            false,
            workspace.Paths));

        configuration["Admin:Username"].Should().Be("ImportedAdmin");
        File.ReadAllBytes(workspace.KeyPath).Should().Equal(expectedKey);
        (await store.ReadDocumentAsync()).Should().Contain("Admin__Username=\"ImportedAdmin\"");
        EncryptionKeyResolver.ResolveKey(workspace.Paths).Should().Equal(expectedKey);
        Convert.FromBase64String(PersistentKeyStore.GetOrCreate("encryption-key", workspace.Paths))
            .Should().Equal(expectedKey);
    }

    [Test]
    public async Task RawKey_RestartsPackageProtectionAndKeepsTheSameKey()
    {
        using var environment = EnvironmentOverride.Clear();
        using var workspace = TestWorkspace.Create();
        workspace.Write(".env.template", "Api__Url=http://127.0.0.1:48923\n");
        var expectedKey = Enumerable.Range(33, 32).Select(value => (byte)value).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(workspace.KeyPath)!);
        File.WriteAllBytes(workspace.KeyPath, expectedKey);

        var options = LocalEnvironment.CreateSecretsOptions(
            workspace.EnvironmentDirectory,
            false,
            workspace.Paths);
        var store = new SupprocomSecretFileStore(options);
        await store.ReplaceDocumentAsync("Api__Url=http://127.0.0.1:48924\n");

        var restarted = new SupprocomSecretFileStore(LocalEnvironment.CreateSecretsOptions(
            workspace.EnvironmentDirectory,
            false,
            workspace.Paths));

        (await restarted.ReadDocumentAsync()).Should().Contain("Api__Url=http://127.0.0.1:48924");
        EncryptionKeyResolver.ResolveKey(workspace.Paths).Should().Equal(expectedKey);
    }

    [Test]
    public async Task EnvironmentOnlyKey_DecryptsExistingActiveFileWithoutCreatingAKeyFile()
    {
        using var environment = EnvironmentOverride.Clear();
        using var workspace = TestWorkspace.Create();
        workspace.Write(".env.template", "Admin__Username=TemplateAdmin\n");
        var expectedKey = Enumerable.Range(65, 32).Select(value => (byte)value).ToArray();
        workspace.Paths.EnsureDirectories();

        var fileStore = new SupprocomSecretFileStore(new SupprocomSecretsOptions
        {
            File =
            {
                Directory = workspace.EnvironmentDirectory,
                ActiveName = ".env",
                TemplateName = ".env.template",
                Protection = SecretFileProtection.InstallationBoundAesGcm,
                InstallationKeyPath = workspace.KeyPath
            }
        });
        File.WriteAllBytes(workspace.KeyPath, expectedKey);
        await fileStore.ReplaceDocumentAsync("Admin__Username=EnvironmentAdmin\n");
        File.Delete(workspace.KeyPath);

        using var configured = EnvironmentOverride.Set(Convert.ToBase64String(expectedKey));
        var configuration = new ConfigurationBuilder()
            .AddLocalEnvironmentFrom(workspace.EnvironmentDirectory, false, workspace.Paths)
            .Build();
        var restarted = new SupprocomSecretFileStore(LocalEnvironment.CreateSecretsOptions(
            workspace.EnvironmentDirectory,
            false,
            workspace.Paths));

        configuration["Admin:Username"].Should().Be("EnvironmentAdmin");
        (await restarted.ReadDocumentAsync()).Should().Contain("Admin__Username=EnvironmentAdmin");
        File.Exists(workspace.KeyPath).Should().BeFalse();
        EncryptionKeyResolver.ResolveKey(workspace.Paths).Should().Equal(expectedKey);
    }

    [Test]
    public void InvalidEnvironmentOnlyKey_FailsExplicitly()
    {
        using var environment = EnvironmentOverride.Set("not-a-key");
        using var workspace = TestWorkspace.Create();

        var exception = Assert.Throws<SupprocomSecretsException>(() =>
            SharpClawInstallationKeyStore.GetConfiguredKeyBase64OrNull());

        exception!.Code.Should().Be("InvalidInstallationKey");
        Assert.Throws<SupprocomSecretsException>(() =>
            EncryptionKeyResolver.ResolveKey(workspace.Paths));
        File.Exists(workspace.KeyPath).Should().BeFalse();
    }

    private sealed class EnvironmentOverride : IDisposable
    {
        private readonly string? previous;

        private EnvironmentOverride(string? value)
        {
            previous = Environment.GetEnvironmentVariable("SHARPCLAW_ENCRYPTION_KEY");
            Environment.SetEnvironmentVariable("SHARPCLAW_ENCRYPTION_KEY", value);
        }

        public static EnvironmentOverride Clear() => new(null);
        public static EnvironmentOverride Set(string value) => new(value);

        public void Dispose() =>
            Environment.SetEnvironmentVariable("SHARPCLAW_ENCRYPTION_KEY", previous);
    }

    private sealed class TestWorkspace : IDisposable
    {
        private TestWorkspace(string root)
        {
            Root = root;
            EnvironmentDirectory = System.IO.Path.Combine(root, "Environment");
            Paths = new SharpClawInstancePaths(
                SharpClawInstanceKind.Backend,
                System.IO.Path.Combine(root, "instance"));
            KeyPath = Paths.GetSecretFilePath("encryption-key");
            Directory.CreateDirectory(EnvironmentDirectory);
        }

        public string Root { get; }
        public string EnvironmentDirectory { get; }
        public SharpClawInstancePaths Paths { get; }
        public string KeyPath { get; }

        public static TestWorkspace Create() => new(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "SharpClaw.Tests",
            Guid.NewGuid().ToString("N")));

        public string Path(string fileName) => System.IO.Path.Combine(EnvironmentDirectory, fileName);

        public void Write(string fileName, string content) => File.WriteAllText(Path(fileName), content);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
