using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Tests.TestHarness;

internal static class TestHarnessConstants
{
    public const string ModuleId = OutOfProcessModuleId;
    public const string OutOfProcessModuleId = "sharpclaw_test_harness_out_of_process";
    public const string InProcessModuleId = "sharpclaw_test_harness_in_process";
    public const string ToolPrefix = "th";

    public const string PlainProviderKey = "sharpclaw-test";
    public const string StreamingProviderKey = "sharpclaw-test-stream";
    public const string ToolProviderKey = "sharpclaw-test-tools";
    public const string FailingProviderKey = "sharpclaw-test-failing";
    public const string CostProviderKey = "sharpclaw-test-cost";
    public const string EdenStyleProviderKey = "sharpclaw-test-edenai";

    public const string ModelId = "test-harness-model";
    public const string GlobalFlagKey = "CanUseTestHarnessTools";
    public const string DelegateName = "UseTestHarnessToolAsync";
    public const string ResourceType = "SharpClaw.TestHarness.Resource";
    public const string ResourceGrantLabel = "TestHarnessResource";
    public const string ResourceDelegateName = "UseTestHarnessResourceAsync";
    public const string DefaultResourceKey = "test_harness_resource";

    public const string HeaderTagName = "testharness";
    public const string InlineOpenTool = "test_harness_inline_open";
    public const string InlinePermissionedTool = "test_harness_inline_permissioned";
    public const string InlinePermissionedToolAlias = "test_harness_inline_permissioned_alias";
    public const string ControlTool = "test_harness_control";
    public const string SnapshotTool = "test_harness_snapshot";
    public const string JobPermissionedTool = "test_harness_job_permissioned";
    public const string JobPermissionedToolAlias = "test_harness_job_permissioned_alias";
    public const string JobResourceTool = "test_harness_job_resource";
    public const string JobStreamingTool = "test_harness_job_streaming";
}

internal sealed record TestHarnessProviderScenario
{
    public IReadOnlyList<string> ModelIds { get; init; } = [TestHarnessConstants.ModelId];
    public IReadOnlyList<TestHarnessProviderTurn> Turns { get; init; } = [new()];
    public int FailuresBeforeSuccess { get; init; }
    public string FailureMessage { get; init; } = "test harness configured provider failure";
    public ProviderCostResult? CostResult { get; init; } = new(
        0.25m,
        "usd",
        [new ProviderCostDailyBucket(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1), 0.25m)]);
}

internal sealed record TestHarnessProviderTurn
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

internal sealed record TestHarnessToolBehavior
{
    public string Result { get; init; } = "test harness tool result";
    public int LatencyMs { get; init; }
    public int PayloadBytes { get; init; }
    public bool ThrowFailure { get; init; }
    public bool MalformedOutput { get; init; }
}

internal sealed record TestHarnessHeaderTagBehavior
{
    public string Value { get; init; } = "test harness header tag";
    public int LatencyMs { get; init; }
    public bool ThrowFailure { get; init; }
}

internal sealed record TestHarnessCostBehavior
{
    public ProviderCostResult? Result { get; init; } = new(
        0.25m,
        "usd",
        [new ProviderCostDailyBucket(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1), 0.25m)]);
    public int LatencyMs { get; init; }
    public bool PermissionDenied { get; init; }
}

internal sealed record CapturedProviderRequest(
    int Sequence,
    string ProviderKey,
    string Surface,
    string Model,
    string? SystemPrompt,
    IReadOnlyList<CapturedProviderMessage> Messages,
    IReadOnlyList<CapturedProviderTool> Tools,
    IReadOnlyDictionary<string, string> ProviderParameters,
    CompletionParameters? CompletionParameters,
    bool ApiKeyWasProvided,
    string ApiKeyFingerprint,
    DateTimeOffset CapturedAt);

internal sealed record CapturedProviderMessage(
    string Role,
    string? Content,
    string? ProviderMetadataJson,
    bool HasImage);

