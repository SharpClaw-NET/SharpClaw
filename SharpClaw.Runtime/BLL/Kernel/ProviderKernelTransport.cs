using System.Security.Cryptography;
using System.Text.Json;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;


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
        var model = RuntimeChatConfiguration.ResolveModel(request.Profile);
        var client = _resolveClient(request.Profile.ProviderKey);
        var (systemPrompt, normalizedMessages) = NormalizeMessages(request, messages);
        var providerMessages = normalizedMessages
            .Select(ToCompletionMessage)
            .ToArray();

        return request.Tools.Count == 0
            ? new ValueTask<ChatCompletionResult>(client.ChatCompletionAsync(
                model,
                systemPrompt,
                providerMessages,
                completionParameters: request.Profile.ProviderParameters,
                ct: cancellationToken))
            : new ValueTask<ChatCompletionResult>(client.ChatCompletionWithToolsAsync(
                model,
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
        var model = RuntimeChatConfiguration.ResolveModel(request.Profile);
        var client = _resolveClient(request.Profile.ProviderKey);
        var (systemPrompt, normalizedMessages) = NormalizeMessages(request, messages);
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
                               ct: cancellationToken).ConfigureAwait(false))
                yield return chunk;

            yield break;
        }

        var result = await client.ChatCompletionAsync(
            model,
            systemPrompt,
            normalizedMessages.Select(ToCompletionMessage).ToArray(),
            completionParameters: request.Profile.ProviderParameters,
            ct: cancellationToken).ConfigureAwait(false);
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
