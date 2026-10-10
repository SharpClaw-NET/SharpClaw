using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;
using SharpClaw.ModuleSDK;

namespace SharpClaw.DefaultPackages.TestHarness;




/// <summary>Executes the deterministic tools through the unified kernel pipeline.</summary>
public sealed class TestHarnessToolHandler(TestHarnessState state) : IToolHandler
{
    private readonly TestHarnessState _state = state ?? throw new ArgumentNullException(nameof(state));

    public async ValueTask<ToolResult> InvokeAsync(
        ToolInvocation invocation,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(invocation);
        if (string.Equals(invocation.ToolName, TestHarnessConstants.ControlTool, StringComparison.Ordinal))
            return ToolResult.Text(ExecuteControl(invocation.Arguments));
        if (string.Equals(invocation.ToolName, TestHarnessConstants.SnapshotTool, StringComparison.Ordinal))
            return ToolResult.Text(JsonSerializer.Serialize(new
            {
                _state.ProviderRequests,
                _state.ProviderTimings,
                _state.ToolCalls,
            }));

        var behavior = string.Equals(invocation.ToolName, TestHarnessConstants.JobStreamingTool, StringComparison.Ordinal) ? _state.StreamingJobToolBehavior
            : string.Equals(invocation.ToolName, TestHarnessConstants.JobPermissionedTool, StringComparison.Ordinal) ? _state.PermissionedJobToolBehavior
                : string.Equals(invocation.ToolName, TestHarnessConstants.JobResourceTool, StringComparison.Ordinal) ? _state.PermissionedJobToolBehavior
                    : string.Equals(invocation.ToolName, TestHarnessConstants.InlineOpenTool, StringComparison.Ordinal) ? _state.OpenInlineToolBehavior
                        : _state.PermissionedInlineToolBehavior;

        behavior = ApplyOverrides(behavior, invocation.Arguments);
        if (behavior.LatencyMs > 0)
            await Task.Delay(behavior.LatencyMs, ct).ConfigureAwait(false);
        if (behavior.ThrowFailure)
            return ToolResult.Error("test harness tool failure");

        var result = behavior.PayloadBytes > 0
            ? TestHarnessState.ExpandPayload(behavior.Result, behavior.PayloadBytes)
            : behavior.Result;
        _state.RecordToolCall(new CapturedToolCall(
            _state.NextSequence(),
            "direct",
            invocation.ToolName,
            invocation.Arguments.GetRawText(),
            Guid.Empty,
            Guid.Empty,
            invocation.ConversationId,
            null,
            behavior.LatencyMs,
            false));
        return ToolResult.Text(result);
    }

    private string ExecuteControl(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("action", out var action) ||
            action.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Test harness control requires an action.");

        switch (action.GetString())
        {
            case "reset":
                _state.Reset();
                return "ok";
            case "resetDiagnostics":
                _state.ResetDiagnostics();
                return "ok";
            case "configureProvider":
                _state.ConfigureProvider(
                    arguments.GetProperty("providerKey").GetString() ?? "",
                    arguments.GetProperty("scenario").Deserialize<TestHarnessProviderScenario>()
                        ?? throw new InvalidOperationException("The provider scenario is required."));
                return "ok";
            default:
                throw new InvalidOperationException($"Unknown test harness control action '{action.GetString()}'.");
        }
    }

    private static TestHarnessToolBehavior ApplyOverrides(
        TestHarnessToolBehavior behavior,
        JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return behavior;
        var next = behavior;
        if (arguments.TryGetProperty("latencyMs", out var latency) && latency.TryGetInt32(out var latencyMs))
            next = next with { LatencyMs = latencyMs };
        if (arguments.TryGetProperty("payloadBytes", out var payload) && payload.TryGetInt32(out var payloadBytes))
            next = next with { PayloadBytes = payloadBytes };
        if (arguments.TryGetProperty("fail", out var fail) && fail.ValueKind is JsonValueKind.True or JsonValueKind.False)
            next = next with { ThrowFailure = fail.GetBoolean() };
        if (arguments.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String)
            next = next with { Result = result.GetString() ?? string.Empty };
        return next;
    }
}
