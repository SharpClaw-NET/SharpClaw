using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Modules;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class RegistrationToolPermissionPlannerTests
{
    [Test]
    public void BuildPlan_WhenActionKeyIsMissing_Denies()
    {
        var planner = new RegistrationToolPermissionPlanner();

        var plan = planner.BuildPlan(null, null, new RegistrationCatalog());

        plan.Kind.Should().Be(RegistrationToolPermissionPlanKind.Denied);
        plan.DenialReason.Should().Be(
            RegistrationToolPermissionDenialReason.MissingActionKey);
        plan.DeniedResult.Should().NotBeNull();
        plan.DeniedResult!.Reason.Should().Be(
            "Module action requires an ActionKey to resolve permissions.");
    }

    [Test]
    public void BuildPlan_WhenToolIsNotRegistered_Denies()
    {
        var planner = new RegistrationToolPermissionPlanner();

        var plan = planner.BuildPlan("missing", null, new RegistrationCatalog());

        plan.Kind.Should().Be(RegistrationToolPermissionPlanKind.Denied);
        plan.DenialReason.Should().Be(
            RegistrationToolPermissionDenialReason.ToolNotRegistered);
        plan.DeniedResult!.Reason.Should().Be(
            "No module registered for tool 'missing'.");
    }

    [Test]
    public void BuildPlan_WhenToolHasNoPermissionDescriptor_Denies()
    {
        var registry = CreateRegistry(null!);
        var planner = new RegistrationToolPermissionPlanner();

        var plan = planner.BuildPlan("run", null, registry);

        plan.Kind.Should().Be(RegistrationToolPermissionPlanKind.Denied);
        plan.DenialReason.Should().Be(
            RegistrationToolPermissionDenialReason.MissingPermissionDescriptor);
        plan.SourceId.Should().Be("test_registration");
        plan.ToolName.Should().Be("run");
        plan.DeniedResult!.Reason.Should().Be(
            "Module tool 'run' has no permission descriptor.");
    }

    [Test]
    public void BuildPlan_WhenPerResourceToolHasNoResourceId_Denies()
    {
        var registry = CreateRegistry(new RegistrationToolPermission(
            IsPerResource: true,
            Check: DirectApprove));
        var planner = new RegistrationToolPermissionPlanner();

        var plan = planner.BuildPlan("run", null, registry);

        plan.Kind.Should().Be(RegistrationToolPermissionPlanKind.Denied);
        plan.DenialReason.Should().Be(
            RegistrationToolPermissionDenialReason.MissingResourceId);
        plan.DeniedResult!.Reason.Should().Be(
            "ResourceId is required for module tool 'run'.");
    }

    [Test]
    public async Task BuildPlan_WhenDirectCheckAndDelegateAreBothSet_ChoosesDirectCheck()
    {
        var registry = CreateRegistry(new RegistrationToolPermission(
            IsPerResource: false,
            Check: DirectApprove,
            DelegateTo: "DelegateShouldNotWin"));
        var planner = new RegistrationToolPermissionPlanner();

        var plan = planner.BuildPlan("run", null, registry);

        plan.Kind.Should().Be(RegistrationToolPermissionPlanKind.DirectCheck);
        plan.DirectCheck.Should().NotBeNull();
        plan.DelegateTo.Should().BeNull();
        var result = await plan.DirectCheck!(
            Guid.NewGuid(),
            null,
            new ActionCaller(),
            CancellationToken.None);
        result.Verdict.Should().Be(ClearanceVerdict.Approved);
    }

    [Test]
    public void BuildPlan_WhenDelegateIsConfigured_ChoosesHostDelegate()
    {
        var registry = CreateRegistry(new RegistrationToolPermission(
            IsPerResource: false,
            Check: null,
            DelegateTo: "CanRun"));
        var planner = new RegistrationToolPermissionPlanner();

        var plan = planner.BuildPlan("run", null, registry);

        plan.Kind.Should().Be(RegistrationToolPermissionPlanKind.DelegateToHost);
        plan.DirectCheck.Should().BeNull();
        plan.DelegateTo.Should().Be("CanRun");
        plan.CreateUnrecognizedDelegateDeniedResult().Reason.Should().Be(
            "Module tool 'run' delegates to 'CanRun' which is not a recognised permission check method.");
    }

    [Test]
    public void BuildPlan_WhenNoCheckOrDelegateIsConfigured_Denies()
    {
        var registry = CreateRegistry(new RegistrationToolPermission(
            IsPerResource: false,
            Check: null,
            DelegateTo: null));
        var planner = new RegistrationToolPermissionPlanner();

        var plan = planner.BuildPlan("run", null, registry);

        plan.Kind.Should().Be(RegistrationToolPermissionPlanKind.Denied);
        plan.DenialReason.Should().Be(
            RegistrationToolPermissionDenialReason.NoPermissionCheckConfigured);
        plan.DeniedResult!.Reason.Should().Be(
            "Module tool 'run' has no permission check configured.");
    }

    private static RegistrationCatalog CreateRegistry(RegistrationToolPermission permission)
    {
        var registry = new RegistrationCatalog();
        registry.Register(new TestRegistration(permission));
        return registry;
    }

    private static Task<AgentActionResult> DirectApprove(
        Guid agentId,
        Guid? resourceId,
        ActionCaller caller,
        CancellationToken ct)
    {
        return Task.FromResult(AgentActionResult.Approve(
            "ok",
            PermissionClearance.ApprovedByWhitelistedUser));
    }

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed class TestRegistration(RegistrationToolPermission permission)
        : ISharpClawCoreRegistration
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
