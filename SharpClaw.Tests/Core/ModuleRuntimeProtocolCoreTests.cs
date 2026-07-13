using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Modules.Sidecar;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ModuleRuntimeProtocolCoreTests
{
    [Test]
    public void ForeignModuleProtocolSurface_ComesFromContractsAssembly()
    {
        typeof(ForeignModuleProtocol).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(ForeignModuleHostCapabilityProtocol).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(ForeignModuleEndpointResponseMode).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(ForeignModuleCapability).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");

        ForeignModuleProtocol.Version.Should().Be(1);
        ForeignModuleProtocol.HandshakePath.Should().Be("/.sharpclaw/handshake");
        ForeignModuleHostCapabilityProtocol.TaskLaunchPath
            .Should().Be("/.sharpclaw/host/tasks/launch");
        ForeignModuleEndpointResponseMode.WebSocket.Should().Be("websocket");
        ForeignModuleCapability.ProviderPlugins.Should().Be("providerPlugins");
    }

    [Test]
    public void ForeignModuleSidecarProtocolModels_ComeFromContractsAndUseCoreMappers()
    {
        typeof(ForeignModuleHandshakeRequest).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(ForeignModuleDiscoveryResponse).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(ForeignModuleToolDescriptor).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(ForeignModuleProtocolContractInvocationRequest).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(ForeignModuleTaskTriggerAttributeHandleRequest).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(ForeignModuleProviderChatCompletionRequest).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(ForeignModuleProviderPluginDescriptor).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(ForeignModuleProtocolModelMapper).Assembly.GetName().Name
            .Should().Be("SharpClaw.Core");

        typeof(SharpClaw.Contracts.Modules.ModuleManifest).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(SharpClaw.Contracts.Tasks.TaskTriggerDefinition).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(SharpClaw.Contracts.Providers.ChatCompletionMessage).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(SharpClaw.Contracts.Providers.ProviderCostSeed).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");

        var schema = JsonSerializer.SerializeToElement(new { type = "object" });
        var descriptor = new ForeignModuleToolDescriptor(
            "sample",
            "Sample tool",
            schema,
            Permission: new ForeignModulePermissionDescriptor(IsPerResource: false));

        descriptor.ToModuleToolDefinition().Name.Should().Be("sample");

        var health = new ForeignModuleHealthResponse(true, Details: new Dictionary<string, JsonElement>
        {
            ["queueDepth"] = JsonSerializer.SerializeToElement(3),
        });

        health.ToModuleHealthStatus().Details.Should().ContainKey("queueDepth");
    }

    [Test]
    public void ModuleManifestRuntimeInfo_ParsesAndNormalizesInContracts()
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
            .Should().Be("SharpClaw.Contracts");
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

    [Test]
    public void ForeignModuleHostCapabilityDtos_ComeFromContracts()
    {
        typeof(ForeignModuleConfigGetRequest).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(ForeignModuleTaskLaunchRequest).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(ForeignModuleTaskStatementInvocationDescriptor).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(ForeignModuleTaskOperationExecutionResponse).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(ForeignModuleInfoListResponse).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        typeof(SharpClaw.Contracts.Modules.ModuleInfo).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");

        var descriptor = new ForeignModuleTaskStatementInvocationDescriptor(
            "module.step",
            Arguments: ["one"],
            Body:
            [
                new ForeignModuleTaskStatementInvocationDescriptor("module.child")
            ]);

        descriptor.StatementKey.Should().Be("module.step");
        descriptor.Body.Should().ContainSingle()
            .Which.StatementKey.Should().Be("module.child");
    }

    [Test]
    public void SidecarReadinessEvaluator_ComeFromCoreAndEvaluatesPreCollectedFacts()
    {
        typeof(ModuleSidecarReadinessReport).Assembly.GetName().Name
            .Should().Be("SharpClaw.Core");
        typeof(SidecarReadinessEvaluator).Assembly.GetName().Name
            .Should().Be("SharpClaw.Core");

        var facts = new ModuleSidecarReadinessFacts(
            "sample",
            "Sample",
            "sam",
            "Sample.Module",
            "Sample.Assembly",
            new ModuleContributionInventory(
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
                OverridesJobCompletionBehavior: false,
                IsTaskParserAware: false),
            new ModuleServiceInventory(
                Registrations: [],
                ModuleStorageRegistrationTypes: [],
                ProviderPluginRegistrations: [],
                TaskRuntimeServiceRegistrations: [],
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
