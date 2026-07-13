using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.Roles;
using SharpClaw.Contracts.Entities.Core;
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

    [Test]
    public void CreateRole_TrimsNameAndAttachesEmptyPermissionSet()
    {
        var role = _engine.CreateRole(" Operators ");

        role.Name.Should().Be("Operators");
        role.PermissionSet.Should().NotBeNull();
        role.PermissionSet!.GlobalFlags.Should().BeEmpty();
        role.PermissionSet.ResourceAccesses.Should().BeEmpty();
    }

    [Test]
    public void CreatePermissionSetForRole_AttachesNewSet()
    {
        var role = new RoleDB { Name = "Operators" };

        var permissionSet = _engine.CreatePermissionSetForRole(role);

        role.PermissionSet.Should().BeSameAs(permissionSet);
    }

    [Test]
    public void ToPermissionsResponse_GroupsResourceGrants()
    {
        var role = new RoleDB
        {
            Id = Guid.NewGuid(),
            Name = "Operators"
        };
        var resourceId = Guid.NewGuid();
        var permissionSet = new PermissionSetDB();
        permissionSet.GlobalFlags.Add(new GlobalFlagDB
        {
            FlagKey = "CanUseShell",
            Clearance = PermissionClearance.Independent
        });
        permissionSet.ResourceAccesses.Add(new ResourceAccessDB
        {
            ResourceType = "Module.Resource",
            ResourceId = resourceId,
            Clearance = PermissionClearance.Independent
        });

        var response = _engine.ToPermissionsResponse(role, permissionSet);

        response.RoleId.Should().Be(role.Id);
        response.RoleName.Should().Be("Operators");
        response.GlobalFlags["CanUseShell"]
            .Should().Be(PermissionClearance.Independent);
        response.ResourceGrants["Module.Resource"].Single()
            .Should().Be(new ResourceGrant(
                resourceId,
                PermissionClearance.Independent));
    }

    [Test]
    public void PlanDeleteRole_DetachesUsersAndReturnsOwnedPermissionRows()
    {
        var roleId = Guid.NewGuid();
        var user = new UserDB
        {
            Username = "marko",
            PasswordHash = [],
            PasswordSalt = [],
            RoleId = roleId
        };
        var permissionSet = new PermissionSetDB();
        var flag = new GlobalFlagDB
        {
            FlagKey = "CanUseShell",
            Clearance = PermissionClearance.Independent
        };
        var access = new ResourceAccessDB
        {
            ResourceType = "Module.Resource",
            ResourceId = Guid.NewGuid(),
            Clearance = PermissionClearance.Independent
        };
        permissionSet.GlobalFlags.Add(flag);
        permissionSet.ResourceAccesses.Add(access);
        var role = new RoleDB
        {
            Id = roleId,
            Name = "Operators",
            PermissionSet = permissionSet,
            Users = [user]
        };

        var plan = _engine.PlanDeleteRole(role);

        user.RoleId.Should().BeNull();
        plan.Role.Should().BeSameAs(role);
        plan.PermissionSet.Should().BeSameAs(permissionSet);
        plan.GlobalFlags.Should().ContainSingle().Which.Should().BeSameAs(flag);
        plan.ResourceAccesses.Should().ContainSingle().Which.Should().BeSameAs(access);
    }
}
