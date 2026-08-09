using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;

/// <summary>Runs direct chat through one compiled Core kernel graph.</summary>
public sealed class DirectChatKernel
{
    private readonly DirectTurnRunner _runner;
    private readonly RunScopedConversationResolver _conversationResolver;

    internal DirectChatKernel(
        DirectTurnRunner runner,
        RunScopedConversationResolver conversationResolver)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _conversationResolver = conversationResolver
            ?? throw new ArgumentNullException(nameof(conversationResolver));
    }

    public async ValueTask<ChatTurnResult> RunAsync(
        ChatTurnInput input,
        CancellationToken cancellationToken = default)
    {
        await using var run = _conversationResolver.BeginRun();
        return await _runner.RunAsync(input, cancellationToken);
    }
}

/// <summary>Builds one direct-chat runner over an explicit Core kernel graph.</summary>
internal static class DirectChatKernelFactory
{
    internal static DirectChatKernel CreateFromGraph(
        KernelGraph graph,
        IKernelProviderTransport providerTransport,
        IConversationResolver conversationResolver,
        IChatProfileResolver profileResolver,
        IConversationStore conversationStore,
        RequestPrincipal? caller = null,
        ExtensionFeatureSet? features = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(providerTransport);
        ArgumentNullException.ThrowIfNull(conversationResolver);
        ArgumentNullException.ThrowIfNull(profileResolver);
        ArgumentNullException.ThrowIfNull(conversationStore);

        var gatedConversationResolver = new RunScopedConversationResolver(
            conversationResolver,
            new ConversationTurnGate());
        var dispatcher = new KernelActionDispatcher(
            graph,
            new KernelActionExecutionContext(
                caller ?? RequestPrincipal.Anonymous,
                features ?? ExtensionFeatureSet.Empty,
                Guid.NewGuid(),
                Guid.NewGuid()));
        var contextAssembler = graph.CreateChatContextAssembler(dispatcher);
        var providerLoop = new ProviderRoundLoop(providerTransport, graph, dispatcher);
        var toolPipeline = new UnifiedToolPipeline(graph, dispatcher);
        return new DirectChatKernel(
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

/// <summary>Adapts one canonical provider client to the Core kernel transport.</summary>
public sealed class ProviderKernelTransport(IProviderApiClient client) : IKernelProviderTransport
{
    private readonly IProviderApiClient _client =
        client ?? throw new ArgumentNullException(nameof(client));

    public ValueTask<ChatCompletionResult> CompleteAsync(
        ProviderTurnRequest request,
        IReadOnlyList<ToolAwareMessage> messages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(messages);
        var providerMessages = messages
            .Select(ToCompletionMessage)
            .ToArray();

        return request.Tools.Count == 0
            ? new ValueTask<ChatCompletionResult>(_client.ChatCompletionAsync(
                request.Profile.ModelName ?? request.Profile.ModelId.ToString(),
                request.Profile.SystemPrompt,
                providerMessages,
                completionParameters: request.Profile.ProviderParameters,
                ct: cancellationToken))
            : new ValueTask<ChatCompletionResult>(_client.ChatCompletionWithToolsAsync(
                request.Profile.ModelName ?? request.Profile.ModelId.ToString(),
                request.Profile.SystemPrompt,
                messages,
                request.Tools.Select(tool => new ChatToolDefinition(
                    tool.Name,
                    tool.Description,
                    tool.ParametersSchema)).ToArray(),
                completionParameters: request.Profile.ProviderParameters,
                ct: cancellationToken));
    }

    public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
        ProviderTurnRequest request,
        IReadOnlyList<ToolAwareMessage> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(messages);
        var model = request.Profile.ModelName ?? request.Profile.ModelId.ToString();
        if (request.Tools.Count > 0)
        {
            await foreach (var chunk in _client.StreamChatCompletionWithToolsAsync(
                               model,
                               request.Profile.SystemPrompt,
                               messages,
                               request.Tools.Select(tool => new ChatToolDefinition(
                                   tool.Name,
                                   tool.Description,
                                   tool.ParametersSchema)).ToArray(),
                               completionParameters: request.Profile.ProviderParameters,
                               ct: cancellationToken))
                yield return chunk;

            yield break;
        }

        var result = await _client.ChatCompletionAsync(
            model,
            request.Profile.SystemPrompt,
            messages.Select(ToCompletionMessage).ToArray(),
            completionParameters: request.Profile.ProviderParameters,
            ct: cancellationToken);
        if (!string.IsNullOrEmpty(result.Content))
            yield return ChatStreamChunk.Text(result.Content);
        yield return ChatStreamChunk.Final(result);
    }

    private static ChatCompletionMessage ToCompletionMessage(ToolAwareMessage message) =>
        new(message.Role, message.Content ?? string.Empty)
        {
            ProviderMetadataJson = message.ProviderMetadataJson,
            ImageBase64 = message.ImageBase64,
            ImageMediaType = message.ImageMediaType,
        };
}

/// <summary>Provides one stable conversation when no feature module is loaded.</summary>
public sealed class SingleConversationResolver(Guid conversationId) : IConversationResolver
{
    private readonly Guid _conversationId = conversationId == Guid.Empty
        ? throw new ArgumentException("The conversation identifier must not be empty.", nameof(conversationId))
        : conversationId;

    public ValueTask<ConversationSelection> ResolveAsync(
        ChatTurnInput input,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ConversationSelection(
            input.ConversationId.GetValueOrDefault(_conversationId),
            input.ConversationId is null));
    }
}

/// <summary>Resolves one configured direct-chat profile.</summary>
public sealed class FixedChatProfileResolver(ChatProfile profile) : IChatProfileResolver
{
    private readonly ChatProfile _profile = profile ?? throw new ArgumentNullException(nameof(profile));

    public ValueTask<ChatProfile> ResolveAsync(ChatTurnContext turn, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_profile);
    }
}

/// <summary>Stores direct-chat exchanges in a bounded process-local history.</summary>
public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, List<ChatCompletionMessage>> _history = [];

    public ValueTask<IReadOnlyList<ChatCompletionMessage>> LoadHistoryAsync(
        Guid conversationId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return ValueTask.FromResult<IReadOnlyList<ChatCompletionMessage>>(
                _history.TryGetValue(conversationId, out var messages)
                    ? messages.ToArray()
                    : []);
        }
    }

    public ValueTask CommitExchangeAsync(ChatExchange exchange, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_history.TryGetValue(exchange.Turn.Conversation.ConversationId, out var messages))
            {
                messages = [];
                _history.Add(exchange.Turn.Conversation.ConversationId, messages);
            }

            messages.Add(new ChatCompletionMessage("user", exchange.UserMessage));
            if (!string.IsNullOrEmpty(exchange.Completion.Content))
                messages.Add(new ChatCompletionMessage("assistant", exchange.Completion.Content));
        }

        return ValueTask.CompletedTask;
    }
}
