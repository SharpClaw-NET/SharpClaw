using FluentAssertions;
using SharpClaw.Utils.Instances;

namespace SharpClaw.Tests.Gateway;

[TestFixture]
public class GatewayInstanceLifecycleTests
{
    [Test]
    public void DiscoveryLease_WhenPublishedForGateway_WritesGatewayDiscoveryEntry()
    {
        var sharedRoot = CreateTempDirectory();
        var instanceRoot = Path.Combine(sharedRoot, "instances", "gateway", "active");
        Directory.CreateDirectory(instanceRoot);

        try
        {
            var paths = new SharpClawInstancePaths(
                SharpClawInstanceKind.Gateway,
                instanceRoot,
                sharedRoot,
                Path.Combine(sharedRoot, "install-anchor"));
            _ = paths.Manifest;

            using var lease = new SharpClawDiscoveryLease(
                paths,
                "http://127.0.0.1:48924",
                TimeSpan.FromMinutes(5));

            lease.PublishNow();

            File.Exists(paths.DiscoveryEntryPath).Should().BeTrue();
        }
        finally
        {
            DeleteDirectoryIfExists(sharedRoot);
        }
    }

    [Test]
    public void DeleteDiscoveryEntry_WhenGatewayEntryExists_RemovesIt()
    {
        var sharedRoot = CreateTempDirectory();
        var instanceRoot = Path.Combine(sharedRoot, "instances", "gateway", "active");
        Directory.CreateDirectory(instanceRoot);

        try
        {
            var paths = new SharpClawInstancePaths(
                SharpClawInstanceKind.Gateway,
                instanceRoot,
                sharedRoot,
                Path.Combine(sharedRoot, "install-anchor"));
            _ = paths.Manifest;
            paths.PublishDiscoveryEntry("http://127.0.0.1:48924");

            paths.DeleteDiscoveryEntry();

            File.Exists(paths.DiscoveryEntryPath).Should().BeFalse();
        }
        finally
        {
            DeleteDirectoryIfExists(sharedRoot);
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
