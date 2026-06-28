using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.Roles;
using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Enums;
using SharpClaw.Core.Permissions;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class RolePermissionAdministrationEngineTests
{
    private readonly RolePermissionAdministrationEngine _engine = new();

    [Test]
    public void ValidateRequestedGrants_WhenCallerLacksGlobalFlag_Throws()
    {
        var request = new SetRolePermissionsRequest(
            GlobalFlags: new Dictionary<string, PermissionClearance>
            {
                ["CanClickDesktop"] = PermissionClearance.Independent
            });

        var act = () => _engine.ValidateRequestedGrants(
            request,
            new PermissionSetDB());

        act.Should().Throw<UnauthorizedAccessException>()
            .WithMessage("Cannot grant CanClickDesktop*");
    }

    [Test]
    public void ValidateRequestedGrants_WhenCallerHasWildcard_AllowsResourceGrant()
    {
        var caller = new PermissionSetDB();
        caller.ResourceAccesses.Add(new ResourceAccessDB
        {
            ResourceType = "Module.Resource",
            ResourceId = WellKnownIds.AllResources,
            Clearance = PermissionClearance.Independent
        });

        var request = new SetRolePermissionsRequest(
            ResourceGrants: new Dictionary<string, IReadOnlyList<ResourceGrant>>
            {
                ["Module.Resource"] =
                [
                    new ResourceGrant(Guid.NewGuid(), PermissionClearance.Independent)
                ]
            });

        var act = () => _engine.ValidateRequestedGrants(request, caller);

        act.Should().NotThrow();
    }

    [Test]
    public void ReconcilePermissionSet_UpdatesAddsAndRemovesGlobalFlags()
    {
        var target = new PermissionSetDB();
        target.GlobalFlags.Add(new GlobalFlagDB
        {
            FlagKey = "remove",
            Clearance = PermissionClearance.Independent
        });
        target.GlobalFlags.Add(new GlobalFlagDB
        {
            FlagKey = "update",
            Clearance = PermissionClearance.Unset
        });

        var request = new SetRolePermissionsRequest(
            GlobalFlags: new Dictionary<string, PermissionClearance>
            {
                ["update"] = PermissionClearance.Independent,
                ["add"] = PermissionClearance.Independent
            });

        _engine.ReconcilePermissionSet(target, request);

        target.GlobalFlags.Select(flag => flag.FlagKey)
            .Should().BeEquivalentTo(["update", "add"]);
        target.GlobalFlags.Single(flag => flag.FlagKey == "update")
            .Clearance.Should().Be(PermissionClearance.Independent);
    }

    [Test]
    public void ReconcilePermissionSet_WhenWildcardGrantIsOmitted_Throws()
    {
        var target = new PermissionSetDB();
        target.ResourceAccesses.Add(new ResourceAccessDB
        {
            ResourceType = "Module.Resource",
            ResourceId = WellKnownIds.AllResources,
            Clearance = PermissionClearance.Independent
        });

        var request = new SetRolePermissionsRequest(
            ResourceGrants: new Dictionary<string, IReadOnlyList<ResourceGrant>>());

        var act = () => _engine.ReconcilePermissionSet(target, request);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Wildcard grant for 'Module.Resource' is immutable and cannot be removed.");
    }

    [Test]
    public void ReconcilePermissionSet_WhenWildcardGrantIsIncluded_UpdatesClearance()
    {
        var target = new PermissionSetDB();
        target.ResourceAccesses.Add(new ResourceAccessDB
        {
            ResourceType = "Module.Resource",
            ResourceId = WellKnownIds.AllResources,
            Clearance = PermissionClearance.Unset
        });

        var request = new SetRolePermissionsRequest(
            ResourceGrants: new Dictionary<string, IReadOnlyList<ResourceGrant>>
            {
                ["Module.Resource"] =
                [
                    new ResourceGrant(
                        WellKnownIds.AllResources,
                        PermissionClearance.Independent)
                ]
            });

        _engine.ReconcilePermissionSet(target, request);

        target.ResourceAccesses.Single().Clearance
            .Should().Be(PermissionClearance.Independent);
    }

    [Test]
    public void EnsureRoleNameAvailable_WhenDuplicateAfterTrimAndCase_Throws()
    {
        var act = () => _engine.EnsureRoleNameAvailable(" Admin ", ["admin"]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("A role named ' Admin ' already exists.");
    }
}
