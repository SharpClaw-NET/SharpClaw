using FluentAssertions;
using SharpClaw.Runtime.Host.Api;
using SharpClaw.Utils.Instances;

namespace SharpClaw.Tests.Api;

[TestFixture]
public class ApiKeyProviderTests
{
    [Test]
    public void Cleanup_DeletesRuntimeFilesWhenTheyStillBelongToProvider()
    {
        var instanceRoot = CreateTempDirectory();

        try
        {
            var paths = new SharpClawInstancePaths(SharpClawInstanceKind.Backend, instanceRoot);
            var provider = new ApiKeyProvider(paths);

            provider.Cleanup();

            File.Exists(paths.ApiKeyFilePath).Should().BeFalse();
            File.Exists(paths.GatewayTokenFilePath).Should().BeFalse();
        }
        finally
        {
            DeleteDirectoryIfExists(instanceRoot);
        }
    }

    [Test]
    public void Cleanup_DoesNotDeleteRuntimeFilesRotatedByAnotherProvider()
    {
        var instanceRoot = CreateTempDirectory();

        try
        {
            var paths = new SharpClawInstancePaths(SharpClawInstanceKind.Backend, instanceRoot);
            var firstProvider = new ApiKeyProvider(paths);
            var secondProvider = new ApiKeyProvider(paths);

            firstProvider.Cleanup();

            File.Exists(paths.ApiKeyFilePath).Should().BeTrue();
            File.Exists(paths.GatewayTokenFilePath).Should().BeTrue();
            File.ReadAllText(paths.ApiKeyFilePath).Trim().Should().Be(secondProvider.ApiKey);
            File.ReadAllText(paths.GatewayTokenFilePath).Trim().Should().Be(secondProvider.GatewayToken);
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
