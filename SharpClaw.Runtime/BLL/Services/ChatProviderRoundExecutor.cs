using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Chat;

namespace SharpClaw.Runtime.BLL.Services;

internal sealed class ChatProviderRoundExecutor(
    IProviderApiClient client) : IChatProviderRoundExecutor
{
    public Task<ChatCompletionResult> CompleteAsync(
        ChatProviderCompletionRequest request,
        CancellationToken ct) =>
        client.ChatCompletionAsync(
            request.ModelName,
            request.SystemPrompt,
            request.History,
            request.MaxCompletionTokens,
            request.ProviderParameters,
            request.CompletionParameters,
            ct);

    public Task<ChatCompletionResult> CompleteWithToolsAsync(
        ChatProviderToolCompletionRequest request,
        CancellationToken ct) =>
        client.ChatCompletionWithToolsAsync(
            request.ModelName,
            request.SystemPrompt,
            request.Messages,
            request.Tools,
            request.MaxCompletionTokens,
            request.ProviderParameters,
            request.CompletionParameters,
            ct);

    public IAsyncEnumerable<ChatStreamChunk> StreamWithToolsAsync(
        ChatProviderToolCompletionRequest request,
        CancellationToken ct) =>
        client.StreamChatCompletionWithToolsAsync(
            request.ModelName,
            request.SystemPrompt,
            request.Messages,
            request.Tools,
            request.MaxCompletionTokens,
            request.ProviderParameters,
            request.CompletionParameters,
            ct);
}
