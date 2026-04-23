using System.Text.Json;
using FluentAssertions;
using SharpClaw.Utils.Instances;
using SharpClaw.Utils.Security;

namespace SharpClaw.Tests.Persistence;

[TestFixture]
public class SharpClawInstancePathsTests
{
    [Test]
    public void Manifest_WhenCreated_PersistsBackendIdentityUnderExplicitRoot()
    {
        var instanceRoot = CreateTempDirectory();

        try
        {
            var paths = new SharpClawInstancePaths(SharpClawInstanceKind.Backend, instanceRoot);

            var manifest = paths.Manifest;

            manifest.InstanceKind.Should().Be(SharpClawInstanceKind.Backend);
            manifest.InstanceRoot.Should().Be(paths.InstanceRoot);
            manifest.DataDirectory.Should().Be(paths.DataDirectory);
            manifest.InstanceId.Should().NotBeNullOrWhiteSpace();
            File.Exists(paths.ManifestPath).Should().BeTrue();
        }
        finally
        {
            DeleteDirectoryIfExists(instanceRoot);
        }
    }

    [Test]
    public void GetOrCreate_WithInstancePaths_WritesKeyInsideSecretsDirectory()
    {
        var instanceRoot = CreateTempDirectory();

        try
        {
            var paths = new SharpClawInstancePaths(SharpClawInstanceKind.Backend, instanceRoot);

            var key = PersistentKeyStore.GetOrCreate("encryption-key", paths);
            var keyFilePath = paths.GetSecretFilePath("encryption-key");

            File.Exists(keyFilePath).Should().BeTrue();
            File.ReadAllText(keyFilePath).Trim().Should().Be(key);
        }
        finally
        {
            DeleteDirectoryIfExists(instanceRoot);
        }
    }

    [Test]
    public void PublishDiscoveryEntry_WritesRuntimeMetadataForTheInstance()
    {
        var instanceRoot = CreateTempDirectory();

        try
        {
            var paths = new SharpClawInstancePaths(SharpClawInstanceKind.Backend, instanceRoot);
            _ = paths.Manifest;

            paths.PublishDiscoveryEntry("http://127.0.0.1:48923");

            File.Exists(paths.DiscoveryEntryPath).Should().BeTrue();
            using var document = JsonDocument.Parse(File.ReadAllText(paths.DiscoveryEntryPath));
            var root = document.RootElement;
            root.GetProperty("baseUrl").GetString().Should().Be("http://127.0.0.1:48923");
            root.GetProperty("apiKeyFilePath").GetString().Should().Be(paths.ApiKeyFilePath);
            root.GetProperty("runtimeDirectory").GetString().Should().Be(paths.RuntimeDirectory);
        }
        finally
        {
            DeleteDirectoryIfExists(instanceRoot);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "SharpClaw.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
