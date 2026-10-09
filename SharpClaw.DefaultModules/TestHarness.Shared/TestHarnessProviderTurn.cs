using System.Text.Json;

using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.DefaultPackages.TestHarness;


public sealed record TestHarnessProviderTurn
{
    public string? Content { get; init; } = "test harness response";
    public IReadOnlyList<string>? StreamingChunks { get; init; }
    public IReadOnlyList<ChatToolCall> ToolCalls { get; init; } = [];
    public TokenUsage? Usage { get; init; } = new(7, 5);
    public string? ProviderMetadataJson { get; init; } = """{"source":"test-harness"}""";
    public FinishReason FinishReason { get; init; } = FinishReason.Stop;
    public int FirstTokenDelayMs { get; init; }
    public int PerChunkDelayMs { get; init; }
    public int CompletionDelayMs { get; init; }
    public int? LargePayloadBytes { get; init; }
    public bool ThrowBeforeResponse { get; init; }
    public bool ThrowMalformedPayload { get; init; }
    public int StreamFailureAfterChunks { get; init; } = -1;

    public int ConfiguredDelayMs
    {
        get
        {
            var chunks = StreamingChunks?.Count ?? 0;
            return FirstTokenDelayMs + Math.Max(0, chunks - 1) * PerChunkDelayMs + CompletionDelayMs;
        }
    }
}
