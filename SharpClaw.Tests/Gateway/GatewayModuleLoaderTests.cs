using Serilog;
using SharpClaw.Gateway.Contracts;
using SharpClaw.Gateway.Modules;

namespace SharpClaw.Tests.Gateway;

[TestFixture]
[NonParallelizable]
public sealed class GatewayModuleLoaderTests
{
    [Test]
    public void DiscoverBundled_DoesNotConstructDisabledModule()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "gateway-disabled-module-" + Guid.NewGuid().ToString("N"));
        var moduleRoot = Path.Combine(root, "disabled-probe");
        var marker = Path.Combine(root, "constructed.txt");
        Directory.CreateDirectory(moduleRoot);

        try
        {
            File.Copy(
                typeof(GatewayDisabledConstructorProbe).Assembly.Location,
                Path.Combine(moduleRoot, "SharpClaw.Tests.dll"));
            File.WriteAllText(
                Path.Combine(moduleRoot, "module.json"),
                """
                {
                  "id": "gateway_disabled_constructor_probe",
                  "entryAssembly": "SharpClaw.Tests.dll"
                }
                """);
            Environment.SetEnvironmentVariable(
                GatewayDisabledConstructorProbe.MarkerVariable,
                marker);

            using var logger = new LoggerConfiguration().CreateLogger();
            var loader = GatewayModuleLoader.DiscoverBundled(
                logger,
                new GatewayModuleOptions(),
                root);

            loader.AllModuleIds.Should().ContainSingle()
                .Which.Should().Be("gateway_disabled_constructor_probe");
            File.Exists(marker).Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                GatewayDisabledConstructorProbe.MarkerVariable,
                null);
            Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class GatewayDisabledConstructorProbe : IGatewayModuleExtension
{
    public const string MarkerVariable = "SHARPCLAW_GATEWAY_CONSTRUCTOR_MARKER";

    public GatewayDisabledConstructorProbe()
    {
        var marker = Environment.GetEnvironmentVariable(MarkerVariable);
        if (!string.IsNullOrWhiteSpace(marker))
            File.WriteAllText(marker, "constructed");
    }

    public string ModuleId => "gateway_disabled_constructor_probe";

    public string DisplayName => "Disabled constructor probe";

    public IReadOnlyList<GatewayEndpointGroup> GetEndpointGroups() => [];

    public void MapEndpoints(IGatewayEndpointGroupBuilder builder)
    {
    }
}
