using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.BLL.Kernel;

namespace SharpClaw.Tests.Kernel;

public sealed class DirectChatKernelTests
{
    [Test]
    public async Task Conversation_gate_serializes_reclamation_with_reacquisition()
    {
        var releaseEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var continueRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new ConversationTurnGate(() =>
        {
            releaseEntered.TrySetResult();
            continueRelease.Task.GetAwaiter().GetResult();
        });
        var conversationId = Guid.NewGuid();
        var first = await gate.EnterAsync(conversationId, CancellationToken.None);

        var releaseThread = new Thread(() => first.DisposeAsync().GetAwaiter().GetResult())
        {
            IsBackground = true,
        };
        releaseThread.Start();
        try
        {
            await releaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = Task.Run(async () =>
                await gate.EnterAsync(conversationId, CancellationToken.None));
            second.IsCompleted.Should().BeFalse();

            continueRelease.SetResult();
            releaseThread.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            await using var secondLease = await second;
            gate.ActiveEntryCount.Should().Be(1);
        }
        finally
        {
            continueRelease.TrySetResult();
            releaseThread.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
        }
    }

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

    [Test]
    public async Task Direct_kernel_serializes_load_provider_and_commit_for_one_conversation()
    {
        var conversationId = Guid.NewGuid();
        var provider = new SequencedProviderClient();
        var resolver = new SignalingConversationResolver(conversationId);
        var kernel = DirectChatKernelFactory.CreateFromGraph(
            new KernelGraphBuilder().Compile(),
            new ProviderKernelTransport(provider),
            resolver,
            new FixedChatProfileResolver(new ChatProfile("test", Guid.NewGuid(), "test-model")),
            new InMemoryConversationStore());

        var first = kernel.RunAsync(new ChatTurnInput("first")).AsTask();
        await provider.FirstCallStarted.Task;

        var second = kernel.RunAsync(new ChatTurnInput("second")).AsTask();
        await resolver.SecondResolutionStarted.Task;
        provider.SecondCallStarted.Should().BeFalse();

        provider.ReleaseFirstCall();
        await Task.WhenAll(first, second);

        provider.Requests.Should().HaveCount(2);
        provider.Requests[1].Select(message => $"{message.Role}:{message.Content}")
            .Should()
            .Equal(
                "user:first",
                "assistant:reply-1",
                "user:second");
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

    private sealed class SequencedProviderClient : IProviderApiClient
    {
        private readonly TaskCompletionSource _releaseFirstCall =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callNumber;

        public string ProviderKey => "test";

        public TaskCompletionSource FirstCallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<IReadOnlyList<ChatCompletionMessage>> Requests { get; } = [];

        public bool SecondCallStarted => Volatile.Read(ref _callNumber) > 1;

        public void ReleaseFirstCall() => _releaseFirstCall.TrySetResult();

        public Task<IReadOnlyList<string>> ListModelIdsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(["test-model"]);

        public async Task<ChatCompletionResult> ChatCompletionAsync(
            string model,
            string? systemPrompt,
            IReadOnlyList<ChatCompletionMessage> messages,
            int? maxCompletionTokens = null,
            Dictionary<string, JsonElement>? providerParameters = null,
            CompletionParameters? completionParameters = null,
            CancellationToken ct = default)
        {
            var callNumber = Interlocked.Increment(ref _callNumber);
            Requests.Add(messages.ToArray());
            if (callNumber == 1)
            {
                FirstCallStarted.TrySetResult();
                await _releaseFirstCall.Task.WaitAsync(ct);
            }

            return new ChatCompletionResult
            {
                Content = $"reply-{callNumber}",
                FinishReason = FinishReason.Stop,
                Usage = new TokenUsage(1, 1),
            };
        }
    }

    private sealed class SignalingConversationResolver(Guid conversationId) : IConversationResolver
    {
        private readonly Guid _conversationId = conversationId;
        private int _resolutionCount;

        public TaskCompletionSource SecondResolutionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ConversationSelection> ResolveAsync(
            ChatTurnInput input,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _resolutionCount) == 2)
                SecondResolutionStarted.TrySetResult();

            return ValueTask.FromResult(new ConversationSelection(
                input.ConversationId.GetValueOrDefault(_conversationId),
                input.ConversationId is null));
        }
    }
}
