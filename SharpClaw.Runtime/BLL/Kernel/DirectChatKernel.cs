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
                var run = _conversationResolver.BeginRun();
                await using var runAsyncDisposal = run.ConfigureAwait(false);
                return await _runner.RunAsync(input, cancellationToken).ConfigureAwait(false);
            });

    public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
        ChatTurnInput input,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in _graph.StreamInServiceScopeAsync(
                           _ => StreamCoreAsync(input, cancellationToken),
                           cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return chunk;
    }

    private async IAsyncEnumerable<ChatStreamChunk> StreamCoreAsync(
        ChatTurnInput input,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        var run = _conversationResolver.BeginRun();
        await using var runAsyncDisposal = run.ConfigureAwait(false);
        await foreach (var chunk in _runner.StreamAsync(input, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return chunk;
    }
}
