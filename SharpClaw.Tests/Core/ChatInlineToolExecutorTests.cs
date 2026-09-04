using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Chat;
using SharpClaw.Core.Modules;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ChatInlineToolExecutorTests
{
    [Test]
    public async Task ExecuteAsync_WhenToolAllowed_InvokesRegistrationThroughRestrictedScope()
    {
        var module = new InlineRegistration(permission: null);
        var registry = CreateRegistry(module);
        var metrics = new PackageMetricsCollector();
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
        result.RegistrationInvoked.Should().BeTrue();
        provider.GetRequiredService<RegistrationExecutionContext>()
            .SourceId
            .Should()
            .Be("test_registration");

        var snapshot = metrics.GetToolMetrics("test_ping");
        snapshot.Should().NotBeNull();
        snapshot!.TotalCalls.Should().Be(1);
        snapshot.SuccessCount.Should().Be(1);
    }

    [Test]
    public async Task ExecuteAsync_WhenPermissionIsDeclared_CachesHostVerdict()
    {
        var module = new InlineRegistration(CreatePermission());
        var registry = CreateRegistry(module);
        var permissionCache =
            new Dictionary<ChatInlineToolPermissionCacheKey, AgentActionResult>();
        var checkCount = 0;
        var executor = new ChatInlineToolExecutor(new PackageMetricsCollector());
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
    public async Task ExecuteAsync_WhenPermissionDenied_DoesNotInvokeRegistration()
    {
        var module = new InlineRegistration(CreatePermission());
        var registry = CreateRegistry(module);
        var metrics = new PackageMetricsCollector();
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
        result.RegistrationInvoked.Should().BeFalse();
        module.Calls.Should().Be(0);
        metrics.GetToolMetrics("test_ping").Should().BeNull();
    }

    [Test]
    public async Task ExecuteAsync_WhenArgumentsAreMalformed_DoesNotInvokeRegistration()
    {
        var module = new InlineRegistration(permission: null);
        var executor = new ChatInlineToolExecutor(new PackageMetricsCollector());

        var result = await executor.ExecuteAsync(
            Request(
                new ChatToolCall("call-1", "ping", "{"),
                Guid.NewGuid(),
                Guid.NewGuid(),
                CreateRegistry(module),
                CreateProvider()));

        result.ToolResult.Should().Be("Error: malformed tool arguments JSON.");
        result.RegistrationInvoked.Should().BeFalse();
        module.Calls.Should().Be(0);
    }

    [Test]
    public async Task ExecuteAsync_WhenRegistrationThrows_ReturnsErrorAndRecordsFailure()
    {
        var module = new InlineRegistration(permission: null)
        {
            ThrowOnExecute = true
        };
        var registry = CreateRegistry(module);
        var metrics = new PackageMetricsCollector();
        var executor = new ChatInlineToolExecutor(metrics);

        var result = await executor.ExecuteAsync(
            Request(
                new ChatToolCall("call-1", "ping", """{"value":7}"""),
                Guid.NewGuid(),
                Guid.NewGuid(),
                registry,
                CreateProvider()));

        result.ToolResult.Should().Be("Error executing inline tool 'ping': boom");
        result.RegistrationInvoked.Should().BeTrue();
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
        RegistrationCatalog registry,
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
        services.AddSingleton<RegistrationExecutionContext>();
        return services.BuildServiceProvider();
    }

    private static RegistrationCatalog CreateRegistry(InlineRegistration module)
    {
        var registry = new RegistrationCatalog();
        registry.Register(module);
        return registry;
    }

    private static RegistrationToolPermission CreatePermission() =>
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

    private sealed class InlineRegistration(RegistrationToolPermission? permission)
        : ISharpClawCoreRegistration
    {
        public int Calls { get; private set; }
        public bool ThrowOnExecute { get; init; }
        public string Id => "test_registration";
        public string DisplayName => "Test Module";
        public string ToolPrefix => "test";

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public IReadOnlyList<RegistrationToolDefinition> GetToolDefinitions() => [];

        public IReadOnlyList<RegistrationInlineToolDefinition> GetInlineToolDefinitions() =>
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
