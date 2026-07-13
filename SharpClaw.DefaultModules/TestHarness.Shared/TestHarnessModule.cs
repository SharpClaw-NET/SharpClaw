using System.Diagnostics;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.DefaultModules.TestHarness;

#if TEST_HARNESS_OUT_OF_PROCESS
public sealed class TestHarnessOutOfProcessModule()
    : TestHarnessModuleBase(TestHarnessConstants.OutOfProcessModuleId, "Test Harness Out Of Process")
{
    public override IAsyncEnumerable<string>? ExecuteToolStreamingAsync(
        string toolName,
        JsonElement parameters,
        AgentJobContext job,
        IServiceProvider scopedServices,
        CancellationToken ct) =>
        base.ExecuteToolStreamingAsync(toolName, parameters, job, scopedServices, ct);

    public override ModuleJobCompletionBehavior GetJobCompletionBehavior(
        string toolName,
        JsonElement parameters,
        AgentJobContext job) =>
        base.GetJobCompletionBehavior(toolName, parameters, job);
}
#endif

#if TEST_HARNESS_IN_PROCESS
public sealed class TestHarnessInProcessModule()
    : TestHarnessModuleBase(TestHarnessConstants.InProcessModuleId, "Test Harness In Process")
{
    public override IAsyncEnumerable<string>? ExecuteToolStreamingAsync(
        string toolName,
        JsonElement parameters,
        AgentJobContext job,
        IServiceProvider scopedServices,
        CancellationToken ct) =>
        base.ExecuteToolStreamingAsync(toolName, parameters, job, scopedServices, ct);

    public override ModuleJobCompletionBehavior GetJobCompletionBehavior(
        string toolName,
        JsonElement parameters,
        AgentJobContext job) =>
        base.GetJobCompletionBehavior(toolName, parameters, job);
}
#endif

public abstract class TestHarnessModuleBase(string moduleId, string displayName) : ISharpClawRuntimeModule
{
    private static readonly JsonSerializerOptions TestHarnessJsonOptions = new(JsonSerializerDefaults.Web);
    private int _permissionDescriptorBuilds;

    public string Id => moduleId;
    public string DisplayName => displayName;
    public string ToolPrefix => TestHarnessConstants.ToolPrefix;
    public int PermissionDescriptorBuilds => Volatile.Read(ref _permissionDescriptorBuilds);

