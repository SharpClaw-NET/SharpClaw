using System.Security.Cryptography;
using System.Text.Json;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;

/// <summary>Runs direct chat through one compiled Core kernel graph.</summary>
public sealed class DirectChatKernel
{
    private readonly KernelGraph _graph;
    private readonly DirectTurnRunner _runner;
    private readonly RunScopedConversationResolver _conversationResolver;

    internal DirectChatKernel(
        KernelGraph graph,
        DirectTurnRunner runner,
        RunScopedConversationResolver conversationResolver)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _conversationResolver = conversationResolver
            ?? throw new ArgumentNullException(nameof(conversationResolver));
    }

    public ValueTask<ChatTurnResult> RunAsync(
        ChatTurnInput input,
        CancellationToken cancellationToken = default) =>
        _graph.RunInServiceScopeAsync(
            async _ =>
            {
                await using var run = _conversationResolver.BeginRun();
                return await _runner.RunAsync(input, cancellationToken);
            });

    public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
        ChatTurnInput input,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in _graph.StreamInServiceScopeAsync(
                           _ => StreamCore(input, cancellationToken),
                           cancellationToken)
                           .WithCancellation(cancellationToken))
            yield return chunk;
    }

    private async IAsyncEnumerable<ChatStreamChunk> StreamCore(
        ChatTurnInput input,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        await using var run = _conversationResolver.BeginRun();
        await foreach (var chunk in _runner.StreamAsync(input, cancellationToken)
                           .WithCancellation(cancellationToken))
            yield return chunk;
    }
}

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

/// <summary>Issues local host authority for one provider-created Tool action.</summary>
internal sealed class RuntimeKernelToolContextIssuer : IKernelToolContextIssuer
{
    public ValueTask<HostActionEntryRequestContext?> IssueAsync(
        KernelToolContextIssueRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.IsWellFormed)
            return ValueTask.FromResult<HostActionEntryRequestContext?>(null);

        var now = DateTimeOffset.UtcNow;
        var parent = request.ParentActionContext;
        var deadline = parent?.Deadline ?? now.AddMinutes(1);
        if (deadline <= now)
            return ValueTask.FromResult<HostActionEntryRequestContext?>(null);

        var payload = JsonSerializer.SerializeToUtf8Bytes(request.Arguments);
        var manifest = KernelActionCatalog.DescriptorFor(SharpClawActions.Tools.Invoke);
        var descriptor = new ActionDescriptor<ToolInvocation, ToolInvocationOutcome>(
            manifest.Key,
            manifest.Version,
            manifest.Category,
            manifest.Capabilities,
            manifest.ContainsSensitiveData,
            manifest.HasIrreversibleEffects,
            manifest.RepeatPolicy,
            manifest.ContinuationPolicy,
            manifest.DefaultTimeout)
        {
            ProtocolVersionRange = ContractVersionRange.Exact(1),
            SafePoints = manifest.SafePoints,
            InputSchema = manifest.InputSchema,
            ResultSchema = manifest.ResultSchema
        };
        var inputSchema = descriptor.InputSchema;
        if (inputSchema is null || string.IsNullOrWhiteSpace(inputSchema.ContentHash))
            return ValueTask.FromResult<HostActionEntryRequestContext?>(null);

        var context = new HostActionEntryRequestContext(
            Guid.NewGuid(),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            HostActionEntryIngress.Tool,
            request.InvocationId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            parent?.Caller ?? RequestPrincipal.Anonymous,
            parent?.Features ?? ExtensionFeatureSet.Empty,
            parent?.TraceId ?? Guid.NewGuid(),
            parent?.IdempotencyKey ?? Guid.NewGuid(),
            deadline,
            deadline)
        {
            Contribution = new HostActionEntryContribution(
                new HostActionEntryIngressBinding(
                    HostActionEntryIngress.Tool,
                    request.ToolName,
                    request.ConversationId?.ToString("D")),
                new HostActionEntryLineage(
                    SharpClawActions.Tools.Invoke,
                    descriptor.Version,
                    HostActionEntryAuthorityValidator.ComputeDescriptorHash(descriptor),
                    typeof(ToolInvocation).AssemblyQualifiedName ?? typeof(ToolInvocation).FullName!,
                    inputSchema.Version,
                    inputSchema.ContentHash,
                    Convert.ToHexString(SHA256.HashData(payload)),
                    payload.Length)),
            ParentInvocationId = parent?.InvocationId,
            Depth = parent is null ? 0 : parent.Depth + 1,
            Attempt = parent?.Attempt > 0 ? parent.Attempt : 1,
        };

        return ValueTask.FromResult<HostActionEntryRequestContext?>(context);
    }
}

/// <summary>
/// Adapts one canonical provider client to the Core kernel transport.
/// Core invokes this adapter only from its published provider terminal actions.
/// </summary>
internal sealed class ProviderKernelTransport : IKernelProviderTransport
{
    private readonly Func<string, IProviderApiClient> _resolveClient;

    public ProviderKernelTransport(IProviderApiClient client)
        : this(_ => client ?? throw new ArgumentNullException(nameof(client)))
    {
    }

