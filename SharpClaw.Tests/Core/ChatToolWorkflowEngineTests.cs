using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Chat;
using SharpClaw.Core.Modules;
using SharpClaw.Core.Tasks.Runtime;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ChatToolWorkflowEngineTests
{
    [Test]
    public async Task GetEffectiveToolsAsync_WhenNoTaskContext_CachesByAgentAndAwareness()
    {
        var registry = CreateRegistry();
        var cache = CreateCache();
        var selection = new ChatToolSelectionEngine();
        var engine = CreateEngine(registry, cache, selection);
        var agentId = Guid.NewGuid();
        var awareness = new Dictionary<string, bool>
        {
            ["beta"] = false
        };

        var tools = await engine.GetEffectiveToolsAsync(
            new ChatEffectiveToolRequest(
                TaskContext: null,
                awareness,
                agentId));

        tools.Select(tool => tool.Name).Should().Equal("alpha");
        var cacheKey = ChatCache.KeyEffectiveTools(
            agentId,
            selection.BuildAwarenessFingerprint(awareness));
        cache.TryGet<IReadOnlyList<ChatToolDefinition>>(
                cacheKey,
                out var cached)
            .Should()
            .BeTrue();
        cached!.Select(tool => tool.Name).Should().Equal("alpha");
    }

    [Test]
    public async Task GetEffectiveToolsAsync_WhenTaskContextExists_AppendsTaskToolsWithoutAgentCache()
    {
        var registry = CreateRegistry();
        var cache = CreateCache();
        var selection = new ChatToolSelectionEngine();
        var engine = CreateEngine(registry, cache, selection);
        var agentId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var store = TaskSharedData.GetOrCreate(instanceId);
        store.RegisterBuiltInTools();

        try
        {
            var tools = await engine.GetEffectiveToolsAsync(
                new ChatEffectiveToolRequest(
                    new TaskChatContext(instanceId, "Task"),
                    ToolAwareness: null,
                    agentId));

            tools.Select(tool => tool.Name)
                .Should()
                .Contain(["alpha", "beta", "task_read_light_data"]);
            var cacheKey = ChatCache.KeyEffectiveTools(
                agentId,
                selection.BuildAwarenessFingerprint(null));
            cache.TryGet<IReadOnlyList<ChatToolDefinition>>(
                    cacheKey,
                    out _)
                .Should()
                .BeFalse();
        }
        finally
        {
            TaskSharedData.Remove(instanceId);
        }
    }

    [Test]
    public async Task GetEffectiveToolsAsync_AppliesAwarenessAfterTaskToolsAreAdded()
    {
        var engine = CreateEngine();
        var instanceId = Guid.NewGuid();
        var store = TaskSharedData.GetOrCreate(instanceId);
        store.RegisterBuiltInTools();

        try
        {
            var tools = await engine.GetEffectiveToolsAsync(
                new ChatEffectiveToolRequest(
                    new TaskChatContext(instanceId, "Task"),
                    new Dictionary<string, bool>
                    {
                        ["alpha"] = false,
                        ["task_read_light_data"] = false
                    },
                    AgentId: null));

            tools.Select(tool => tool.Name).Should().NotContain("alpha");
            tools.Select(tool => tool.Name).Should().NotContain("task_read_light_data");
            tools.Select(tool => tool.Name).Should().Contain("beta");
        }
        finally
        {
            TaskSharedData.Remove(instanceId);
        }
    }

    [Test]
    public async Task TryHandleTaskToolAsync_WhenBuiltInTaskToolExists_InvokesTool()
    {
        var engine = CreateEngine();
        var instanceId = Guid.NewGuid();
        var store = TaskSharedData.GetOrCreate(instanceId);
        store.RegisterBuiltInTools();

        try
        {
            var result = await engine.TryHandleTaskToolAsync(
                new ChatToolCall(
                    "call-1",
                    "task_write_light_data",
                    """{"text":"hello task"}"""),
                new TaskChatContext(instanceId, "Task"));

            result.Handled.Should().BeTrue();
            result.Result.Should().Be("OK: light shared data written.");
            store.LightData.Should().Be("hello task");
        }
        finally
        {
            TaskSharedData.Remove(instanceId);
        }
    }

    [Test]
    public async Task TryHandleTaskToolAsync_WhenToolIsUnknown_ReturnsUnhandled()
    {
        var engine = CreateEngine();
        var instanceId = Guid.NewGuid();
        var store = TaskSharedData.GetOrCreate(instanceId);
        store.RegisterBuiltInTools();

        try
        {
            var result = await engine.TryHandleTaskToolAsync(
                new ChatToolCall("call-1", "missing_tool", "{}"),
                new TaskChatContext(instanceId, "Task"));

            result.Handled.Should().BeFalse();
            result.Result.Should().BeNull();
        }
        finally
        {
            TaskSharedData.Remove(instanceId);
        }
    }

    [Test]
    public async Task TryHandleTaskToolAsync_WhenArgumentsAreMalformed_ReturnsHandledError()
    {
        var engine = CreateEngine();
        var instanceId = Guid.NewGuid();
        var store = TaskSharedData.GetOrCreate(instanceId);
        store.RegisterBuiltInTools();

        try
        {
            var result = await engine.TryHandleTaskToolAsync(
                new ChatToolCall("call-1", "task_write_light_data", "{"),
                new TaskChatContext(instanceId, "Task"));

            result.Handled.Should().BeTrue();
            result.Result.Should().StartWith(
                "Error handling task tool 'task_write_light_data':");
        }
        finally
        {
            TaskSharedData.Remove(instanceId);
        }
    }

    private static ChatToolWorkflowEngine CreateEngine(
        ModuleRegistry? registry = null,
        ChatCache? cache = null,
        ChatToolSelectionEngine? selection = null)
        => new(
            registry ?? CreateRegistry(),
            cache ?? CreateCache(),
            selection ?? new ChatToolSelectionEngine());

    private static ChatCache CreateCache()
        => new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Chat:CacheMaxMegabytes"] = "1"
                    })
                .Build());

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
