using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Chat;
using SharpClaw.Core.Modules;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ChatInlineToolExecutorTests
{
    [Test]
    public async Task ExecuteAsync_WhenToolAllowed_InvokesModuleThroughRestrictedScope()
    {
        var module = new InlineModule(permission: null);
        var registry = CreateRegistry(module);
        var metrics = new ModuleMetricsCollector();
        var provider = CreateProvider();
        var executor = new ChatInlineToolExecutor(metrics);
        var agentId = Guid.NewGuid();
        var channelId = Guid.NewGuid();

        var result = await executor.ExecuteAsync(
            Request(
                new ChatToolCall("call-1", "ping", """{"value":7}"""),
                agentId,
                channelId,
                registry,
                provider));

        result.ToolResult.Should().Be($"pong:7:{agentId:D}:{channelId:D}:call-1");
        result.Succeeded.Should().BeTrue();
        result.ModuleInvoked.Should().BeTrue();
        provider.GetRequiredService<ModuleExecutionContext>()
            .ModuleId
            .Should()
            .Be("test_module");

        var snapshot = metrics.GetToolMetrics("test_ping");
        snapshot.Should().NotBeNull();
        snapshot!.TotalCalls.Should().Be(1);
        snapshot.SuccessCount.Should().Be(1);
    }

    [Test]
    public async Task ExecuteAsync_WhenPermissionIsDeclared_CachesHostVerdict()
    {
        var module = new InlineModule(CreatePermission());
        var registry = CreateRegistry(module);
        var permissionCache =
            new Dictionary<ChatInlineToolPermissionCacheKey, AgentActionResult>();
        var checkCount = 0;
        var executor = new ChatInlineToolExecutor(new ModuleMetricsCollector());
        var provider = CreateProvider();
        var request = Request(
            new ChatToolCall("call-1", "ping", """{"value":7}"""),
            Guid.NewGuid(),
            Guid.NewGuid(),
            registry,
            provider,
            permissionCache,
            (_, _) =>
            {
                checkCount++;
                return Task.FromResult(Approved());
            });

        await executor.ExecuteAsync(request);
        await executor.ExecuteAsync(request);

        checkCount.Should().Be(1);
        module.Calls.Should().Be(2);
    }

    [Test]
    public async Task ExecuteAsync_WhenPermissionDenied_DoesNotInvokeModule()
    {
        var module = new InlineModule(CreatePermission());
        var registry = CreateRegistry(module);
        var metrics = new ModuleMetricsCollector();
        var executor = new ChatInlineToolExecutor(metrics);

        var result = await executor.ExecuteAsync(
            Request(
                new ChatToolCall("call-1", "ping", """{"value":7}"""),
                Guid.NewGuid(),
                Guid.NewGuid(),
                registry,
                CreateProvider(),
                checkPermission: (_, _) => Task.FromResult(
                    AgentActionResult.Denied("no"))));

        result.ToolResult.Should().Be("Error: permission denied for inline tool 'ping': no");
        result.ModuleInvoked.Should().BeFalse();
        module.Calls.Should().Be(0);
        metrics.GetToolMetrics("test_ping").Should().BeNull();
    }

    [Test]
    public async Task ExecuteAsync_WhenArgumentsAreMalformed_DoesNotInvokeModule()
    {
        var module = new InlineModule(permission: null);
        var executor = new ChatInlineToolExecutor(new ModuleMetricsCollector());

        var result = await executor.ExecuteAsync(
            Request(
                new ChatToolCall("call-1", "ping", "{"),
                Guid.NewGuid(),
                Guid.NewGuid(),
                CreateRegistry(module),
                CreateProvider()));

        result.ToolResult.Should().Be("Error: malformed tool arguments JSON.");
        result.ModuleInvoked.Should().BeFalse();
        module.Calls.Should().Be(0);
    }

    [Test]
    public async Task ExecuteAsync_WhenModuleThrows_ReturnsErrorAndRecordsFailure()
    {
        var module = new InlineModule(permission: null)
        {
            ThrowOnExecute = true
        };
        var registry = CreateRegistry(module);
        var metrics = new ModuleMetricsCollector();
        var executor = new ChatInlineToolExecutor(metrics);

        var result = await executor.ExecuteAsync(
            Request(
                new ChatToolCall("call-1", "ping", """{"value":7}"""),
                Guid.NewGuid(),
                Guid.NewGuid(),
                registry,
                CreateProvider()));

        result.ToolResult.Should().Be("Error executing inline tool 'ping': boom");
        result.ModuleInvoked.Should().BeTrue();
        result.Succeeded.Should().BeFalse();
        result.Exception.Should().BeOfType<InvalidOperationException>();

        var snapshot = metrics.GetToolMetrics("test_ping");
        snapshot.Should().NotBeNull();
        snapshot!.TotalCalls.Should().Be(1);
        snapshot.FailureCount.Should().Be(1);
    }

    private static ChatInlineToolExecutionRequest Request(
        ChatToolCall toolCall,
        Guid agentId,
        Guid channelId,
        ModuleRegistry registry,
        IServiceProvider provider,
        IDictionary<ChatInlineToolPermissionCacheKey, AgentActionResult>? permissionCache = null,
        Func<ChatInlineToolPermissionCheck, CancellationToken, Task<AgentActionResult>>? checkPermission = null) =>
        new(
            toolCall,
            agentId,
            channelId,
            ThreadId: null,
            registry,
            permissionCache ?? new Dictionary<ChatInlineToolPermissionCacheKey, AgentActionResult>(),
            checkPermission ?? ((_, _) => Task.FromResult(Approved())),
            provider,
            [typeof(BlockedService)]);

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<AllowedService>();
        services.AddSingleton<BlockedService>();
        services.AddSingleton<ModuleExecutionContext>();
        return services.BuildServiceProvider();
    }

    private static ModuleRegistry CreateRegistry(InlineModule module)
    {
        var registry = new ModuleRegistry();
        registry.Register(module);
        return registry;
    }

    private static ModuleToolPermission CreatePermission() =>
        new(
            IsPerResource: false,
            Check: (_, _, _, _) => Task.FromResult(Approved()));

    private static AgentActionResult Approved() =>
        AgentActionResult.Approve(
            "ok",
            PermissionClearance.ApprovedByWhitelistedUser);

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed class InlineModule(ModuleToolPermission? permission)
        : ISharpClawCoreModule
    {
        public int Calls { get; private set; }
        public bool ThrowOnExecute { get; init; }
        public string Id => "test_module";
        public string DisplayName => "Test Module";
        public string ToolPrefix => "test";

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];

        public IReadOnlyList<ModuleInlineToolDefinition> GetInlineToolDefinitions() =>
        [
            new(
                "ping",
                "Ping",
                Json("""{"type":"object"}"""),
                permission)
        ];

        public Task<string> ExecuteToolAsync(
            string toolName,
            JsonElement parameters,
            AgentJobContext job,
            IServiceProvider scopedServices,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> ExecuteInlineToolAsync(
            string toolName,
            JsonElement parameters,
            InlineToolContext context,
            IServiceProvider scopedServices,
            CancellationToken ct)
        {
            Calls++;

            if (ThrowOnExecute)
                throw new InvalidOperationException("boom");

            toolName.Should().Be("ping");
            scopedServices.GetRequiredService<AllowedService>()
                .Should()
                .NotBeNull();
            var blocked = () => scopedServices.GetRequiredService<BlockedService>();
            blocked.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*blocked service*BlockedService*");

            var value = parameters.GetProperty("value").GetInt32();
            return Task.FromResult(
                $"pong:{value}:{context.AgentId:D}:{context.ChannelId:D}:{context.ToolCallId}");
        }
    }

    private sealed class AllowedService;

    private sealed class BlockedService;
}
