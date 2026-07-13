using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Modules;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ModuleToolPermissionExecutorTests
{
    [Test]
    public async Task ExecuteAsync_WhenPerResourceToolHasNoResourceId_DeniesAndTraces()
    {
        var registry = CreateRegistry(new ModuleToolPermission(
            IsPerResource: true,
            Check: DirectApprove));
        var traces = new List<string>();
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync(new(
            "run",
            null,
            Guid.NewGuid(),
            new ActionCaller(),
            registry,
            (_, _, _, _, _) => throw new InvalidOperationException(
                "Host delegate should not be called."),
            traces.Add));

        result.Verdict.Should().Be(ClearanceVerdict.Denied);
        result.Reason.Should().Be("ResourceId is required for module tool 'run'.");
        traces.Should().ContainSingle()
            .Which.Should().Contain("ResourceId is null");
    }

    [Test]
    public async Task ExecuteAsync_WhenDirectCheckIsConfigured_ReturnsDirectResult()
    {
        var registry = CreateRegistry(new ModuleToolPermission(
            IsPerResource: true,
            Check: DirectApprove));
        var resourceId = Guid.NewGuid();
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync(new(
            "run",
            resourceId,
            Guid.NewGuid(),
            new ActionCaller(),
            registry,
            (_, _, _, _, _) => throw new InvalidOperationException(
                "Host delegate should not be called.")));

        result.Verdict.Should().Be(ClearanceVerdict.Approved);
        result.Reason.Should().Be("direct ok");
    }

    [Test]
    public async Task ExecuteAsync_WhenDelegateIsConfigured_ReturnsHostResult()
    {
        var registry = CreateRegistry(new ModuleToolPermission(
            IsPerResource: true,
            Check: null,
            DelegateTo: "CanRun"));
        var agentId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var caller = new ActionCaller();
        var executor = CreateExecutor();
        string? observedDelegate = null;
        Guid? observedAgentId = null;
        Guid? observedResourceId = null;
        ActionCaller? observedCaller = null;

        var result = await executor.ExecuteAsync(new(
            "run",
            resourceId,
            agentId,
            caller,
            registry,
            (delegateName, targetAgentId, targetResourceId,
                targetCaller, _) =>
            {
                observedDelegate = delegateName;
                observedAgentId = targetAgentId;
                observedResourceId = targetResourceId;
                observedCaller = targetCaller;
                return Task.FromResult<AgentActionResult?>(
                    AgentActionResult.Approve(
                        "host ok",
                        PermissionClearance.ApprovedByWhitelistedUser));
            }));

        result.Verdict.Should().Be(ClearanceVerdict.Approved);
        result.Reason.Should().Be("host ok");
        observedDelegate.Should().Be("CanRun");
        observedAgentId.Should().Be(agentId);
        observedResourceId.Should().Be(resourceId);
        observedCaller.Should().BeSameAs(caller);
    }

    [Test]
    public async Task ExecuteAsync_WhenDelegateIsUnrecognized_Denies()
    {
        var registry = CreateRegistry(new ModuleToolPermission(
            IsPerResource: false,
            Check: null,
            DelegateTo: "UnknownDelegate"));
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync(new(
            "run",
            null,
            Guid.NewGuid(),
            new ActionCaller(),
            registry,
            (_, _, _, _, _) => Task.FromResult<AgentActionResult?>(null)));

        result.Verdict.Should().Be(ClearanceVerdict.Denied);
        result.Reason.Should().Be(
            "Module tool 'run' delegates to 'UnknownDelegate' which is not a recognised permission check method.");
    }

    private static ModuleToolPermissionExecutor CreateExecutor() =>
        new(new ModuleToolPermissionPlanner());

    private static ModuleRegistry CreateRegistry(ModuleToolPermission permission)
    {
        var registry = new ModuleRegistry();
        registry.Register(new TestModule(permission));
        return registry;
    }

    private static Task<AgentActionResult> DirectApprove(
        Guid agentId,
        Guid? resourceId,
        ActionCaller caller,
        CancellationToken ct)
    {
        return Task.FromResult(AgentActionResult.Approve(
            "direct ok",
            PermissionClearance.ApprovedByWhitelistedUser));
    }

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed class TestModule(ModuleToolPermission permission)
        : ISharpClawCoreModule
    {
        public string Id => "test_module";
        public string DisplayName => "Test Module";
        public string ToolPrefix => "test";

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() =>
        [
            new(
                "run",
                "Run",
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
    }
}