internal sealed record CapturedProviderTool(
    string Name,
    string Description,
    string ParametersSchemaJson);

internal sealed record CapturedProviderTiming(
    int Sequence,
    string ProviderKey,
    string Surface,
    int ConfiguredDelayMs,
    long ElapsedMs,
    bool Failed);

internal sealed record CapturedToolCall(
    int Sequence,
    string Kind,
    string ToolName,
    string ParametersJson,
    Guid AgentId,
    Guid ChannelId,
    Guid? ThreadId,
    Guid? JobId,
    long ElapsedMs,
    bool Failed)
{
    public long StartedAtTimestamp { get; init; }
    public long CompletedAtTimestamp { get; init; }
}

internal sealed record CapturedHeaderTagCall(
    int Sequence,
    ModuleHeaderTagContext Context,
    long ElapsedMs,
    bool Failed);

internal sealed record CapturedCostCall(
    int Sequence,
    string ProviderKey,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime,
    long ElapsedMs,
    bool PermissionDenied);

internal sealed record TestHarnessSnapshot(
    IReadOnlyList<CapturedProviderRequest> ProviderRequests,
    IReadOnlyList<CapturedProviderTiming> ProviderTimings,
    IReadOnlyList<CapturedToolCall> ToolCalls,
    IReadOnlyList<CapturedHeaderTagCall> HeaderTagCalls,
    IReadOnlyList<CapturedCostCall> CostCalls,
    int PermissionDescriptorBuilds);

internal sealed class TestHarnessProviderException(string message) : Exception(message);

internal sealed class TestHarnessControl(ChatHarnessHost host)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<CapturedProviderRequest> ProviderRequests => Snapshot().ProviderRequests;
    public IReadOnlyList<CapturedProviderTiming> ProviderTimings => Snapshot().ProviderTimings;
    public IReadOnlyList<CapturedToolCall> ToolCalls => Snapshot().ToolCalls;
    public IReadOnlyList<CapturedHeaderTagCall> HeaderTagCalls => Snapshot().HeaderTagCalls;
    public IReadOnlyList<CapturedCostCall> CostCalls => Snapshot().CostCalls;
    public int PermissionDescriptorBuilds => Snapshot().PermissionDescriptorBuilds;

    public void Reset() => Control(new { action = "reset" });

    public void ResetDiagnostics() => Control(new { action = "resetDiagnostics" });

    public void ConfigureProvider(string providerKey, TestHarnessProviderScenario scenario) =>
        Control(new { action = "configureProvider", providerKey, scenario });

    public void ConfigureOpenInlineTool(TestHarnessToolBehavior behavior) =>
        ConfigureTool("openInline", behavior);

    public void ConfigurePermissionedInlineTool(TestHarnessToolBehavior behavior) =>
        ConfigureTool("permissionedInline", behavior);

    public void ConfigurePermissionedJobTool(TestHarnessToolBehavior behavior) =>
        ConfigureTool("permissionedJob", behavior);

    public void ConfigureStreamingJobTool(TestHarnessToolBehavior behavior) =>
        ConfigureTool("streamingJob", behavior);

    public void ConfigureHeaderTag(TestHarnessHeaderTagBehavior behavior) =>
        Control(new { action = "configureHeaderTag", behavior });

    public void ConfigureCost(TestHarnessCostBehavior behavior) =>
        Control(new { action = "configureCost", behavior });

    private void ConfigureTool(string target, TestHarnessToolBehavior behavior) =>
        Control(new { action = "configureTool", target, behavior });

    private void Control(object payload) =>
        host.ExecuteHarnessInlineTool(TestHarnessConstants.ControlTool, payload);

    private TestHarnessSnapshot Snapshot()
    {
        var json = host.ExecuteHarnessInlineTool(TestHarnessConstants.SnapshotTool, new { });
        return JsonSerializer.Deserialize<TestHarnessSnapshot>(json, JsonOptions)
            ?? throw new InvalidOperationException("Test harness snapshot was empty.");
    }
}