    public void ResetDiagnostics() =>
        Interlocked.Exchange(ref _permissionDescriptorBuilds, 0);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<TestHarnessState>();
        services.AddSingleton<IProviderPlugin>(sp => new TestHarnessProviderPlugin(
            moduleId,
            TestHarnessConstants.PlainProviderKey,
            "SharpClaw Test Harness",
            supportsNativeToolCalling: false,
            sp.GetRequiredService<TestHarnessState>()));
        services.AddSingleton<IProviderPlugin>(sp => new TestHarnessProviderPlugin(
            moduleId,
            TestHarnessConstants.StreamingProviderKey,
            "SharpClaw Test Harness Streaming",
            supportsNativeToolCalling: true,
            sp.GetRequiredService<TestHarnessState>()));
        services.AddSingleton<IProviderPlugin>(sp => new TestHarnessProviderPlugin(
            moduleId,
            TestHarnessConstants.ToolProviderKey,
            "SharpClaw Test Harness Tools",
            supportsNativeToolCalling: true,
            sp.GetRequiredService<TestHarnessState>()));
        services.AddSingleton<IProviderPlugin>(sp => new TestHarnessProviderPlugin(
            moduleId,
            TestHarnessConstants.FailingProviderKey,
            "SharpClaw Test Harness Failure",
            supportsNativeToolCalling: true,
            sp.GetRequiredService<TestHarnessState>()));
        services.AddSingleton<IProviderPlugin>(sp => new TestHarnessProviderPlugin(
            moduleId,
            TestHarnessConstants.CostProviderKey,
            "SharpClaw Test Harness Cost",
            supportsNativeToolCalling: true,
            sp.GetRequiredService<TestHarnessState>()));
        services.AddSingleton<IProviderPlugin>(sp => new TestHarnessProviderPlugin(
            moduleId,
            TestHarnessConstants.EdenStyleProviderKey,
            "SharpClaw Test Harness EdenAI",
            supportsNativeToolCalling: true,
            sp.GetRequiredService<TestHarnessState>()));
    }

    public IReadOnlyList<ModuleGlobalFlagDescriptor> GetGlobalFlagDescriptors() =>
    [
        new(
            TestHarnessConstants.GlobalFlagKey,
            "Use Test Harness Tools",
            "Execute deterministic test harness tools.",
            TestHarnessConstants.DelegateName)
    ];

    public IReadOnlyList<ModuleHeaderTag>? GetHeaderTags() =>
    [
        new ModuleHeaderTag(
            TestHarnessConstants.HeaderTagName,
            async (sp, ct) =>
            {
                var state = sp.GetRequiredService<TestHarnessState>();
                var behavior = state.HeaderTagBehavior;
                if (behavior.LatencyMs > 0)
                    await Task.Delay(behavior.LatencyMs, ct);
                if (behavior.ThrowFailure)
                    throw new InvalidOperationException("test harness header tag failure");
                return behavior.Value;
            })
        {
            ResolveWithContext = ResolveHeaderTagWithContextAsync
        }
    ];

    public IReadOnlyList<ModuleInlineToolDefinition> GetInlineToolDefinitions()
    {
        var permission = PermissionDescriptor();
        return
        [
            new(
                TestHarnessConstants.InlineOpenTool,
                "Deterministic open inline tool for host pipeline tests.",
                EmptyObjectSchema()),

            new(
                TestHarnessConstants.ControlTool,
                "Configure deterministic test harness behavior through the module boundary.",
                EmptyObjectSchema()),

            new(
                TestHarnessConstants.SnapshotTool,
                "Read deterministic test harness observations through the module boundary.",
                EmptyObjectSchema()),

            new(
                TestHarnessConstants.InlinePermissionedTool,
                "Deterministic permissioned inline tool for host enforcement tests.",
                ToolBehaviorSchema(),
                permission,
                Aliases: [TestHarnessConstants.InlinePermissionedToolAlias])
        ];
    }

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions()
    {
        var permission = PermissionDescriptor();
        var resourcePermission = ResourcePermissionDescriptor();
        return
        [
            new(
                TestHarnessConstants.JobPermissionedTool,
                "Deterministic permissioned job tool for host enforcement tests.",
                ToolBehaviorSchema(),
                permission,
                Aliases: [TestHarnessConstants.JobPermissionedToolAlias]),

            new(
                TestHarnessConstants.JobResourceTool,
                "Deterministic per-resource job tool for default-resource resolution tests.",
                ToolBehaviorSchema(),
                resourcePermission),

            new(
                TestHarnessConstants.JobStreamingTool,
                "Deterministic streaming job tool for SSE forwarding tests.",
                ToolBehaviorSchema(),
                permission)
        ];
    }

    public IReadOnlyList<ModuleResourceTypeDescriptor> GetResourceTypeDescriptors() =>
    [
        new(
            TestHarnessConstants.ResourceType,
            TestHarnessConstants.ResourceGrantLabel,
            TestHarnessConstants.ResourceDelegateName,
            LoadHarnessResourceIdsAsync,
            LoadLookupItems: null,
            DefaultResourceKey: TestHarnessConstants.DefaultResourceKey)
    ];

    public IReadOnlyList<ModuleCliCommand> GetCliCommands() =>
    [
        new(
            Name: "testharness",
            Aliases: ["tharness"],
            Scope: ModuleCliScope.TopLevel,
            Description: "Configure deterministic Test Harness scenarios",
            UsageLines:
            [
                "testharness reset",
                "testharness provider-tool-then-final <providerKey> <toolName> <argumentsJson> <finalContent>",
            ],
            Handler: HandleCliCommandAsync)
    ];

    private static Task HandleCliCommandAsync(
        string[] args, IServiceProvider sp, CancellationToken ct)
    {
        _ = ct;
        var state = sp.GetRequiredService<TestHarnessState>();

        if (args.Length < 2)
        {
            PrintCliUsage();
            return Task.CompletedTask;
        }

        switch (args[1].ToLowerInvariant())
        {
            case "reset":
                state.Reset();
                Console.WriteLine("Test Harness state reset.");
                break;

            case "provider-tool-then-final" when args.Length >= 6:
                ConfigureProviderToolThenFinal(
                    state,
                    args[2],
                    args[3],
                    args[4],
                    string.Join(' ', args[5..]));
                break;

            case "provider-tool-then-final":
                Console.Error.WriteLine(
                    "Usage: testharness provider-tool-then-final <providerKey> <toolName> <argumentsJson> <finalContent>");
                break;

            default:
                PrintCliUsage();
                break;
        }

        return Task.CompletedTask;
    }

    private static void ConfigureProviderToolThenFinal(
        TestHarnessState state,
        string providerKey,
        string toolName,
        string argumentsJson,
        string finalContent)
    {
        try
        {
            using var _ = JsonDocument.Parse(argumentsJson);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"Invalid tool arguments JSON: {ex.Message}");
            return;
        }

        state.ConfigureProvider(
            providerKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        ToolCalls = [new ChatToolCall("call-1", toolName, argumentsJson)],
                        Usage = new TokenUsage(3, 2)
                    },
                    new TestHarnessProviderTurn
                    {
                        Content = finalContent,
                        Usage = new TokenUsage(4, 3)
                    }
                ]
            });

        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                ProviderKey = providerKey,
                ToolName = toolName,
                FinalContent = finalContent
            },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void PrintCliUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  testharness reset");
        Console.Error.WriteLine("  testharness provider-tool-then-final <providerKey> <toolName> <argumentsJson> <finalContent>");
    }

    public async Task<string> ExecuteInlineToolAsync(
        string toolName,
        JsonElement parameters,
        InlineToolContext context,
        IServiceProvider scopedServices,
        CancellationToken ct)
    {
        var state = scopedServices.GetRequiredService<TestHarnessState>();
        if (toolName == TestHarnessConstants.ControlTool)
            return ExecuteControl(state, parameters);

        if (toolName == TestHarnessConstants.SnapshotTool)
            return ExecuteSnapshot(state);

        var behavior = toolName switch
        {
            TestHarnessConstants.InlineOpenTool => state.OpenInlineToolBehavior,
            TestHarnessConstants.InlinePermissionedTool => state.PermissionedInlineToolBehavior,
            _ => throw new InvalidOperationException($"Unknown test harness inline tool: '{toolName}'.")
        };

        return await ExecuteToolBehaviorAsync(
            state,
            "inline",
            toolName,
            behavior,
            parameters,
            context.AgentId,
            context.ChannelId,
            context.ThreadId,
            jobId: null,
            ct);
    }

    private string ExecuteControl(TestHarnessState state, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("action", out var actionElement)
            || actionElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Test harness control requires an action.");
        }

        var action = actionElement.GetString();
        switch (action)
        {
            case "reset":
                state.Reset();
                return "ok";

            case "resetDiagnostics":
                ResetDiagnostics();
                return "ok";

            case "configureProvider":
                state.ConfigureProvider(
                    ReadRequiredString(parameters, "providerKey"),
                    DeserializeRequired<TestHarnessProviderScenario>(parameters, "scenario"));
                return "ok";

            case "configureTool":
                ConfigureTool(
                    state,
                    ReadRequiredString(parameters, "target"),
                    DeserializeRequired<TestHarnessToolBehavior>(parameters, "behavior"));
                return "ok";

            case "configureHeaderTag":
                state.ConfigureHeaderTag(DeserializeRequired<TestHarnessHeaderTagBehavior>(parameters, "behavior"));
                return "ok";

            case "configureCost":
                state.ConfigureCost(DeserializeRequired<TestHarnessCostBehavior>(parameters, "behavior"));
                return "ok";

            default:
                throw new InvalidOperationException($"Unknown test harness control action: '{action}'.");
        }
    }

    private string ExecuteSnapshot(TestHarnessState state) =>
        JsonSerializer.Serialize(
            new
            {
                state.ProviderRequests,
                state.ProviderTimings,
                state.ToolCalls,
                state.HeaderTagCalls,
                state.CostCalls,
                PermissionDescriptorBuilds,
            },
            TestHarnessJsonOptions);

    private static void ConfigureTool(
        TestHarnessState state,
        string target,
        TestHarnessToolBehavior behavior)
    {
        switch (target)
        {
            case "openInline":
                state.ConfigureOpenInlineTool(behavior);
                break;

            case "permissionedInline":
                state.ConfigurePermissionedInlineTool(behavior);
                break;

            case "permissionedJob":
                state.ConfigurePermissionedJobTool(behavior);
                break;

            case "streamingJob":
                state.ConfigureStreamingJobTool(behavior);
                break;

            default:
                throw new InvalidOperationException($"Unknown test harness tool target: '{target}'.");
        }
    }

    private static string ReadRequiredString(JsonElement parameters, string propertyName)
    {
        if (!parameters.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"Test harness control requires string property '{propertyName}'.");

        return value.GetString() ?? "";
    }

    private static T DeserializeRequired<T>(JsonElement parameters, string propertyName)
    {
        if (!parameters.TryGetProperty(propertyName, out var value))
            throw new InvalidOperationException($"Test harness control requires property '{propertyName}'.");

        return value.Deserialize<T>(TestHarnessJsonOptions)
            ?? throw new InvalidOperationException($"Test harness control property '{propertyName}' was null.");
    }

    public async Task<string> ExecuteToolAsync(
        string toolName,
        JsonElement parameters,
        AgentJobContext job,
        IServiceProvider scopedServices,
        CancellationToken ct)
    {
        var state = scopedServices.GetRequiredService<TestHarnessState>();
        var behavior = toolName switch
        {
            TestHarnessConstants.JobPermissionedTool => state.PermissionedJobToolBehavior,
            TestHarnessConstants.JobResourceTool => state.PermissionedJobToolBehavior,
            TestHarnessConstants.JobStreamingTool => state.StreamingJobToolBehavior,
            _ => throw new InvalidOperationException($"Unknown test harness job tool: '{toolName}'.")
        };

        return await ExecuteToolBehaviorAsync(
            state,
            "job",
            toolName,
            behavior,
            parameters,
            job.AgentId,
            job.ChannelId,
            threadId: null,
            job.JobId,
            ct);
    }

    public virtual IAsyncEnumerable<string>? ExecuteToolStreamingAsync(
        string toolName,
        JsonElement parameters,
        AgentJobContext job,
        IServiceProvider scopedServices,
        CancellationToken ct)
    {
        if (toolName != TestHarnessConstants.JobStreamingTool)
            return null;

        return StreamToolAsync(toolName, parameters, job, scopedServices, ct);
    }

    public virtual ModuleJobCompletionBehavior GetJobCompletionBehavior(
        string toolName,
        JsonElement parameters,
        AgentJobContext job)
    {
        if (parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("remainExecuting", out var remain)
            && remain.ValueKind is JsonValueKind.True or JsonValueKind.False
            && remain.GetBoolean())
        {
            return ModuleJobCompletionBehavior.RemainExecuting;
        }

        return ModuleJobCompletionBehavior.CompleteWhenExecutionReturns;
    }

    private static async IAsyncEnumerable<string> StreamToolAsync(
        string toolName,
        JsonElement parameters,
        AgentJobContext job,
        IServiceProvider scopedServices,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var state = scopedServices.GetRequiredService<TestHarnessState>();
        var behavior = ApplyParameterOverrides(state.StreamingJobToolBehavior, parameters);
        var parts = SplitToolResult(BuildToolResult(behavior), 3);
        var startedAt = Stopwatch.GetTimestamp();
        var sw = Stopwatch.StartNew();
        var failed = false;

        try
        {
            foreach (var part in parts)
            {
                if (behavior.LatencyMs > 0)
                    await Task.Delay(behavior.LatencyMs / parts.Count, ct);
                if (behavior.ThrowFailure)
                {
                    failed = true;
                    throw new InvalidOperationException("test harness streaming job tool failure");
                }
                yield return part;
            }
        }
        finally
        {
            sw.Stop();
            var completedAt = Stopwatch.GetTimestamp();
            state.RecordToolCall(new CapturedToolCall(
                state.NextSequence(),
                "job-streaming",
                toolName,
                parameters.GetRawText(),
                job.AgentId,
                job.ChannelId,
                null,
                job.JobId,
                sw.ElapsedMilliseconds,
                failed)
            {
                StartedAtTimestamp = startedAt,
                CompletedAtTimestamp = completedAt
            });
        }
    }

    private static async Task<string> ResolveHeaderTagWithContextAsync(
        IServiceProvider sp,
        ModuleHeaderTagContext context,
        CancellationToken ct)
    {
        var state = sp.GetRequiredService<TestHarnessState>();
        var behavior = state.HeaderTagBehavior;
        var sequence = state.NextSequence();
        var sw = Stopwatch.StartNew();
        var failed = false;

        try
        {
            if (behavior.LatencyMs > 0)
                await Task.Delay(behavior.LatencyMs, ct);
            if (behavior.ThrowFailure)
                throw new InvalidOperationException("test harness header tag failure");
            return behavior.Value;
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            sw.Stop();
            state.RecordHeaderTagCall(new CapturedHeaderTagCall(
                sequence,
                context,
                sw.ElapsedMilliseconds,
                failed));
        }
    }

    private static async Task<string> ExecuteToolBehaviorAsync(
        TestHarnessState state,
        string kind,
        string toolName,
        TestHarnessToolBehavior behavior,
        JsonElement parameters,
        Guid agentId,
        Guid channelId,
        Guid? threadId,
        Guid? jobId,
        CancellationToken ct)
    {
        behavior = ApplyParameterOverrides(behavior, parameters);
        var sequence = state.NextSequence();
        var startedAt = Stopwatch.GetTimestamp();
        var sw = Stopwatch.StartNew();
        var failed = false;
        try
        {
            if (behavior.LatencyMs > 0)
                await Task.Delay(behavior.LatencyMs, ct);

            if (behavior.ThrowFailure)
                throw new InvalidOperationException("test harness tool failure");

            return BuildToolResult(behavior);
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
            state.RecordToolCall(new CapturedToolCall(
                sequence,
                kind,
                toolName,
                parameters.GetRawText(),
                agentId,
                channelId,
                threadId,
                jobId,
                sw.ElapsedMilliseconds,
                failed)
            {
                StartedAtTimestamp = startedAt,
                CompletedAtTimestamp = completedAt
            });
        }
    }

    private static TestHarnessToolBehavior ApplyParameterOverrides(
        TestHarnessToolBehavior behavior,
        JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
            return behavior;

        var next = behavior;
        if (parameters.TryGetProperty("latencyMs", out var latency) && latency.TryGetInt32(out var latencyMs))
            next = next with { LatencyMs = latencyMs };
        if (parameters.TryGetProperty("payloadBytes", out var payload) && payload.TryGetInt32(out var payloadBytes))
            next = next with { PayloadBytes = payloadBytes };
        if (parameters.TryGetProperty("fail", out var fail) && fail.ValueKind is JsonValueKind.True or JsonValueKind.False)
            next = next with { ThrowFailure = fail.GetBoolean() };
        if (parameters.TryGetProperty("malformed", out var malformed)
            && malformed.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            next = next with { MalformedOutput = malformed.GetBoolean() };
        }
        if (parameters.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String)
            next = next with { Result = result.GetString() ?? "" };

        return next;
    }

    private static string BuildToolResult(TestHarnessToolBehavior behavior)
    {
        var result = behavior.MalformedOutput
            ? "{{malformed-tool-output"
            : behavior.Result;

        return behavior.PayloadBytes > 0
            ? TestHarnessState.ExpandPayload(result, behavior.PayloadBytes)
            : result;
    }

    private static IReadOnlyList<string> SplitToolResult(string result, int parts)
    {
        if (result.Length == 0)
            return [""];

        parts = Math.Clamp(parts, 1, result.Length);
        var size = (int)Math.Ceiling(result.Length / (double)parts);
        var chunks = new List<string>(parts);
        for (var i = 0; i < result.Length; i += size)
            chunks.Add(result.Substring(i, Math.Min(size, result.Length - i)));
        return chunks;
    }

    private ModuleToolPermission PermissionDescriptor()
    {
        Interlocked.Increment(ref _permissionDescriptorBuilds);
        return new(
            IsPerResource: false,
            Check: null,
            DelegateTo: TestHarnessConstants.DelegateName);
    }

    private ModuleToolPermission ResourcePermissionDescriptor()
    {
        Interlocked.Increment(ref _permissionDescriptorBuilds);
        return new(
            IsPerResource: true,
            Check: null,
            DelegateTo: TestHarnessConstants.ResourceDelegateName);
    }

    private static Task<List<Guid>> LoadHarnessResourceIdsAsync(
        IServiceProvider _,
        CancellationToken __) =>
        Task.FromResult(new List<Guid>());

    private static JsonElement EmptyObjectSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {},
              "additionalProperties": false
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement ToolBehaviorSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "latencyMs": { "type": "integer" },
                "payloadBytes": { "type": "integer" },
                "fail": { "type": "boolean" },
                "malformed": { "type": "boolean" },
                "remainExecuting": { "type": "boolean" },
                "result": { "type": "string" }
              },
              "additionalProperties": false
            }
            """);
        return doc.RootElement.Clone();
    }
}
