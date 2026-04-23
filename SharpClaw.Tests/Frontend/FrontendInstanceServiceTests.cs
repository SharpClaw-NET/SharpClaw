using System.Text.Json;
using FluentAssertions;
using SharpClaw.Services;
using SharpClaw.Utils.Instances;

namespace SharpClaw.Tests.Frontend;

[TestFixture]
public class FrontendInstanceServiceTests
{
    [Test]
    public void Constructor_CreatesInstanceManifestAndDirectories()
    {
        var instanceRoot = CreateTempDirectory();

        try
        {
            var service = new FrontendInstanceService(instanceRoot);

            service.Paths.InstanceRoot.Should().Be(instanceRoot);
            service.Paths.Manifest.InstanceKind.Should().Be(SharpClawInstanceKind.Frontend);
            service.Paths.Manifest.InstanceId.Should().NotBeNullOrWhiteSpace();
            Directory.Exists(service.Paths.ConfigDirectory).Should().BeTrue();
            Directory.Exists(service.Paths.LogsDirectory).Should().BeTrue();
            File.Exists(service.Paths.ManifestPath).Should().BeTrue();
        }
        finally
        {
            DeleteDirectoryIfExists(instanceRoot);
        }
    }

    [Test]
    public void ResolvePreferredBackendBaseUrl_WithNonDefaultConfiguredUrl_PersistsAsConfiguredBinding()
    {
        var instanceRoot = CreateTempDirectory();

        try
        {
            var service = new FrontendInstanceService(instanceRoot);
            var configuredUrl = "http://example.com:9000";

            var result = service.ResolvePreferredBackendBaseUrl(configuredUrl);

            result.Should().Be(configuredUrl);
            var manifest = service.Paths.Manifest;
            manifest.SelectedBackendBaseUrl.Should().Be(configuredUrl);
            manifest.SelectedBackendBindingKind.Should().Be("configured");
        }
        finally
        {
            DeleteDirectoryIfExists(instanceRoot);
        }
    }

    [Test]
    public void ResolvePreferredBackendBaseUrl_WithDefaultConfiguredUrl_UsesPersistedBackendUrlWhenPresent()
    {
        var instanceRoot = CreateTempDirectory();

        try
        {
            var service = new FrontendInstanceService(instanceRoot);
            var persistedUrl = "http://127.0.0.1:48923";
            service.RememberBackendBinding("test-backend-id", persistedUrl, "discovered");

            var result = service.ResolvePreferredBackendBaseUrl("http://127.0.0.1:48923");

            result.Should().Be(persistedUrl);
            var manifest = service.Paths.Manifest;
            manifest.SelectedBackendBaseUrl.Should().Be(persistedUrl);
            manifest.SelectedBackendInstanceId.Should().Be("test-backend-id");
            manifest.SelectedBackendBindingKind.Should().Be("discovered");
        }
        finally
        {
            DeleteDirectoryIfExists(instanceRoot);
        }
    }

    [Test]
    public void ResolvePreferredBackendBaseUrl_WithDefaultUrl_DiscoversSingleBackendAndPersistsBinding()
    {
        var instanceRoot = CreateTempDirectory();
        var sharedRoot = CreateTempDirectory();

        try
        {
            var service = new FrontendInstanceService(
                explicitInstanceRoot: instanceRoot,
                sharedRootOverride: sharedRoot);

            var backendInstanceId = "backend-" + Guid.NewGuid().ToString("N");
            var backendUrl = "http://127.0.0.1:48923";
            PublishFakeBackendDiscovery(sharedRoot, backendInstanceId, backendUrl);

            var result = service.ResolvePreferredBackendBaseUrl("http://127.0.0.1:48923");

            result.Should().Be(backendUrl);
            var manifest = service.Paths.Manifest;
            manifest.SelectedBackendBaseUrl.Should().Be(backendUrl);
            manifest.SelectedBackendInstanceId.Should().Be(backendInstanceId);
            manifest.SelectedBackendBindingKind.Should().Be("discovered");
        }
        finally
        {
            DeleteDirectoryIfExists(instanceRoot);
            DeleteDirectoryIfExists(sharedRoot);
        }
    }

    [Test]
    public void ResolvePreferredBackendBaseUrl_WithMultipleDiscoveredBackends_ReturnsDefaultUrl()
    {
        var instanceRoot = CreateTempDirectory();
        var sharedRoot = CreateTempDirectory();

        try
        {
            var service = new FrontendInstanceService(
                explicitInstanceRoot: instanceRoot,
                sharedRootOverride: sharedRoot);

            PublishFakeBackendDiscovery(sharedRoot, "backend-1", "http://127.0.0.1:48923");
            PublishFakeBackendDiscovery(sharedRoot, "backend-2", "http://127.0.0.1:48924");

            var result = service.ResolvePreferredBackendBaseUrl("http://127.0.0.1:48923");

            result.Should().Be("http://127.0.0.1:48923");
        }
        finally
        {
            DeleteDirectoryIfExists(instanceRoot);
            DeleteDirectoryIfExists(sharedRoot);
        }
    }

