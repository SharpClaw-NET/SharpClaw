using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Modules;
using SharpClaw.Core.Permissions;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class PermissionDelegatePlannerTests
{
    [Test]
    public void BuildPlan_WhenDelegateMapsToGlobalFlag_ReturnsGlobalFlagPlan()
    {
        var registry = CreateRegistry();

        var plan = PermissionDelegatePlanner.BuildPlan(
            "CanAuditAsync",
            resourceId: null,
            registry);

        plan.Kind.Should().Be(PermissionDelegatePlanKind.GlobalFlag);
        plan.FlagKey.Should().Be("CanAudit");
        plan.ResourceType.Should().BeNull();
    }

    [Test]
    public void BuildPlan_WhenDelegateMapsToResource_ReturnsResourcePlan()
    {
        var registry = CreateRegistry();
        var resourceId = Guid.NewGuid();

        var plan = PermissionDelegatePlanner.BuildPlan(
            "UseDocumentAsync",
            resourceId,
            registry);

        plan.Kind.Should().Be(PermissionDelegatePlanKind.ResourceAccess);
        plan.ResourceType.Should().Be("document");
        plan.ResourceId.Should().Be(resourceId);
        plan.FlagKey.Should().BeNull();
    }

    [Test]
    public void BuildPlan_WhenDelegateIsUnknown_ReturnsUnrecognizedPlan()
    {
        var registry = CreateRegistry();

        var plan = PermissionDelegatePlanner.BuildPlan(
            "MissingAsync",
            resourceId: null,
            registry);

        plan.Kind.Should().Be(PermissionDelegatePlanKind.Unrecognized);
        PermissionDelegatePlanner.HasGrant(
            PermissionSetSnapshot.Empty,
            plan).Should().BeFalse();
    }

    [Test]
    public void HasGrant_WhenResourcePlanHasNoResourceId_MatchesWildcardGrant()
    {
        var registry = CreateRegistry();
        var plan = PermissionDelegatePlanner.BuildPlan(
            "UseDocumentAsync",
            resourceId: null,
            registry);
        var permissionSet = new PermissionSetSnapshot(
            GlobalFlags: [],
            ResourceAccesses:
            [
                new ResourcePermissionGrant(
                    "document",
                    WellKnownIds.AllResources,
                    PermissionClearance.Independent)
            ],
            ClearanceUserWhitelist: new HashSet<Guid>(),
            ClearanceAgentWhitelist: new HashSet<Guid>());

        PermissionDelegatePlanner.HasGrant(permissionSet, plan).Should().BeTrue();
    }

    private static ModuleRegistry CreateRegistry()
    {
        var registry = new ModuleRegistry();
        registry.Register(new PermissionDelegateModule());
        return registry;
    }

    private sealed class PermissionDelegateModule : ISharpClawCoreModule
    {
        private static readonly JsonElement EmptySchema =
            JsonDocument.Parse("{}").RootElement.Clone();

        public string Id => "permission_delegate";
        public string DisplayName => "Permission Delegate";
        public string ToolPrefix => "permdelegate";

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() =>
        [
            new(
                "audit",
                "Audit",
                EmptySchema,
                new ModuleToolPermission(
                    IsPerResource: false,
                    Check: null,
                    DelegateTo: "CanAuditAsync"))
        ];

        public IReadOnlyList<ModuleResourceTypeDescriptor> GetResourceTypeDescriptors() =>
        [
            new(
                "document",
                "Document",
                "UseDocumentAsync",
                (_, _) => Task.FromResult(new List<Guid>()))
        ];

        public IReadOnlyList<ModuleGlobalFlagDescriptor> GetGlobalFlagDescriptors() =>
        [
            new(
                "CanAudit",
                "Can Audit",
                "Allows audit operations.",
                "CanAuditAsync")
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
