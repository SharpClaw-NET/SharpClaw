using System.Security.Cryptography;
using System.Text.Json;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;


/// <summary>Builds one direct-chat runner over an explicit Core kernel graph.</summary>
internal static class DirectChatKernelFactory
{
    internal static DirectChatKernel CreateFromGraph(
        KernelGraph graph,
        KernelActionDispatcher dispatcher,
        IKernelProviderTransport providerTransport,
        IConversationResolver conversationResolver,
        IChatProfileResolver profileResolver,
        IConversationStore conversationStore)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(providerTransport);
        ArgumentNullException.ThrowIfNull(conversationResolver);
        ArgumentNullException.ThrowIfNull(profileResolver);
        ArgumentNullException.ThrowIfNull(conversationStore);

        var gatedConversationResolver = new RunScopedConversationResolver(
            conversationResolver,
            new ConversationTurnGate());
        var contextAssembler = graph.CreateChatContextAssembler(dispatcher);
        var providerLoop = new ProviderRoundLoop(
            providerTransport,
            graph,
            dispatcher,
            new RuntimeKernelToolContextIssuer());
        var toolPipeline = new UnifiedToolPipeline(graph, dispatcher);
        return new DirectChatKernel(
            graph,
            new DirectTurnRunner(
                graph,
                dispatcher,
                gatedConversationResolver,
                profileResolver,
                conversationStore,
                contextAssembler,
                providerLoop,
                toolPipeline),
            gatedConversationResolver);
    }
}
