using SharpClaw.Contracts.Modules;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.Host;

namespace SharpClaw.Tests.Kernel;

public sealed class RuntimeChatScenarioManifestTests
{
    [Test]
    public void Manifest_uses_only_published_chat_and_conversation_actions()
    {
        RuntimeChatScenarioManifest.RequiredActions.Should().NotBeEmpty();
        RuntimeChatScenarioManifest.RequiredActions.Should().OnlyContain(key =>
            key.Value.StartsWith("chat.", StringComparison.Ordinal)
            || key.Value.StartsWith("conversation.", StringComparison.Ordinal));
        RuntimeChatScenarioManifest.RequiredActions.Should().BeEquivalentTo(
            SharpClawActionCatalog.Kernel.Where(key =>
                key.Value.StartsWith("chat.", StringComparison.Ordinal)
                || key.Value.StartsWith("conversation.", StringComparison.Ordinal)));

        var graph = new SharpClaw.Core.Kernel.KernelGraphBuilder().Compile();
        RuntimeChatScenarioManifest.RequiredActions.Should().AllSatisfy(key =>
            graph.ContainsAction(key).Should().BeTrue());
    }

    [Test]
    public void Manifest_declares_the_complete_direct_chat_scenarios()
    {
        RuntimeChatScenarioManifest.RequiredScenarios.Should().BeEquivalentTo(
        [
            "buffered-turn",
            "streaming-turn",
            "conversation-history",
            "stream-cancellation",
            "stream-failure",
        ]);
    }

    [Test]
    public void Runtime_output_does_not_expose_the_removed_chat_service_or_handlers()
    {
        var runtimeBllTypes = typeof(DirectChatKernel).Assembly
            .GetTypes()
            .Select(type => type.Name)
            .ToArray();
        runtimeBllTypes.Should().NotContain("ChatService");
        runtimeBllTypes.Should().NotContain("ChatProviderRoundExecutor");

        var runtimeHostTypes = typeof(KernelHostEndpoints).Assembly
            .GetTypes()
            .Select(type => type.Name)
            .ToArray();
        runtimeHostTypes.Should().NotContain("ChatHandlers");
        runtimeHostTypes.Should().NotContain("ChatStreamHandlers");
        runtimeHostTypes.Should().NotContain("ThreadChatHandlers");
        runtimeHostTypes.Should().NotContain("ThreadChatStreamHandlers");
    }
}
