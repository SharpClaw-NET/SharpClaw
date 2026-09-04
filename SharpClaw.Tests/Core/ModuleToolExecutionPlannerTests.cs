using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Modules;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class RegistrationToolExecutionPlannerTests
{
    [Test]
    public void BuildPlan_WhenActionKeyResolvesAndScriptJsonIsRawParameters_UsesResolvedTool()
    {
        var registry = CreateRegistry();
        var planner = new RegistrationToolExecutionPlanner();

        var plan = planner.BuildPlan(
            actionKey: "run",
            scriptJson: """{"value":42}""",
            maxEnvelopeBytes: 1024,
            moduleRegistry: registry);

        plan.SourceId.Should().Be("test_registration");
        plan.ToolName.Should().Be("run");
        plan.ResolvedFromActionKey.Should().BeTrue();
        plan.Parameters.GetProperty("value").GetInt32().Should().Be(42);
    }

    [Test]
    public void BuildPlan_WhenActionKeyResolvesAndScriptJsonIsFullEnvelope_UsesNestedParameters()
    {
        var registry = CreateRegistry();
        var planner = new RegistrationToolExecutionPlanner();

        var plan = planner.BuildPlan(
            actionKey: "run",
            scriptJson: """
            {"module":"other","tool":"ignored","params":{"value":7}}
            """,
            maxEnvelopeBytes: 1024,
            moduleRegistry: registry);

        plan.SourceId.Should().Be("test_registration");
        plan.ToolName.Should().Be("run");
        plan.ResolvedFromActionKey.Should().BeTrue();
        plan.Parameters.GetProperty("value").GetInt32().Should().Be(7);
    }

    [Test]
    public void BuildPlan_WhenActionKeyDoesNotResolve_DeserializesFullEnvelope()
    {
        var registry = CreateRegistry();
        var planner = new RegistrationToolExecutionPlanner();

        var plan = planner.BuildPlan(
            actionKey: "missing",
            scriptJson: """
            {"module":"test_registration","tool":"run","params":{"value":3}}
            """,
            maxEnvelopeBytes: 1024,
            moduleRegistry: registry);

        plan.SourceId.Should().Be("test_registration");
        plan.ToolName.Should().Be("run");
        plan.ResolvedFromActionKey.Should().BeFalse();
        plan.Parameters.GetProperty("value").GetInt32().Should().Be(3);
    }

    [Test]
    public void BuildPlan_WhenNoActionKeyAndNoScriptJson_Throws()
    {
        var registry = CreateRegistry();
        var planner = new RegistrationToolExecutionPlanner();

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
        var planner = new RegistrationToolExecutionPlanner();

        var act = () => planner.BuildPlan(
            actionKey: null,
            scriptJson: """{"module":"test_registration","tool":"run","params":{}}""",
            maxEnvelopeBytes: 10,
            moduleRegistry: registry);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("ScriptJson exceeds maximum envelope size (10 bytes).");
    }

    [Test]
    public void CreateEnvelopeJson_SerializesStandardEnvelope()
    {
        var planner = new RegistrationToolExecutionPlanner();

        var json = planner.CreateEnvelopeJson(
            "test_registration",
            "run",
            """{"value":5}""");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("module").GetString().Should().Be("test_registration");
        doc.RootElement.GetProperty("tool").GetString().Should().Be("run");
        doc.RootElement.GetProperty("params").GetProperty("value").GetInt32().Should().Be(5);
    }

    private static RegistrationCatalog CreateRegistry()
    {
        var registry = new RegistrationCatalog();
        registry.Register(new TestRegistration());
        return registry;
    }

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed class TestRegistration : ISharpClawCoreRegistration
    {
        public string Id => "test_registration";
        public string DisplayName => "Test Module";
        public string ToolPrefix => "test";

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public IReadOnlyList<RegistrationToolDefinition> GetToolDefinitions() =>
        [
            new(
                "run",
                "Run",
                Json("""{"type":"object"}"""),
                new RegistrationToolPermission(
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
