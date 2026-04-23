using FluentAssertions;
using SharpClaw.Utils.Instances;
using System.Text.Json;

namespace SharpClaw.Tests.Persistence;

[TestFixture]
public class SharpClawInstanceLifecycleTests
{
    [Test]
    public void CleanupStaleDiscoveryEntries_RemovesExpiredEntry()
    {
        var sharedRoot = CreateTempDirectory();
        var activeInstanceRoot = Path.Combine(sharedRoot, "instances", "backend", "active");
        var staleInstanceRoot = Path.Combine(sharedRoot, "instances", "backend", "stale");
        Directory.CreateDirectory(activeInstanceRoot);
        Directory.CreateDirectory(staleInstanceRoot);
        File.WriteAllText(Path.Combine(staleInstanceRoot, "instance.json"), "{}");

        try
        {
            var paths = new SharpClawInstancePaths(
                SharpClawInstanceKind.Backend,
                activeInstanceRoot,
                sharedRoot,
                Path.Combine(sharedRoot, "install-anchor"));
            _ = paths.Manifest;
            paths.PublishDiscoveryEntry("http://127.0.0.1:48923");

            var staleEntryPath = Path.Combine(paths.DiscoveryDirectory, "backend-stale.json");
            File.WriteAllText(
                staleEntryPath,
                """
                {
                  "schemaVersion": 1,
                  "instanceKind": "Backend",
                  "instanceId": "stale-instance",
                  "installFingerprint": "stale",
                  "instanceRoot": "REPLACE_ROOT",
                  "baseUrl": "http://127.0.0.1:48999",
                  "runtimeDirectory": "REPLACE_RUNTIME",
                  "apiKeyFilePath": "REPLACE_API",
                  "gatewayTokenFilePath": "REPLACE_GATEWAY",
                  "processId": 999999,
                  "startedAtUtc": "2000-01-01T00:00:00Z",
                  "lastSeenUtc": "2000-01-01T00:00:00Z"
                }
                """
                .Replace("REPLACE_ROOT", EscapeJson(staleInstanceRoot))
                .Replace("REPLACE_RUNTIME", EscapeJson(Path.Combine(staleInstanceRoot, "runtime")))
                .Replace("REPLACE_API", EscapeJson(Path.Combine(staleInstanceRoot, "runtime", ".api-key")))
                .Replace("REPLACE_GATEWAY", EscapeJson(Path.Combine(staleInstanceRoot, "runtime", ".gateway-token"))));

            paths.CleanupStaleDiscoveryEntries(TimeSpan.FromMinutes(2));

            File.Exists(paths.DiscoveryEntryPath).Should().BeTrue();
            File.Exists(staleEntryPath).Should().BeFalse();
        }
        finally
        {
            DeleteDirectoryIfExists(sharedRoot);
        }
    }

    [Test]
    public void DiscoveryLease_PublishNow_WritesAndRefreshesLeaseTimestamp()
    {
        var sharedRoot = CreateTempDirectory();
        var instanceRoot = Path.Combine(sharedRoot, "instances", "backend", "active");
        Directory.CreateDirectory(instanceRoot);

        try
        {
            var paths = new SharpClawInstancePaths(
                SharpClawInstanceKind.Backend,
                instanceRoot,
                sharedRoot,
                Path.Combine(sharedRoot, "install-anchor"));
            _ = paths.Manifest;

            using var lease = new SharpClawDiscoveryLease(
                paths,
                "http://127.0.0.1:48923",
                TimeSpan.FromMinutes(5));

            lease.PublishNow();
            var firstSeen = ReadLastSeen(paths.DiscoveryEntryPath);

            Thread.Sleep(20);

            lease.PublishNow();
            var secondSeen = ReadLastSeen(paths.DiscoveryEntryPath);

            secondSeen.Should().BeAfter(firstSeen);
        }
        finally
        {
            DeleteDirectoryIfExists(sharedRoot);
        }
    }

    [Test]
    public void InstanceLock_WhenAcquiredTwiceForSameRoot_Throws()
    {
        var instanceRoot = CreateTempDirectory();

        try
        {
            var paths = new SharpClawInstancePaths(SharpClawInstanceKind.Backend, instanceRoot);
            using var firstLock = new SharpClawInstanceLock(paths);

            var act = () => new SharpClawInstanceLock(paths);

            act.Should().Throw<InvalidOperationException>();
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

    private static string EscapeJson(string value) => value.Replace("\\", "\\\\");

    private static DateTimeOffset ReadLastSeen(string discoveryEntryPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(discoveryEntryPath));
        return document.RootElement.GetProperty("lastSeenUtc").GetDateTimeOffset();
    }
}
