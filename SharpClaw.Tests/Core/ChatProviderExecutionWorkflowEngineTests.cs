using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Tasks;
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
        var client = new RecordingProviderClient
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
                client,
                host,
                enableTools: false));

        result.AssistantContent.Should().Be("");
        result.JobResults.Should().BeEmpty();
        result.TotalPromptTokens.Should().Be(3);
        result.TotalCompletionTokens.Should().Be(5);
        result.ProviderMetadataJson.Should().Be("""{"id":"plain"}""");
        client.PlainCalls.Should().Be(1);
        client.NativeCalls.Should().Be(0);
        host.TaskToolCalls.Should().Be(0);
        host.NativeJobToolCalls.Should().Be(0);
    }

    [Test]
    public async Task RunBufferedAsync_WhenToolsAreEnabled_UsesNativeLoopWithEffectiveTools()
    {
        var registry = CreateRegistry();
        var client = new RecordingProviderClient
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
                    client,
                    host,
                    enableTools: true,
                    taskContext: new TaskChatContext(instanceId, "Task")));

            result.AssistantContent.Should().Be("native answer");
            result.JobResults.Should().BeEmpty();
            result.TotalPromptTokens.Should().Be(7);
            result.TotalCompletionTokens.Should().Be(11);
            result.ProviderMetadataJson.Should().Be("""{"id":"native"}""");
            client.PlainCalls.Should().Be(0);
            client.NativeCalls.Should().Be(1);
            client.LastNativeToolNames.Should().Contain(["alpha", "beta", "task_read_light_data"]);
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
        var client = new RecordingProviderClient
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
                    client,
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
            client.StreamingCalls.Should().Be(1);
            client.LastStreamingToolNames.Should().Contain(["alpha", "beta", "task_read_light_data"]);
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
        var client = new RecordingProviderClient
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
                client,
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
        client.StreamingCalls.Should().Be(1);
        client.LastStreamingToolNames.Should().BeEmpty();
        host.TaskToolCalls.Should().Be(0);
        host.NativeJobToolCalls.Should().Be(0);
    }

    private static ChatBufferedProviderExecutionRequest CreateRequest(
        RecordingProviderClient client,
        RecordingNativeToolLoopHost host,
        bool enableTools,
        TaskChatContext? taskContext = null)
        => new(
            client,
            new HttpClient(),
            "api-key",
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
        RecordingProviderClient client,
        RecordingNativeToolLoopHost host,
        bool enableTools,
        TaskChatContext? taskContext = null)
        => new(
            client,
            new HttpClient(),
            "api-key",
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

    private sealed class RecordingProviderClient : IProviderApiClient
    {
        public string ProviderKey => "test";
        public bool SupportsNativeToolCalling => true;
        public ChatCompletionResult PlainResult { get; init; } = new()
        {
            Content = "plain answer"
        };

        public ChatCompletionResult NativeResult { get; init; } = new()
        {
            Content = "native answer"
        };

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

        public Task<IReadOnlyList<string>> ListModelIdsAsync(
            HttpClient httpClient,
            string apiKey,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(["model"]);

        public Task<ChatCompletionResult> ChatCompletionAsync(
            HttpClient httpClient,
            string apiKey,
            string model,
            string? systemPrompt,
            IReadOnlyList<ChatCompletionMessage> messages,
            int? maxCompletionTokens = null,
            Dictionary<string, JsonElement>? providerParameters = null,
            CompletionParameters? completionParameters = null,
            CancellationToken ct = default)
        {
            PlainCalls++;
            return Task.FromResult(PlainResult);
        }

        public Task<ChatCompletionResult> ChatCompletionWithToolsAsync(
            HttpClient httpClient,
            string apiKey,
            string model,
            string? systemPrompt,
            IReadOnlyList<ToolAwareMessage> messages,
            IReadOnlyList<ChatToolDefinition> tools,
            int? maxCompletionTokens = null,
            Dictionary<string, JsonElement>? providerParameters = null,
            CompletionParameters? completionParameters = null,
            CancellationToken ct = default)
        {
            NativeCalls++;
            LastNativeToolNames = [.. tools.Select(tool => tool.Name)];
            return Task.FromResult(NativeResult);
        }

        public async IAsyncEnumerable<ChatStreamChunk> StreamChatCompletionWithToolsAsync(
            HttpClient httpClient,
            string apiKey,
            string model,
            string? systemPrompt,
            IReadOnlyList<ToolAwareMessage> messages,
            IReadOnlyList<ChatToolDefinition> tools,
            int? maxCompletionTokens = null,
            Dictionary<string, JsonElement>? providerParameters = null,
            CompletionParameters? completionParameters = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            StreamingCalls++;
            LastStreamingToolNames = [.. tools.Select(tool => tool.Name)];
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
            throw new NotSupportedException();
        }

        public Task RecordRoundTokenUsageAsync(
            IReadOnlyList<Guid> jobIds,
            int promptTokens,
            int completionTokens,
            CancellationToken ct) =>
            Task.CompletedTask;
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
