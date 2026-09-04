using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;
using SharpClaw.ModuleSDK;

namespace SharpClaw.DefaultPackages.TestHarness;

#if TEST_HARNESS_OUT_OF_PROCESS
public sealed class TestHarnessOutOfProcessRegistration()
    : TestHarnessRegistrationBase(TestHarnessConstants.OutOfProcessRegistrationId, "Test Harness Out Of Process");
#endif

#if TEST_HARNESS_IN_PROCESS
public sealed class TestHarnessInProcessRegistration()
    : TestHarnessRegistrationBase(TestHarnessConstants.InProcessRegistrationId, "Test Harness In Process");
#endif

/// <summary>Provides deterministic provider and direct-tool behavior for host tests.</summary>
public abstract class TestHarnessRegistrationBase(string SourceId, string displayName) : ISharpClawModule
{
    public ModuleIdentity Identity { get; } = new(SourceId, displayName, TestHarnessConstants.ToolPrefix);

    public void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<TestHarnessState>();
        AddProvider(services, TestHarnessConstants.PlainProviderKey, "SharpClaw Test Harness", false);
        AddProvider(services, TestHarnessConstants.StreamingProviderKey, "SharpClaw Test Harness Streaming", true);
        AddProvider(services, TestHarnessConstants.ToolProviderKey, "SharpClaw Test Harness Tools", true);
        AddProvider(services, TestHarnessConstants.FailingProviderKey, "SharpClaw Test Harness Failure", true);
        AddProvider(services, TestHarnessConstants.CostProviderKey, "SharpClaw Test Harness Cost", true);
        AddProvider(services, TestHarnessConstants.EdenStyleProviderKey, "SharpClaw Test Harness EdenAI", true);

        foreach (var descriptor in ToolDescriptors())
            services.AddTool<TestHarnessToolHandler>(descriptor);
#if TEST_HARNESS_IN_PROCESS
        services.AddCliCommand<TestHarnessScopedCliHandler>(new CliCommandDescriptor(
            TestHarnessConstants.ScopedCliCommand,
            [],
            "Reports scoped command execution.",
            new JsonSchemaReference("test-harness.scope.input", 1, "test-harness-scope-input"),
            new JsonSchemaReference("test-harness.scope.result", 1, "test-harness-scope-result")));
#endif
    }

    public int PermissionDescriptorBuilds => 0;

    public void ResetDiagnostics()
    {
    }

    private void AddProvider(
        IServiceCollection services,
        string providerKey,
        string displayName,
        bool supportsNativeToolCalling) =>
        services.AddSingleton<IProviderPlugin>(sp => new TestHarnessProviderPlugin(
            OwnerId: Identity.Id,
            providerKey,
            displayName,
            supportsNativeToolCalling,
            sp.GetRequiredService<TestHarnessState>()));

    private static IEnumerable<ToolDescriptor> ToolDescriptors()
    {
        var schema = ToolSchema();
        yield return new(
            TestHarnessConstants.InlineOpenTool,
            "Deterministic direct tool for host pipeline tests.",
            schema);
        yield return new(
            TestHarnessConstants.ControlTool,
            "Configure deterministic test harness behavior.",
            schema);
        yield return new(
            TestHarnessConstants.SnapshotTool,
            "Read deterministic test harness observations.",
            schema);
        yield return new(
            TestHarnessConstants.JobPermissionedTool,
            "Deterministic tool for host pipeline tests.",
            schema);
        yield return new(
            TestHarnessConstants.JobResourceTool,
            "Deterministic resource tool for host pipeline tests.",
            schema);
        yield return new(
            TestHarnessConstants.JobStreamingTool,
            "Deterministic streaming tool for host pipeline tests.",
            schema);
    }

    private static JsonElement ToolSchema()
    {
        using var document = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "latencyMs": { "type": "integer" },
                "payloadBytes": { "type": "integer" },
                "fail": { "type": "boolean" },
                "result": { "type": "string" }
              },
              "additionalProperties": false
            }
            """);
        return document.RootElement.Clone();
    }
}

#if TEST_HARNESS_IN_PROCESS
public sealed class TestHarnessScopedCliHandler : ICliHandler, IDisposable
{
    private static int _created;
    private static int _disposed;
    private static int _active;
    private readonly Guid _instanceId = Guid.NewGuid();
    private int _isDisposed;

    public TestHarnessScopedCliHandler()
    {
        Interlocked.Increment(ref _created);
        Interlocked.Increment(ref _active);
    }

    public ValueTask<CliResult> ExecuteAsync(
        CliInvocation invocation,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new CliResult(
            true,
            [new CliOutput("stdout", JsonSerializer.Serialize(new
            {
                instanceId = _instanceId,
                created = Volatile.Read(ref _created),
                disposed = Volatile.Read(ref _disposed),
                active = Volatile.Read(ref _active),
            }))]));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            return;
        Interlocked.Decrement(ref _active);
        Interlocked.Increment(ref _disposed);
    }
}
#endif

/// <summary>Executes the deterministic tools through the unified kernel pipeline.</summary>
public sealed class TestHarnessToolHandler(TestHarnessState state) : IToolHandler
{
    private readonly TestHarnessState _state = state ?? throw new ArgumentNullException(nameof(state));

    public async ValueTask<ToolResult> InvokeAsync(
        ToolInvocation invocation,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (invocation.ToolName == TestHarnessConstants.ControlTool)
            return ToolResult.Text(ExecuteControl(invocation.Arguments));
        if (invocation.ToolName == TestHarnessConstants.SnapshotTool)
            return ToolResult.Text(JsonSerializer.Serialize(new
            {
                _state.ProviderRequests,
                _state.ProviderTimings,
                _state.ToolCalls,
            }));

        var behavior = invocation.ToolName == TestHarnessConstants.JobStreamingTool
            ? _state.StreamingJobToolBehavior
            : invocation.ToolName == TestHarnessConstants.JobPermissionedTool
                ? _state.PermissionedJobToolBehavior
                : invocation.ToolName == TestHarnessConstants.JobResourceTool
                    ? _state.PermissionedJobToolBehavior
                    : invocation.ToolName == TestHarnessConstants.InlineOpenTool
                        ? _state.OpenInlineToolBehavior
                        : _state.PermissionedInlineToolBehavior;

        behavior = ApplyOverrides(behavior, invocation.Arguments);
        if (behavior.LatencyMs > 0)
            await Task.Delay(behavior.LatencyMs, ct);
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