    [Test]
    public void ResolveBackendApiKeyPath_WithMatchingBackendInstanceId_ReturnsApiKeyPath()
    {
        var instanceRoot = CreateTempDirectory();
        var sharedRoot = CreateTempDirectory();

        try
        {
            var service = new FrontendInstanceService(
                explicitInstanceRoot: instanceRoot,
                sharedRootOverride: sharedRoot);

            var backendInstanceId = "backend-" + Guid.NewGuid().ToString("N");
            var backendUrl = "http://127.0.0.1:48923";
            var apiKeyPath = Path.Combine(sharedRoot, "backend-runtime", ".api-key");
            PublishFakeBackendDiscovery(sharedRoot, backendInstanceId, backendUrl, apiKeyPath);

            service.RememberBackendBinding(backendInstanceId, backendUrl, "discovered");

            var result = service.ResolveBackendApiKeyPath(backendUrl);

            result.Should().Be(apiKeyPath);
        }
        finally
        {
            DeleteDirectoryIfExists(instanceRoot);
            DeleteDirectoryIfExists(sharedRoot);
        }
    }

    [Test]
    public void ResolveBackendApiKeyPath_WithNoMatchingBackend_ReturnsNull()
    {
        var instanceRoot = CreateTempDirectory();
        var sharedRoot = CreateTempDirectory();

        try
        {
            var service = new FrontendInstanceService(
                explicitInstanceRoot: instanceRoot,
                sharedRootOverride: sharedRoot);

            var result = service.ResolveBackendApiKeyPath("http://127.0.0.1:48923");

            result.Should().BeNull();
        }
        finally
        {
            DeleteDirectoryIfExists(instanceRoot);
            DeleteDirectoryIfExists(sharedRoot);
        }
    }

    [Test]
    public void ResolveBackendApiKeyPath_WithSingleDiscoveredBackend_ReturnsApiKeyPathAndPersistsBinding()
    {
        var instanceRoot = CreateTempDirectory();
        var sharedRoot = CreateTempDirectory();

        try
        {
            var service = new FrontendInstanceService(
                explicitInstanceRoot: instanceRoot,
                sharedRootOverride: sharedRoot);

            var backendInstanceId = "backend-" + Guid.NewGuid().ToString("N");
            var backendUrl = "http://127.0.0.1:48923";
            var apiKeyPath = Path.Combine(sharedRoot, "backend-runtime", ".api-key");
            PublishFakeBackendDiscovery(sharedRoot, backendInstanceId, backendUrl, apiKeyPath);

            var result = service.ResolveBackendApiKeyPath();

            result.Should().Be(apiKeyPath);
            var manifest = service.Paths.Manifest;
            manifest.SelectedBackendInstanceId.Should().Be(backendInstanceId);
            manifest.SelectedBackendBaseUrl.Should().Be(backendUrl);
            manifest.SelectedBackendBindingKind.Should().Be("discovered");
        }
        finally
        {
            DeleteDirectoryIfExists(instanceRoot);
            DeleteDirectoryIfExists(sharedRoot);
        }
    }

    [Test]
    public void RememberBackendBinding_UpdatesManifestAndPersists()
    {
        var instanceRoot = CreateTempDirectory();

        try
        {
            var service = new FrontendInstanceService(instanceRoot);
            var backendId = "backend-test-id";
            var backendUrl = "http://127.0.0.1:48923";

            service.RememberBackendBinding(backendId, backendUrl, "discovered");

            var manifest = service.Paths.Manifest;
            manifest.SelectedBackendInstanceId.Should().Be(backendId);
            manifest.SelectedBackendBaseUrl.Should().Be(backendUrl);
            manifest.SelectedBackendBindingKind.Should().Be("discovered");

            var manifestOnDisk = LoadManifestFromDisk(service.Paths.ManifestPath);
            manifestOnDisk.SelectedBackendInstanceId.Should().Be(backendId);
            manifestOnDisk.SelectedBackendBaseUrl.Should().Be(backendUrl);
            manifestOnDisk.SelectedBackendBindingKind.Should().Be("discovered");
        }
        finally
        {
            DeleteDirectoryIfExists(instanceRoot);
        }
    }

