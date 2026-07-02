using System.Text.Json;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ModuleRuntimeProtocolCoreTests
{
    [Test]
    public void ForeignModuleProtocolSurface_ComesFromCoreAssembly()
    {
        typeof(ForeignModuleProtocol).Assembly.GetName().Name
            .Should().Be("SharpClaw.Core");
        typeof(ForeignModuleHostCapabilityProtocol).Assembly.GetName().Name
            .Should().Be("SharpClaw.Core");
        typeof(ForeignModuleEndpointResponseMode).Assembly.GetName().Name
            .Should().Be("SharpClaw.Core");
        typeof(ForeignModuleCapability).Assembly.GetName().Name
            .Should().Be("SharpClaw.Core");

        ForeignModuleProtocol.Version.Should().Be(1);
        ForeignModuleProtocol.HandshakePath.Should().Be("/.sharpclaw/handshake");
        ForeignModuleHostCapabilityProtocol.TaskLaunchPath
            .Should().Be("/.sharpclaw/host/tasks/launch");
        ForeignModuleEndpointResponseMode.WebSocket.Should().Be("websocket");
        ForeignModuleCapability.ProviderPlugins.Should().Be("providerPlugins");
    }

    [Test]
    public void ModuleManifestRuntimeInfo_ParsesAndNormalizesInCore()
    {
        var runtimeInfo = ModuleManifestRuntimeInfo.FromJson(
            """
            {
              "runtime": " DOTNET ",
              "entrypoint": "ignored",
              "moduleType": "SharpClaw.Tests.FakeModule",
              "hostMode": "inprocess"
            }
            """);

        typeof(ModuleManifestRuntimeInfo).Assembly.GetName().Name
            .Should().Be("SharpClaw.Core");
        runtimeInfo.Runtime.Should().Be(ModuleManifestRuntimeInfo.DotNet);
        runtimeInfo.ModuleType.Should().Be("SharpClaw.Tests.FakeModule");
        runtimeInfo.HostMode.Should().Be(ModuleManifestRuntimeInfo.HostModeInProcess);
        runtimeInfo.IsDotNet.Should().BeTrue();
        runtimeInfo.IsInProcessHostMode.Should().BeTrue();
    }

    [Test]
    public void ModuleManifestRuntimeInfo_RejectsPathLikeDotNetEntryAssembly()
    {
        var manifest = JsonSerializer.Deserialize<SharpClaw.Contracts.Modules.ModuleManifest>(
            """
            {
              "id": "bad_module",
              "displayName": "Bad",
              "toolPrefix": "bad",
              "entryAssembly": "nested/bad.dll"
            }
            """)!;

        var act = () => ModuleManifestRuntimeInfo.DotNetDefault
            .EnsureDotNetEntryAssembly(manifest);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*entryAssembly*file name*");
    }
}
