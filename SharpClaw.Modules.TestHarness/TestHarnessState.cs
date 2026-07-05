using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Modules.TestHarness;

public sealed partial class TestHarnessState
{
    private readonly ConcurrentDictionary<string, TestHarnessProviderScenario> _providerScenarios =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _providerCallCounts =
        new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<CapturedProviderRequest> _providerRequests = new();
    private readonly ConcurrentQueue<CapturedProviderTiming> _providerTimings = new();
    private readonly ConcurrentQueue<CapturedToolCall> _toolCalls = new();
    private readonly ConcurrentQueue<CapturedHeaderTagCall> _headerTagCalls = new();
    private readonly ConcurrentQueue<CapturedCostCall> _costCalls = new();
    private int _sequence;

    public TestHarnessState()
    {
        Reset();
    }

    public IReadOnlyList<CapturedProviderRequest> ProviderRequests => [.. _providerRequests];
    public IReadOnlyList<CapturedProviderTiming> ProviderTimings => [.. _providerTimings];
    public IReadOnlyList<CapturedToolCall> ToolCalls => [.. _toolCalls];
    public IReadOnlyList<CapturedHeaderTagCall> HeaderTagCalls => [.. _headerTagCalls];
    public IReadOnlyList<CapturedCostCall> CostCalls => [.. _costCalls];

    public TestHarnessToolBehavior OpenInlineToolBehavior { get; private set; } = new();
    public TestHarnessToolBehavior PermissionedInlineToolBehavior { get; private set; } = new();
    public TestHarnessToolBehavior PermissionedJobToolBehavior { get; private set; } = new();
    public TestHarnessToolBehavior StreamingJobToolBehavior { get; private set; } = new();
    public TestHarnessHeaderTagBehavior HeaderTagBehavior { get; private set; } = new();
    public TestHarnessCostBehavior CostBehavior { get; private set; } = new();

    public void Reset()
    {
        _providerScenarios.Clear();
        _providerCallCounts.Clear();
        Drain(_providerRequests);
        Drain(_providerTimings);
        Drain(_toolCalls);
        Drain(_headerTagCalls);
        Drain(_costCalls);
        _sequence = 0;

        ConfigureProvider(TestHarnessConstants.PlainProviderKey, new TestHarnessProviderScenario());
        ConfigureProvider(TestHarnessConstants.StreamingProviderKey, new TestHarnessProviderScenario
        {
            Turns =
            [
                new TestHarnessProviderTurn
                {
                    Content = "test harness stream",
                    StreamingChunks = ["test ", "harness ", "stream"],
                    Usage = new TokenUsage(7, 6)
                }
            ]
        });
        ConfigureProvider(TestHarnessConstants.ToolProviderKey, new TestHarnessProviderScenario());
        ConfigureProvider(TestHarnessConstants.FailingProviderKey, new TestHarnessProviderScenario
        {
            Turns = [new TestHarnessProviderTurn { ThrowBeforeResponse = true }]
        });
        ConfigureProvider(TestHarnessConstants.CostProviderKey, new TestHarnessProviderScenario());
        ConfigureProvider(TestHarnessConstants.EdenStyleProviderKey, new TestHarnessProviderScenario
        {
            ModelIds =
            [
                TestHarnessConstants.ModelId,
                "edenai/harness-chat"
            ]
        });

        OpenInlineToolBehavior = new TestHarnessToolBehavior();
        PermissionedInlineToolBehavior = new TestHarnessToolBehavior();
        PermissionedJobToolBehavior = new TestHarnessToolBehavior();
        StreamingJobToolBehavior = new TestHarnessToolBehavior { Result = "test harness streaming tool result" };
        HeaderTagBehavior = new TestHarnessHeaderTagBehavior();
        CostBehavior = new TestHarnessCostBehavior();
    }