    [Test]
    public void RememberBackendBinding_OnlyPersistsWhenChanged()
    {
        var instanceRoot = CreateTempDirectory();

        try
        {
            var service = new FrontendInstanceService(instanceRoot);
            var backendId = "backend-test-id";
            var backendUrl = "http://127.0.0.1:48923";

            service.RememberBackendBinding(backendId, backendUrl, "discovered");
            var manifestPath = service.Paths.ManifestPath;
            var firstWriteTime = File.GetLastWriteTimeUtc(manifestPath);

            Thread.Sleep(100);

            service.RememberBackendBinding(backendId, backendUrl, "discovered");
            var secondWriteTime = File.GetLastWriteTimeUtc(manifestPath);

            secondWriteTime.Should().Be(firstWriteTime);
        }
        finally
        {
            DeleteDirectoryIfExists(instanceRoot);
        }
    }

    [Test]
    public void BundledBackendInstanceRoot_ReturnsPathUnderFrontendInstanceRoot()
    {
        var instanceRoot = CreateTempDirectory();

        try
        {
            var service = new FrontendInstanceService(instanceRoot);

            var bundledRoot = service.BundledBackendInstanceRoot;

            bundledRoot.Should().Be(Path.Combine(instanceRoot, "stack", "backend"));
        }
        finally
        {
            DeleteDirectoryIfExists(instanceRoot);
        }
    }

    [Test]
    public void AccountsPath_ReturnsPathUnderConfigDirectory()
    {
        var instanceRoot = CreateTempDirectory();

        try
        {
            var service = new FrontendInstanceService(instanceRoot);

            service.AccountsPath.Should().Be(Path.Combine(service.Paths.ConfigDirectory, "accounts.json"));
        }
        finally
        {
            DeleteDirectoryIfExists(instanceRoot);
        }
    }

    [Test]
    public void ClientSettingsPath_ReturnsPathUnderConfigDirectory()
    {
        var instanceRoot = CreateTempDirectory();

        try
        {
            var service = new FrontendInstanceService(instanceRoot);

            service.ClientSettingsPath.Should().Be(Path.Combine(service.Paths.ConfigDirectory, "client-settings.json"));
        }
        finally
        {
            DeleteDirectoryIfExists(instanceRoot);
        }
    }

    [Test]
    public void SetupMarkerPath_ReturnsPathUnderConfigDirectory()
    {
        var instanceRoot = CreateTempDirectory();

        try
        {
            var service = new FrontendInstanceService(instanceRoot);

            service.SetupMarkerPath.Should().Be(Path.Combine(service.Paths.ConfigDirectory, ".setup-complete"));
        }
        finally
        {
            DeleteDirectoryIfExists(instanceRoot);
        }
    }

    [Test]
    public void GetUserSettingsPath_ReturnsPerUserPathUnderUsersDirectory()
    {
        var instanceRoot = CreateTempDirectory();

        try
        {
            var service = new FrontendInstanceService(instanceRoot);
            var userId = Guid.NewGuid();

            var path = service.GetUserSettingsPath(userId);

            path.Should().Be(Path.Combine(
                service.UsersSettingsDirectory,
                userId.ToString("N"),
                "settings.json"));
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

    private static void PublishFakeBackendDiscovery(
        string sharedRoot,
        string instanceId,
        string baseUrl,
        string? apiKeyPath = null)
    {
        var discoveryDir = Path.Combine(sharedRoot, "discovery", "instances");
        Directory.CreateDirectory(discoveryDir);

        var discoveryPath = Path.Combine(discoveryDir, $"backend-{instanceId}.json");
        var runtimeDir = Path.Combine(sharedRoot, "runtime");
        var entry = new SharpClawDiscoveryEntry
        {
            InstanceKind = SharpClawInstanceKind.Backend,
            InstanceId = instanceId,
            InstallFingerprint = "test-fingerprint",
            InstanceRoot = Path.Combine(sharedRoot, "instances", instanceId),
            BaseUrl = baseUrl,
            ApiKeyFilePath = apiKeyPath ?? Path.Combine(runtimeDir, ".api-key"),
            RuntimeDirectory = runtimeDir,
            ProcessId = 12345,
            StartedAtUtc = DateTimeOffset.UtcNow,
            LastSeenUtc = DateTimeOffset.UtcNow,
        };

        var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        });

        File.WriteAllText(discoveryPath, json);
    }

    private static SharpClawInstanceManifest LoadManifestFromDisk(string manifestPath)
    {
        var json = File.ReadAllText(manifestPath);
        return JsonSerializer.Deserialize<SharpClawInstanceManifest>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        })!;
    }
}
