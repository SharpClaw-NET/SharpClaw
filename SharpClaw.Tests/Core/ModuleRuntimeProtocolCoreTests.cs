using System.Text.Json;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Modules.Sidecar;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class RegistrationRuntimeProtocolCoreTests
{
    [Test]
    public void ForeignRegistrationProtocolSurface_ComesFromContractsAssembly()
    {
        typeof(ForeignRegistrationProtocol).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(ForeignRegistrationHostCapabilityProtocol).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(ForeignEndpointResponseMode).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(ForeignRegistrationCapability).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");

        ForeignRegistrationProtocol.Version.Should().Be(1);
        ForeignRegistrationProtocol.HandshakePath.Should().Be("/.sharpclaw/handshake");
        ForeignEndpointResponseMode.WebSocket.Should().Be("websocket");
        ForeignRegistrationCapability.ProviderPlugins.Should().Be("providerPlugins");
    }

    [Test]
    public void ForeignRegistrationSidecarProtocolModels_ComeFromContractsAndUseCoreMappers()
    {
        typeof(ForeignRegistrationHandshakeRequest).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(ForeignRegistrationDiscoveryResponse).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(ForeignRegistrationToolDescriptor).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(ForeignRegistrationProtocolContractInvocationRequest).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(ForeignRegistrationProviderChatCompletionRequest).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(ForeignRegistrationProviderPluginDescriptor).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(ForeignRegistrationProtocolModelMapper).Assembly.GetName().Name
            .Should().Be("SharpClaw.Core");

        typeof(SharpClaw.Contracts.Kernel.PackageManifest).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(SharpClaw.Contracts.Providers.ChatCompletionMessage).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(SharpClaw.Contracts.Providers.ProviderCostSeed).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");

        var schema = JsonSerializer.SerializeToElement(new { type = "object" });
        var descriptor = new ForeignRegistrationToolDescriptor(
            "sample",
            "Sample tool",
            schema,
            Permission: new ForeignRegistrationPermissionDescriptor(IsPerResource: false));

        descriptor.ToRegistrationToolDefinition().Name.Should().Be("sample");

        var health = new ForeignRegistrationHealthResponse(true, Details: new Dictionary<string, JsonElement>
        {
            ["queueDepth"] = JsonSerializer.SerializeToElement(3),
        });

        health.ToRegistrationHealthStatus().Details.Should().ContainKey("queueDepth");
    }

    [Test]
    public void PackageManifestRuntimeInfo_ParsesAndNormalizesInContracts()
    {
        var runtimeInfo = PackageRuntimeInfo.FromJson(
            """
            {
              "runtime": " DOTNET ",
              "entryType": "SharpClaw.Tests.FakeRegistration",
              "hostMode": "inprocess"
            }
            """);

        typeof(PackageRuntimeInfo).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        runtimeInfo.Runtime.Should().Be(PackageRuntimeInfo.DotNet);
        runtimeInfo.EntryType.Should().Be("SharpClaw.Tests.FakeRegistration");
        runtimeInfo.HostMode.Should().Be(PackageRuntimeInfo.HostModeInProcess);
        runtimeInfo.IsDotNet.Should().BeTrue();
        runtimeInfo.IsInProcessHostMode.Should().BeTrue();
    }

    [Test]
    public void PackageManifestRuntimeInfo_RejectsPathLikeDotNetEntryAssembly()
    {
        var manifest = JsonSerializer.Deserialize<SharpClaw.Contracts.Kernel.PackageManifest>(
            """
            {
              "id": "bad_registration",
              "displayName": "Bad",
              "toolPrefix": "bad",
              "entryAssembly": "nested/bad.dll"
            }
            """)!;

        var act = () => PackageRuntimeInfo.DotNetDefault
            .EnsureDotNetEntryAssembly(manifest);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*entryAssembly*file name*");
    }

    [Test]
    public void ForeignRegistrationHostCapabilityDtos_ComeFromContracts()
    {
        typeof(ForeignConfigurationGetRequest).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(ForeignRegistrationInfoListResponse).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(SharpClaw.Contracts.Kernel.PackageInfo).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");

    }

    [Test]
    public void SidecarReadinessEvaluator_ComeFromCoreAndEvaluatesPreCollectedFacts()
    {
        typeof(RegistrationSidecarReadinessReport).Assembly.GetName().Name
            .Should().Be("SharpClaw.Core");
        typeof(SidecarReadinessEvaluator).Assembly.GetName().Name
            .Should().Be("SharpClaw.Core");

        var facts = new RegistrationSidecarReadinessFacts(
            "sample",
            "Sample",
            "sam",
            "Sample.Module",
            "Sample.Assembly",
            new RegistrationContributionInventory(
                ToolCount: 1,
                InlineToolCount: 0,
                ResourceTypeDescriptorCount: 0,
                GlobalFlagDescriptorCount: 0,
                HeaderTagCount: 0,
                UiContributionCount: 0,
                FrontendContributionCount: 0,
                CliCommandCount: 0,
                ExportedClrContractCount: 0,
                RequiredClrContractCount: 1,
                RequiredNonOptionalClrContractCount: 1,
                RequiredOptionalClrContractCount: 0,
                ExportedProtocolContractCount: 0,
                RequiredProtocolContractCount: 0,
                MapsEndpoints: false,
                OverridesInitialize: false,
                OverridesShutdown: false,
                OverridesSeedData: false,
                OverridesHealthCheck: false,
                OverridesStreamingTools: false,
                OverridesJobCompletionBehavior: false),
            new RegistrationServiceInventory(
                Registrations: [],
                ScopedStorageEntryTypes: [],
                ProviderPluginRegistrations: [],
                EventSinkRegistrations: [],
                FactoryBackedServiceRegistrations: []));

        var report = new SidecarReadinessEvaluator().Evaluate(facts);

        report.Findings.Should()
            .Contain(finding => finding.Kind == SidecarReadinessFindingKind.CoveredByCurrentProtocol
                                && finding.Key == "tools.job")
            .And
            .Contain(finding => finding.Kind == SidecarReadinessFindingKind.RequiresClrContractBridge
                                && finding.Key == "contracts.clr.requirements");
    }
}