    public void ConfigureProvider(string providerKey, TestHarnessProviderScenario scenario)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentNullException.ThrowIfNull(scenario);
        _providerScenarios[providerKey] = scenario;
        _providerCallCounts.TryRemove(providerKey, out _);
    }

    public void ConfigureOpenInlineTool(TestHarnessToolBehavior behavior) =>
        OpenInlineToolBehavior = behavior;

    public void ConfigurePermissionedInlineTool(TestHarnessToolBehavior behavior) =>
        PermissionedInlineToolBehavior = behavior;

    public void ConfigurePermissionedJobTool(TestHarnessToolBehavior behavior) =>
        PermissionedJobToolBehavior = behavior;

    public void ConfigureStreamingJobTool(TestHarnessToolBehavior behavior) =>
        StreamingJobToolBehavior = behavior;

    public void ConfigureHeaderTag(TestHarnessHeaderTagBehavior behavior) =>
        HeaderTagBehavior = behavior;

    public void ConfigureCost(TestHarnessCostBehavior behavior) =>
        CostBehavior = behavior;

    public TestHarnessProviderScenario GetScenario(string providerKey) =>
        _providerScenarios.TryGetValue(providerKey, out var scenario)
            ? scenario
            : new TestHarnessProviderScenario();

    public (int CallNumber, TestHarnessProviderTurn Turn, bool ShouldFailBeforeSuccess) NextTurn(
        string providerKey)
    {
        var scenario = GetScenario(providerKey);
        var callNumber = _providerCallCounts.AddOrUpdate(providerKey, 1, (_, current) => current + 1);
        var shouldFail = callNumber <= scenario.FailuresBeforeSuccess;
        var turnIndex = Math.Max(0, callNumber - scenario.FailuresBeforeSuccess - 1);
        var turns = scenario.Turns.Count == 0 ? [new TestHarnessProviderTurn()] : scenario.Turns;
        var turn = turns[Math.Min(turnIndex, turns.Count - 1)];
        return (callNumber, turn, shouldFail);
    }

    public int CaptureProviderRequest(
        string providerKey,
        string surface,
        string apiKey,
        string model,
        string? systemPrompt,
        IEnumerable<ToolAwareMessage>? toolAwareMessages,
        IEnumerable<ChatCompletionMessage>? simpleMessages,
        IEnumerable<ChatToolDefinition>? tools,
        Dictionary<string, JsonElement>? providerParameters,
        CompletionParameters? completionParameters)
    {
        var sequence = NextSequence();
        var messages = toolAwareMessages is not null
            ? toolAwareMessages.Select(m => new CapturedProviderMessage(
                m.Role,
                SanitizeText(m.Content),
                SanitizeText(m.ProviderMetadataJson),
                m.HasImage)).ToList()
            : simpleMessages!.Select(m => new CapturedProviderMessage(
                m.Role,
                SanitizeText(m.Content),
                SanitizeText(m.ProviderMetadataJson),
                m.HasImage)).ToList();

        var capturedTools = tools?.Select(t => new CapturedProviderTool(
            SanitizeText(t.Name) ?? "",
            SanitizeText(t.Description) ?? "",
            SanitizeText(t.ParametersSchema.GetRawText()) ?? "{}")).ToList()
            ?? [];

        var parameters = providerParameters?.ToDictionary(
                p => SanitizeText(p.Key) ?? "",
                p => IsSensitiveKey(p.Key) ? "[redacted]" : SanitizeText(p.Value.GetRawText()) ?? "",
                StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);

        _providerRequests.Enqueue(new CapturedProviderRequest(
            sequence,
            providerKey,
            surface,
            SanitizeText(model) ?? "",
            SanitizeText(systemPrompt),
            messages,
            capturedTools,
            parameters,
            completionParameters,
            !string.IsNullOrEmpty(apiKey),
            Fingerprint(apiKey),
            DateTimeOffset.UtcNow));

        return sequence;
    }

    public void RecordProviderTiming(CapturedProviderTiming timing) =>
        _providerTimings.Enqueue(timing);

    public int NextSequence() => Interlocked.Increment(ref _sequence);

    public void RecordToolCall(CapturedToolCall call) =>
        _toolCalls.Enqueue(call);

    public void RecordHeaderTagCall(CapturedHeaderTagCall call) =>
        _headerTagCalls.Enqueue(call);

    public void RecordCostCall(CapturedCostCall call) =>
        _costCalls.Enqueue(call);

    public static string ExpandPayload(string seed, int payloadBytes)
    {
        if (payloadBytes <= 0)
            return seed;

        var builder = new StringBuilder(payloadBytes + seed.Length);
        builder.Append(seed);
        while (Encoding.UTF8.GetByteCount(builder.ToString()) < payloadBytes)
            builder.Append('x');

        var text = builder.ToString();
        return Encoding.UTF8.GetByteCount(text) <= payloadBytes
            ? text
            : text[..payloadBytes];
    }

    public static string? SanitizeText(string? value)
    {
        if (value is null)
            return null;

        var sanitized = SecretPattern().Replace(value, "$1=[redacted]");
        sanitized = ApiKeyPattern().Replace(sanitized, "[redacted-api-key]");
        sanitized = BearerPattern().Replace(sanitized, "Bearer [redacted]");

        const int maxChars = 64_000;
        return sanitized.Length <= maxChars
            ? sanitized
            : sanitized[..maxChars] + "...[truncated]";
    }

    private static bool IsSensitiveKey(string key) =>
        key.Contains("key", StringComparison.OrdinalIgnoreCase)
        || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || key.Contains("token", StringComparison.OrdinalIgnoreCase)
        || key.Contains("password", StringComparison.OrdinalIgnoreCase);

    private static string Fingerprint(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash[..8]).ToLowerInvariant();
    }

    private static void Drain<T>(ConcurrentQueue<T> queue)
    {
        while (queue.TryDequeue(out _)) { }
    }

    [GeneratedRegex(@"(?i)\b(api[_-]?key|secret|token|password)\s*[:=]\s*['""]?[^'""\s\]]+")]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"\b(sk|pk|rk)-[A-Za-z0-9_\-]{12,}\b")]
    private static partial Regex ApiKeyPattern();

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9_\-\.=]{12,}")]
    private static partial Regex BearerPattern();
}
