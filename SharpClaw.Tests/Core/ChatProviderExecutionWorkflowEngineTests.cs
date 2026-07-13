using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Chat;
using SharpClaw.Core.Modules;
using SharpClaw.Core.Tasks.Runtime;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ChatProviderExecutionWorkflowEngineTests
{
    [Test]
    public async Task RunBufferedAsync_WhenToolsAreDisabled_CallsPlainCompletion()
    {
        var provider = new RecordingProviderRoundExecutor
        {
            PlainResult = new ChatCompletionResult
            {
                Content = null,
                Usage = new TokenUsage(3, 5),
                ProviderMetadataJson = """{"id":"plain"}"""
            }
        };
        var engine = CreateEngine();
        var host = new RecordingNativeToolLoopHost();

        var result = await engine.RunBufferedAsync(
            CreateRequest(
                provider,
                host,
                enableTools: false));

        result.AssistantContent.Should().Be("");
        result.JobResults.Should().BeEmpty();
        result.TotalPromptTokens.Should().Be(3);
        result.TotalCompletionTokens.Should().Be(5);
        result.ProviderMetadataJson.Should().Be("""{"id":"plain"}""");
        provider.PlainCalls.Should().Be(1);
        provider.NativeCalls.Should().Be(0);
        host.TaskToolCalls.Should().Be(0);
        host.NativeJobToolCalls.Should().Be(0);
    }

    [Test]
    public async Task RunBufferedAsync_WhenToolsAreEnabled_UsesNativeLoopWithEffectiveTools()
    {
        var registry = CreateRegistry();
        var provider = new RecordingProviderRoundExecutor
        {
            NativeResult = new ChatCompletionResult
            {
                Content = "native answer",
                Usage = new TokenUsage(7, 11),
                ProviderMetadataJson = """{"id":"native"}"""
            }
        };
        var engine = CreateEngine(registry);
        var host = new RecordingNativeToolLoopHost();
        var instanceId = Guid.NewGuid();
        var store = TaskSharedData.GetOrCreate(instanceId);
        store.RegisterBuiltInTools();

        try
        {
            var result = await engine.RunBufferedAsync(
                CreateRequest(
                    provider,
                    host,
                    enableTools: true,
                    taskContext: new TaskChatContext(instanceId, "Task")));

            result.AssistantContent.Should().Be("native answer");
            result.JobResults.Should().BeEmpty();
            result.TotalPromptTokens.Should().Be(7);
            result.TotalCompletionTokens.Should().Be(11);
            result.ProviderMetadataJson.Should().Be("""{"id":"native"}""");
            provider.PlainCalls.Should().Be(0);
            provider.NativeCalls.Should().Be(1);
            provider.LastNativeToolNames.Should().Contain(["alpha", "beta", "task_read_light_data"]);
            host.TaskToolCalls.Should().Be(0);
            host.NativeJobToolCalls.Should().Be(0);
        }
        finally
        {
            TaskSharedData.Remove(instanceId);
        }
    }

    [Test]
    public async Task StreamAsync_WhenToolsAreEnabled_StreamsWithEffectiveTools()
    {
        var registry = CreateRegistry();
        var provider = new RecordingProviderRoundExecutor
        {
            StreamingDeltas = ["stream ", "answer"],
            StreamingResult = new ChatCompletionResult
            {
                Content = "stream answer",
                Usage = new TokenUsage(13, 17),
                ProviderMetadataJson = """{"id":"stream"}"""
            }
        };
        var engine = CreateEngine(registry);
        var host = new RecordingNativeToolLoopHost();
        var instanceId = Guid.NewGuid();
        var store = TaskSharedData.GetOrCreate(instanceId);
        store.RegisterBuiltInTools();

        try
        {
            var events = new List<ChatNativeToolStreamingLoopEvent>();
            await foreach (var loopEvent in engine.StreamAsync(
                CreateStreamingRequest(
                    provider,
                    host,
                    enableTools: true,
                    taskContext: new TaskChatContext(instanceId, "Task"))))
            {
                events.Add(loopEvent);
            }

            events.Select(loopEvent => loopEvent.Kind).Should().Equal(
                ChatNativeToolStreamingLoopEventKind.TextDelta,
                ChatNativeToolStreamingLoopEventKind.TextDelta,
                ChatNativeToolStreamingLoopEventKind.Completed);
            events[0].Text.Should().Be("stream ");
            events[1].Text.Should().Be("answer");
            var result = events[^1].Result!;
            result.AssistantContent.Should().Be("stream answer");
            result.TotalPromptTokens.Should().Be(13);
            result.TotalCompletionTokens.Should().Be(17);
            result.ProviderMetadataJson.Should().Be("""{"id":"stream"}""");
            result.ProviderRounds.Should().Be(1);
            provider.StreamingCalls.Should().Be(1);
            provider.LastStreamingToolNames.Should().Contain(["alpha", "beta", "task_read_light_data"]);
            host.TaskToolCalls.Should().Be(0);
            host.NativeJobToolCalls.Should().Be(0);
        }
        finally
        {
            TaskSharedData.Remove(instanceId);
        }
    }

    [Test]
    public async Task StreamAsync_WhenToolsAreDisabled_StreamsWithEmptyToolSet()
    {
        var provider = new RecordingProviderRoundExecutor
        {
            StreamingResult = new ChatCompletionResult
            {
                Content = "plain stream",
                Usage = new TokenUsage(19, 23)
            }
        };
        var engine = CreateEngine();
        var host = new RecordingNativeToolLoopHost();

        var events = new List<ChatNativeToolStreamingLoopEvent>();
        await foreach (var loopEvent in engine.StreamAsync(
            CreateStreamingRequest(
                provider,
                host,
                enableTools: false)))
        {
            events.Add(loopEvent);
        }

        var result = events.Single(loopEvent =>
            loopEvent.Kind == ChatNativeToolStreamingLoopEventKind.Completed).Result!;
        result.AssistantContent.Should().Be("plain stream");
        result.TotalPromptTokens.Should().Be(19);
        result.TotalCompletionTokens.Should().Be(23);
        provider.StreamingCalls.Should().Be(1);
        provider.LastStreamingToolNames.Should().BeEmpty();
        host.TaskToolCalls.Should().Be(0);
        host.NativeJobToolCalls.Should().Be(0);
    }

    [Test]
    public async Task RunBufferedAsync_WhenApprovalCannotResolve_RunsFinalProviderRound()
    {
        var provider = new RecordingProviderRoundExecutor();
        provider.NativeResults.Enqueue(
            new ChatCompletionResult
            {
                ToolCalls =
                [
                    new ChatToolCall(
                        "call-1",
                        "job.run",
                        """{"x":1}""")
                ],
                Usage = new TokenUsage(2, 3)
            });
        provider.NativeResults.Enqueue(
            new ChatCompletionResult
            {
                Content = "final after approval",
                Usage = new TokenUsage(5, 7),
                ProviderMetadataJson = """{"id":"final"}"""
            });
        var submittedJobId = Guid.NewGuid();
        var host = new RecordingNativeToolLoopHost();
        host.NativeJobResults.Enqueue(
            new ChatNativeJobToolExecutionResult(
                Parsed: true,
                ToolResultMessage: ToolAwareMessage.ToolResult(
                    "call-1",
                    "approval pending"),
                JobResponse: new AgentJobResponse(
                    submittedJobId,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    ActionKey: "job.run",
                    ResourceId: null,
                    Status: AgentJobStatus.AwaitingApproval,
                    EffectiveClearance: PermissionClearance.Independent,
                    ResultData: null,
                    ErrorLog: null,
                    Logs: [],
                    CreatedAt: DateTimeOffset.UtcNow,
                    StartedAt: null,
                    CompletedAt: null),
                SubmittedJobId: submittedJobId,
                ToolNotation: "[job submitted]",
                AwaitingUnresolvableApproval: true,
                StreamEvents: []));
        var engine = CreateEngine();

        var result = await engine.RunBufferedAsync(
            CreateRequest(
                provider,
                host,
                enableTools: true));

        result.AssistantContent.Should().Be(
            "[job submitted]\nfinal after approval");
        result.JobResults.Should().ContainSingle(job =>
            job.Id == submittedJobId);
        result.TotalPromptTokens.Should().Be(7);
        result.TotalCompletionTokens.Should().Be(10);
        result.ProviderMetadataJson.Should().Be("""{"id":"final"}""");
        provider.NativeCalls.Should().Be(2);
        host.NativeJobToolCalls.Should().Be(1);
        host.RecordedTokenUsage.Should().Be((
            submittedJobId,
            PromptTokens: 2,
            CompletionTokens: 3));
    }

    private static ChatBufferedProviderExecutionRequest CreateRequest(
        RecordingProviderRoundExecutor provider,
        RecordingNativeToolLoopHost host,
        bool enableTools,
        TaskChatContext? taskContext = null)
        => new(
            provider,
            "model",
            "system",
            [new ChatCompletionMessage("user", "hello")],
            Guid.NewGuid(),
            Guid.NewGuid(),
            new HashSet<string>(),
            MaxCompletionTokens: 128,
            ProviderParameters: null,
            CompletionParameters: null,
            enableTools,
            host,
            CancellationToken.None,
            TaskContext: taskContext);

    private static ChatStreamingProviderExecutionRequest CreateStreamingRequest(
        RecordingProviderRoundExecutor provider,
        RecordingNativeToolLoopHost host,
        bool enableTools,
        TaskChatContext? taskContext = null)
        => new(
            provider,
            "model",
            "system",
            [new ChatCompletionMessage("user", "hello")],
            Guid.NewGuid(),
            Guid.NewGuid(),
            new HashSet<string>(),
            MaxCompletionTokens: 128,
            ProviderParameters: null,
            CompletionParameters: null,
            enableTools,
            host,
            CancellationToken.None,
            TaskContext: taskContext);

    private static ChatProviderExecutionWorkflowEngine CreateEngine(
        ModuleRegistry? registry = null)
    {
        var cache = new ChatCache(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Chat:CacheMaxMegabytes"] = "1"
                    })
                .Build());
        var tools = new ChatToolWorkflowEngine(
            registry ?? CreateRegistry(),
            cache,
            new ChatToolSelectionEngine());

        return new ChatProviderExecutionWorkflowEngine(
            new ChatNativeToolLoopEngine(new ChatToolResultEngine()),
            tools);
    }

    private static ModuleRegistry CreateRegistry()
    {
        var registry = new ModuleRegistry();
        registry.Register(new ToolModule());
        return registry;
    }

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed class RecordingProviderRoundExecutor : IChatProviderRoundExecutor
    {
        public ChatCompletionResult PlainResult { get; init; } = new()
        {
            Content = "plain answer"
        };

        public ChatCompletionResult NativeResult { get; init; } = new()
        {
            Content = "native answer"
        };
        public Queue<ChatCompletionResult> NativeResults { get; } = new();

        public IReadOnlyList<string> StreamingDeltas { get; init; } = [];
        public ChatCompletionResult StreamingResult { get; init; } = new()
        {
            Content = "stream answer"
        };

        public int PlainCalls { get; private set; }
        public int NativeCalls { get; private set; }
        public int StreamingCalls { get; private set; }
        public IReadOnlyList<string> LastNativeToolNames { get; private set; } = [];
        public IReadOnlyList<string> LastStreamingToolNames { get; private set; } = [];

        public Task<ChatCompletionResult> CompleteAsync(
            ChatProviderCompletionRequest request,
            CancellationToken ct)
        {
            PlainCalls++;
            return Task.FromResult(PlainResult);
        }

        public Task<ChatCompletionResult> CompleteWithToolsAsync(
            ChatProviderToolCompletionRequest request,
            CancellationToken ct)
        {
            NativeCalls++;
            LastNativeToolNames = [.. request.Tools.Select(tool => tool.Name)];
            return Task.FromResult(
                NativeResults.Count > 0
                    ? NativeResults.Dequeue()
                    : NativeResult);
        }

        public async IAsyncEnumerable<ChatStreamChunk> StreamWithToolsAsync(
            ChatProviderToolCompletionRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            StreamingCalls++;
            LastStreamingToolNames = [.. request.Tools.Select(tool => tool.Name)];
            await Task.CompletedTask;

            foreach (var delta in StreamingDeltas)
            {
                ct.ThrowIfCancellationRequested();
                yield return ChatStreamChunk.Text(delta);
            }

            ct.ThrowIfCancellationRequested();
            yield return ChatStreamChunk.Final(StreamingResult);
        }
    }

    private sealed class RecordingNativeToolLoopHost : IChatNativeToolLoopHost
    {
        public int TaskToolCalls { get; private set; }
        public int NativeJobToolCalls { get; private set; }
        public Queue<ChatNativeJobToolExecutionResult> NativeJobResults { get; } = new();
        public (Guid JobId, int PromptTokens, int CompletionTokens)? RecordedTokenUsage { get; private set; }

        public bool IsInlineTool(string toolName) => false;

        public Task<(bool Handled, string? Result)> TryHandleTaskToolAsync(
            ChatToolCall toolCall,
            TaskChatContext? taskContext,
            CancellationToken ct)
        {
            TaskToolCalls++;
            return Task.FromResult((false, (string?)null));
        }

        public Task<string> ExecuteInlineToolAsync(
            ChatToolCall toolCall,
            Guid agentId,
            Guid channelId,
            Guid? threadId,
            IDictionary<ChatInlineToolPermissionCacheKey, AgentActionResult> permissionCache,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ChatNativeJobToolExecutionResult> ExecuteNativeJobToolAsync(
            ChatToolCall toolCall,
            Guid agentId,
            Guid channelId,
            bool supportsVision,
            bool emitStreamEvents,
            Func<AgentJobResponse, CancellationToken, Task<bool>>? approvalCallback,
            CancellationToken ct)
        {
            NativeJobToolCalls++;
            if (NativeJobResults.Count == 0)
                throw new NotSupportedException();

            return Task.FromResult(NativeJobResults.Dequeue());
        }

        public Task RecordRoundTokenUsageAsync(
            IReadOnlyList<Guid> jobIds,
            int promptTokens,
            int completionTokens,
            CancellationToken ct)
        {
            RecordedTokenUsage = (
                jobIds.Single(),
                promptTokens,
                completionTokens);
            return Task.CompletedTask;
        }
    }

    private sealed class ToolModule : ISharpClawCoreModule
    {
        public string Id => "tool_module";
        public string DisplayName => "Tool Module";
        public string ToolPrefix => "tool";

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() =>
        [
            new("alpha", "Alpha", Json("""{"type":"object"}"""), Permission: null),
            new("beta", "Beta", Json("""{"type":"object"}"""), Permission: null)
        ];

        public Task<string> ExecuteToolAsync(
            string toolName,
            JsonElement parameters,
            AgentJobContext job,
            IServiceProvider scopedServices,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
