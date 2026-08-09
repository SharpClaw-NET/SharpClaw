using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.BLL.Kernel;

namespace SharpClaw.Tests.Kernel;

public sealed class DirectChatKernelTests
{
    [Test]
    public async Task Direct_kernel_runs_one_canonical_provider_turn_and_persists_history()
    {
        var provider = new RecordingProviderClient();
        var conversationId = Guid.NewGuid();
        var kernel = DirectChatKernelFactory.CreateFromGraph(
            new KernelGraphBuilder().Compile(),
            new ProviderKernelTransport(provider),
            new SingleConversationResolver(conversationId),
            new FixedChatProfileResolver(new ChatProfile("test", Guid.NewGuid(), "test-model")),
            new InMemoryConversationStore());

        var result = await kernel.RunAsync(new ChatTurnInput("hello"));

        result.ConversationId.Should().Be(conversationId);
        result.Completion.Content.Should().Be("reply");
        provider.Messages.Should().ContainSingle(message => message.Content == "hello");
    }

    [Test]
    public async Task Direct_kernel_honors_explicit_conversation_and_cancellation()
    {
        var provider = new RecordingProviderClient();
        var store = new InMemoryConversationStore();
        var kernel = DirectChatKernelFactory.CreateFromGraph(
            new KernelGraphBuilder().Compile(),
            new ProviderKernelTransport(provider),
            new SingleConversationResolver(Guid.NewGuid()),
            new FixedChatProfileResolver(new ChatProfile("test", Guid.NewGuid(), "test-model")),
            store);
        var explicitConversation = Guid.NewGuid();

        var result = await kernel.RunAsync(new ChatTurnInput(
            "hello",
            explicitConversation));

        result.ConversationId.Should().Be(explicitConversation);
        var history = await store.LoadHistoryAsync(explicitConversation, CancellationToken.None);
        history.Should().HaveCount(2);
        history[0].Role.Should().Be("user");
        history[1].Role.Should().Be("assistant");
    }

    private sealed class RecordingProviderClient : IProviderApiClient
    {
        public string ProviderKey => "test";
        public List<ChatCompletionMessage> Messages { get; } = [];

        public Task<IReadOnlyList<string>> ListModelIdsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(["test-model"]);

        public Task<ChatCompletionResult> ChatCompletionAsync(
            string model,
            string? systemPrompt,
            IReadOnlyList<ChatCompletionMessage> messages,
            int? maxCompletionTokens = null,
            Dictionary<string, JsonElement>? providerParameters = null,
            CompletionParameters? completionParameters = null,
            CancellationToken ct = default)
        {
            Messages.AddRange(messages);
            return Task.FromResult(new ChatCompletionResult
            {
                Content = "reply",
                FinishReason = FinishReason.Stop,
                Usage = new TokenUsage(1, 1),
            });
        }
    }
}
