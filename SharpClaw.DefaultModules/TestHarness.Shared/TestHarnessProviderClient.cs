using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;

namespace SharpClaw.DefaultPackages.TestHarness;

internal sealed class TestHarnessProviderClient(
    string providerKey,
    bool supportsNativeToolCalling,
    TestHarnessState state,
    string apiKey) : IProviderApiClient
{
    public string ProviderKey => providerKey;
    public bool SupportsNativeToolCalling => supportsNativeToolCalling;

    public Task<IReadOnlyList<string>> ListModelIdsAsync(CancellationToken ct = default)
    {
        var scenario = state.GetScenario(providerKey);
        return Task.FromResult(scenario.ModelIds);
    }

    public async Task<ChatCompletionResult> ChatCompletionAsync(
        string model,
        string? systemPrompt,
        IReadOnlyList<ChatCompletionMessage> messages,
        int? maxCompletionTokens = null,
        Dictionary<string, JsonElement>? providerParameters = null,
        CompletionParameters? completionParameters = null,
        CancellationToken ct = default)
    {
        var sequence = state.CaptureProviderRequest(
            providerKey,
            "chat",
            apiKey,
            model,
            systemPrompt,
            toolAwareMessages: null,
            simpleMessages: messages,
            tools: null,
            providerParameters,
            completionParameters);

        return await RunTurnAsync(sequence, "chat", ct).ConfigureAwait(false);
    }

    public async Task<ChatCompletionResult> ChatCompletionWithToolsAsync(
        string model,
        string? systemPrompt,
        IReadOnlyList<ToolAwareMessage> messages,
        IReadOnlyList<ChatToolDefinition> tools,
        int? maxCompletionTokens = null,
        Dictionary<string, JsonElement>? providerParameters = null,
        CompletionParameters? completionParameters = null,
        CancellationToken ct = default)
    {
        var sequence = state.CaptureProviderRequest(
            providerKey,
            "chat-tools",
            apiKey,
            model,
            systemPrompt,
            messages,
            simpleMessages: null,
            tools,
            providerParameters,
            completionParameters);

        return await RunTurnAsync(sequence, "chat-tools", ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatStreamChunk> StreamChatCompletionWithToolsAsync(
        string model,
        string? systemPrompt,
        IReadOnlyList<ToolAwareMessage> messages,
        IReadOnlyList<ChatToolDefinition> tools,
        int? maxCompletionTokens = null,
        Dictionary<string, JsonElement>? providerParameters = null,
        CompletionParameters? completionParameters = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var sequence = state.CaptureProviderRequest(
            providerKey,
            "stream-tools",
            apiKey,
            model,
            systemPrompt,
            messages,
            simpleMessages: null,
            tools,
            providerParameters,
            completionParameters);

        var scenario = state.GetScenario(providerKey);
        var (_, turn, failBeforeSuccess) = state.NextTurn(providerKey);
        var startedAt = Stopwatch.GetTimestamp();
        var sw = Stopwatch.StartNew();
        var failed = false;
        try
        {
            if (failBeforeSuccess)
            {
                failed = true;
                throw new TestHarnessProviderException(scenario.FailureMessage);
            }
            if (turn.ThrowBeforeResponse)
            {
                failed = true;
                throw new TestHarnessProviderException("test harness configured stream failure");
            }
            if (turn.ThrowMalformedPayload)
            {
                failed = true;
                throw new InvalidDataException("test harness malformed provider payload");
            }

            var chunks = turn.StreamingChunks ?? SplitForStreaming(BuildContent(turn), 3);

            if (chunks.Count > 0 && turn.FirstTokenDelayMs > 0)
                await Task.Delay(turn.FirstTokenDelayMs, ct).ConfigureAwait(false);

            for (var i = 0; i < chunks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (i > 0 && turn.PerChunkDelayMs > 0)
                    await Task.Delay(turn.PerChunkDelayMs, ct).ConfigureAwait(false);

                yield return ChatStreamChunk.Text(chunks[i]);

                if (turn.StreamFailureAfterChunks == i + 1)
                {
                    failed = true;
                    throw new TestHarnessProviderException("test harness configured mid-stream failure");
                }
            }

            if (turn.CompletionDelayMs > 0)
                await Task.Delay(turn.CompletionDelayMs, ct).ConfigureAwait(false);

            yield return ChatStreamChunk.Final(BuildResult(turn));
        }
        finally
        {
            sw.Stop();
            var completedAt = Stopwatch.GetTimestamp();
            state.RecordProviderTiming(new CapturedProviderTiming(
                sequence,
                providerKey,
                "stream-tools",
                turn.ConfiguredDelayMs,
                sw.ElapsedMilliseconds,
                failed)
            {
                StartedAtTimestamp = startedAt,
                CompletedAtTimestamp = completedAt
            });
        }
    }

    private async Task<ChatCompletionResult> RunTurnAsync(
        int sequence,
        string surface,
        CancellationToken ct)
    {
        var scenario = state.GetScenario(providerKey);
        var (_, turn, failBeforeSuccess) = state.NextTurn(providerKey);
        var startedAt = Stopwatch.GetTimestamp();
        var sw = Stopwatch.StartNew();
        var failed = false;
        try
        {
            if (failBeforeSuccess)
                throw new TestHarnessProviderException(scenario.FailureMessage);
            if (turn.ThrowBeforeResponse)
                throw new TestHarnessProviderException("test harness configured provider failure");
            if (turn.ThrowMalformedPayload)
                throw new InvalidDataException("test harness malformed provider payload");

            var delay = turn.FirstTokenDelayMs + turn.CompletionDelayMs;
            if (delay > 0)
                await Task.Delay(delay, ct).ConfigureAwait(false);

            return BuildResult(turn);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            sw.Stop();
            var completedAt = Stopwatch.GetTimestamp();
            state.RecordProviderTiming(new CapturedProviderTiming(
                sequence,
                providerKey,
                surface,
                turn.FirstTokenDelayMs + turn.CompletionDelayMs,
                sw.ElapsedMilliseconds,
                failed)
            {
                StartedAtTimestamp = startedAt,
                CompletedAtTimestamp = completedAt
            });
        }
    }

    private static ChatCompletionResult BuildResult(TestHarnessProviderTurn turn) => new()
    {
        Content = BuildContent(turn),
        ToolCalls = turn.ToolCalls,
        Usage = turn.Usage,
        FinishReason = turn.ToolCalls.Count > 0 ? FinishReason.ToolCalls : turn.FinishReason,
        ProviderMetadataJson = turn.ProviderMetadataJson
    };

    private static string BuildContent(TestHarnessProviderTurn turn)
    {
        var content = turn.Content
            ?? (turn.StreamingChunks is not null ? string.Concat(turn.StreamingChunks) : "");
        return turn.LargePayloadBytes is { } bytes
            ? TestHarnessState.ExpandPayload(content, bytes)
            : content;
    }

    private static IReadOnlyList<string> SplitForStreaming(string text, int parts)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        parts = Math.Clamp(parts, 1, text.Length);
        var chunkSize = (int)Math.Ceiling(text.Length / (double)parts);
        var chunks = new List<string>(parts);
        for (var i = 0; i < text.Length; i += chunkSize)
            chunks.Add(text.Substring(i, Math.Min(chunkSize, text.Length - i)));
        return chunks;
    }
}
