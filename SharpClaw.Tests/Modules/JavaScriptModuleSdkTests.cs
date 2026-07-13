using FluentAssertions;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class JavaScriptModuleSdkTests
{
    [Test]
    public async Task JavaScriptSdk_ExposesCurrentForeignModuleProtocolShape()
    {
        var source = await File.ReadAllTextAsync(ModuleSdkSourcePaths.JavaScriptHostSourcePath);

        source.Should().Contain("X-SharpClaw-Control-Token");
        source.Should().Contain("SHARPCLAW_MODULE_DIR");
        source.Should().Contain("SHARPCLAW_MODULE_DATA_DIR");
        source.Should().Contain("SHARPCLAW_CONTROL_ADDRESS");
        source.Should().Contain("SHARPCLAW_CONTROL_TOKEN");
        source.Should().Contain("SHARPCLAW_MODULE_ID");
        source.Should().Contain("SHARPCLAW_MODULE_RUNTIME");
        source.Should().Contain("SHARPCLAW_HOST_CAPABILITIES_ADDRESS");
        source.Should().Contain("SHARPCLAW_HOST_CAPABILITIES_TOKEN");
        source.Should().Contain("/.sharpclaw/handshake");
        source.Should().Contain("/.sharpclaw/discovery");
        source.Should().Contain("/.sharpclaw/health");
        source.Should().Contain("/.sharpclaw/initialize");
        source.Should().Contain("/.sharpclaw/shutdown");
        source.Should().Contain("/.sharpclaw/tools/execute");
        source.Should().Contain("/.sharpclaw/tools/stream");
        source.Should().Contain("/.sharpclaw/inline-tools/execute");
        source.Should().Contain("/.sharpclaw/contracts/invoke");
        source.Should().Contain("/.sharpclaw/host/config/get");
        source.Should().Contain("/.sharpclaw/host/job/log");
        source.Should().Contain("/.sharpclaw/host/contracts/invoke");
        source.Should().Contain("/.sharpclaw/host/conversation/steer");
        source.Should().Contain("/.sharpclaw/host/conversation/steering/list");
        source.Should().Contain("/.sharpclaw/host/modules/storage/list");
        source.Should().Contain("/.sharpclaw/host/modules/storage/invoke");
        source.Should().Contain("createHostCapabilitiesClient");
        source.Should().Contain("createDocumentStore");
        source.Should().Contain("addConversationSteering");
        source.Should().Contain("listConversationSteering");
        source.Should().Contain("inlineTools");
        source.Should().Contain("protocolContracts");
        source.Should().Contain("storageContracts");
        source.Should().Contain("invokeStorage");
        source.Should().Contain("batchUpsert");
        source.Should().Contain("lessThanOrEqual");
        source.Should().Contain("supportsStreaming");
    }

}
