using FluentAssertions;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class PythonModuleSdkTests
{
    [Test]
    public async Task PythonSdk_ExposesCurrentForeignModuleProtocolShape()
    {
        var source = await File.ReadAllTextAsync(ModuleSdkSourcePaths.PythonHostSourcePath);

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
        source.Should().Contain("HostCapabilitiesClient");
        source.Should().Contain("ModuleDocumentStore");
        source.Should().Contain("create_document_store");
        source.Should().Contain("add_conversation_steering");
        source.Should().Contain("list_conversation_steering");
        source.Should().Contain("InlineToolExecutionContext");
        source.Should().Contain("ProtocolContractContext");
        source.Should().Contain("storageContracts");
        source.Should().Contain("invoke_storage");
        source.Should().Contain("batchUpsert");
        source.Should().Contain("lessThanOrEqual");
        source.Should().Contain("supportsStreaming");
        source.Should().Contain("asgi_app");
    }

}