    public ProviderKernelTransport(Func<string, IProviderApiClient> resolveClient) =>
        _resolveClient = resolveClient ?? throw new ArgumentNullException(nameof(resolveClient));

    public ValueTask<ChatCompletionResult> CompleteAsync(
        ProviderTurnRequest request,
        IReadOnlyList<ToolAwareMessage> messages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(messages);
        var client = _resolveClient(request.Profile.ProviderKey);
        var (systemPrompt, normalizedMessages) = NormalizeMessages(request, messages);
        var providerMessages = normalizedMessages
            .Select(ToCompletionMessage)
            .ToArray();

        return request.Tools.Count == 0
            ? new ValueTask<ChatCompletionResult>(client.ChatCompletionAsync(
                request.Profile.ModelName ?? request.Profile.ModelId.ToString(),
                systemPrompt,
                providerMessages,
                completionParameters: request.Profile.ProviderParameters,
                ct: cancellationToken))
            : new ValueTask<ChatCompletionResult>(client.ChatCompletionWithToolsAsync(
                request.Profile.ModelName ?? request.Profile.ModelId.ToString(),
                systemPrompt,
                normalizedMessages,
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
        var client = _resolveClient(request.Profile.ProviderKey);
        var (systemPrompt, normalizedMessages) = NormalizeMessages(request, messages);
        var model = request.Profile.ModelName ?? request.Profile.ModelId.ToString();
        if (request.Tools.Count > 0)
        {
            await foreach (var chunk in client.StreamChatCompletionWithToolsAsync(
                               model,
                               systemPrompt,
                               normalizedMessages,
                               request.Tools.Select(tool => new ChatToolDefinition(
                                   tool.Name,
                                   tool.Description,
                                   tool.ParametersSchema)).ToArray(),
                               completionParameters: request.Profile.ProviderParameters,
                               ct: cancellationToken))
                yield return chunk;

            yield break;
        }

        var result = await client.ChatCompletionAsync(
            model,
            systemPrompt,
            normalizedMessages.Select(ToCompletionMessage).ToArray(),
            completionParameters: request.Profile.ProviderParameters,
            ct: cancellationToken);
        if (!string.IsNullOrEmpty(result.Content))
            yield return ChatStreamChunk.Text(result.Content);
        yield return ChatStreamChunk.Final(result);
    }

    private static (string? SystemPrompt, ToolAwareMessage[] Messages) NormalizeMessages(
        ProviderTurnRequest request,
        IReadOnlyList<ToolAwareMessage> messages)
    {
        var systemParts = messages
            .Where(message => string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
            .Select(message => message.Content)
            .Where(content => !string.IsNullOrWhiteSpace(content))
            .ToArray();
        var systemPrompt = systemParts.Length > 0
            ? string.Join("\n\n", systemParts)
            : request.Profile.SystemPrompt;
        var nonSystemMessages = messages
            .Where(message => !string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return (systemPrompt, nonSystemMessages);
    }

    private static ChatCompletionMessage ToCompletionMessage(ToolAwareMessage message) =>
        new(message.Role, message.Content ?? string.Empty)
        {
            ProviderMetadataJson = message.ProviderMetadataJson,
            ImageBase64 = message.ImageBase64,
            ImageMediaType = message.ImageMediaType,
        };
}

/// <summary>Provides one stable conversation when no feature registration is loaded.</summary>
public sealed class SingleConversationResolver(Guid conversationId) : IConversationResolver
{
    private readonly Guid _conversationId = conversationId == Guid.Empty
        ? throw new ArgumentException("The conversation identifier must not be empty.", nameof(conversationId))
        : conversationId;

    public ValueTask<ConversationSelection> ResolveAsync(
        ChatTurnInput input,
        ChatOperationContext context,
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

    public ValueTask<ChatProfile> ResolveAsync(
        ChatTurnContext turn,
        ChatOperationContext context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_profile);
    }
}

/// <summary>Resolves one independent conversation for each stateless turn.</summary>
internal sealed class StatelessConversationResolver : IConversationResolver
{
    public ValueTask<ConversationSelection> ResolveAsync(
        ChatTurnInput input,
        ChatOperationContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ConversationSelection(Guid.NewGuid(), Created: true));
    }
}

/// <summary>Discards conversation state when the Context registration is absent.</summary>
internal sealed class StatelessConversationStore : IConversationStore
{
    public ValueTask<IReadOnlyList<ChatCompletionMessage>> LoadHistoryAsync(
        Guid conversationId,
        ChatOperationContext context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<ChatCompletionMessage>>([]);
    }

    public ValueTask CommitExchangeAsync(
        ChatExchange exchange,
        ChatOperationContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Stores direct-chat exchanges in a bounded process-local history.</summary>
public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, List<ChatCompletionMessage>> _history = [];

    public ValueTask<IReadOnlyList<ChatCompletionMessage>> LoadHistoryAsync(
        Guid conversationId,
        ChatOperationContext context,
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

    public ValueTask CommitExchangeAsync(
        ChatExchange exchange,
        ChatOperationContext context,
        CancellationToken ct)
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
