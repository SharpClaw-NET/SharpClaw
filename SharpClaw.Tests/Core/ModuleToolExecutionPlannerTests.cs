using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Modules;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ModuleToolExecutionPlannerTests
{
    [Test]
    public void BuildPlan_WhenActionKeyResolvesAndScriptJsonIsRawParameters_UsesResolvedTool()
    {
        var registry = CreateRegistry();
        var planner = new ModuleToolExecutionPlanner();

        var plan = planner.BuildPlan(
            actionKey: "run",
            scriptJson: """{"value":42}""",
            maxEnvelopeBytes: 1024,
            moduleRegistry: registry);

        plan.ModuleId.Should().Be("test_module");
        plan.ToolName.Should().Be("run");
        plan.ResolvedFromActionKey.Should().BeTrue();
        plan.Parameters.GetProperty("value").GetInt32().Should().Be(42);
    }

    [Test]
    public void BuildPlan_WhenActionKeyResolvesAndScriptJsonIsFullEnvelope_UsesNestedParameters()
    {
        var registry = CreateRegistry();
        var planner = new ModuleToolExecutionPlanner();

        var plan = planner.BuildPlan(
            actionKey: "run",
            scriptJson: """
            {"module":"other","tool":"ignored","params":{"value":7}}
            """,
            maxEnvelopeBytes: 1024,
            moduleRegistry: registry);

        plan.ModuleId.Should().Be("test_module");
        plan.ToolName.Should().Be("run");
        plan.ResolvedFromActionKey.Should().BeTrue();
        plan.Parameters.GetProperty("value").GetInt32().Should().Be(7);
    }

    [Test]
    public void BuildPlan_WhenActionKeyDoesNotResolve_DeserializesFullEnvelope()
    {
        var registry = CreateRegistry();
        var planner = new ModuleToolExecutionPlanner();

        var plan = planner.BuildPlan(
            actionKey: "missing",
            scriptJson: """
            {"module":"test_module","tool":"run","params":{"value":3}}
            """,
            maxEnvelopeBytes: 1024,
            moduleRegistry: registry);

        plan.ModuleId.Should().Be("test_module");
        plan.ToolName.Should().Be("run");
        plan.ResolvedFromActionKey.Should().BeFalse();
        plan.Parameters.GetProperty("value").GetInt32().Should().Be(3);
    }

    [Test]
    public void BuildPlan_WhenNoActionKeyAndNoScriptJson_Throws()
    {
        var registry = CreateRegistry();
        var planner = new ModuleToolExecutionPlanner();

        var act = () => planner.BuildPlan(
            actionKey: null,
            scriptJson: null,
            maxEnvelopeBytes: 1024,
            moduleRegistry: registry);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Module action requires a ScriptJson envelope.");
    }

    [Test]
    public void BuildPlan_WhenEnvelopeExceedsLimit_Throws()
    {
        var registry = CreateRegistry();
        var planner = new ModuleToolExecutionPlanner();

        var act = () => planner.BuildPlan(
            actionKey: null,
            scriptJson: """{"module":"test_module","tool":"run","params":{}}""",
            maxEnvelopeBytes: 10,
            moduleRegistry: registry);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("ScriptJson exceeds maximum envelope size (10 bytes).");
    }

    [Test]
    public void CreateEnvelopeJson_SerializesStandardEnvelope()
    {
        var planner = new ModuleToolExecutionPlanner();

        var json = planner.CreateEnvelopeJson(
            "test_module",
            "run",
            """{"value":5}""");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("module").GetString().Should().Be("test_module");
        doc.RootElement.GetProperty("tool").GetString().Should().Be("run");
        doc.RootElement.GetProperty("params").GetProperty("value").GetInt32().Should().Be(5);
    }

    private static ModuleRegistry CreateRegistry()
    {
        var registry = new ModuleRegistry();
        registry.Register(new TestModule());
        return registry;
    }

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed class TestModule : ISharpClawCoreModule
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
                new ModuleToolPermission(
                    IsPerResource: false,
                    Check: (_, _, _, _) => Task.FromResult(
                        AgentActionResult.Approve(
                            "ok",
                            PermissionClearance.ApprovedByWhitelistedUser))))
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
