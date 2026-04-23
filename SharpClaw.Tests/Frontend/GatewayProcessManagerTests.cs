using FluentAssertions;
using SharpClaw.Services;
using SharpClaw.Utils.Logging;

namespace SharpClaw.Tests.Frontend;

[TestFixture]
public class GatewayProcessManagerTests
{
    [Test]
    public void BundledGatewayInstanceRoot_WhenFrontendInstanceProvided_ResolvesUnderFrontendStack()
    {
        var instanceRoot = CreateTempDirectory();
        var sharedRoot = CreateTempDirectory();
        using var sessionLogs = new SessionLogWriter("gateway-tests", CreateTempDirectory());

        try
        {
            var frontendInstance = new FrontendInstanceService(
                explicitInstanceRoot: instanceRoot,
                sharedRootOverride: sharedRoot);

            var manager = new GatewayProcessManager(
                GatewayProcessManager.DefaultGatewayUrl,
                "http://127.0.0.1:48923",
                sessionLogs,
                frontendInstance);

            manager.BundledGatewayInstanceRoot.Should().Be(
                Path.Combine(frontendInstance.Paths.InstanceRoot, "stack", "gateway"));
        }
        finally
        {
            DeleteDirectoryIfExists(instanceRoot);
            DeleteDirectoryIfExists(sharedRoot);
        }
    }

    [Test]
    public void UpdateBackendBaseUrl_WhenCalled_UpdatesForwardedBackendBaseUrl()
    {
        using var sessionLogs = new SessionLogWriter("gateway-tests", CreateTempDirectory());
        var manager = new GatewayProcessManager(
            GatewayProcessManager.DefaultGatewayUrl,
            "http://127.0.0.1:48923",
            sessionLogs);

        manager.UpdateBackendBaseUrl("http://127.0.0.1:48925");

        manager.BackendBaseUrl.Should().Be("http://127.0.0.1:48925");
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
