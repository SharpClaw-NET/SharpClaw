using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Contracts.DTOs.Roles;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Enums;
using SharpClaw.Tests.TestHarness;

namespace SharpClaw.Tests.Services;

[TestFixture]
public sealed class RoleServiceTests
{
    [Test]
    public async Task CreateAsync_CreatesRoleWithPermissionSet()
    {
        await using var host = ChatHarnessHost.Create();
        var roles = host.Services.GetRequiredService<RoleService>();

        var response = await roles.CreateAsync(" Operators ");

        response.Name.Should().Be("Operators");
        response.PermissionSetId.Should().NotBeNull();

        var stored = await host.Db.Roles
            .Include(role => role.PermissionSet)
            .SingleAsync(role => role.Id == response.Id);

        stored.PermissionSetId.Should().Be(response.PermissionSetId);
        stored.PermissionSet.Should().NotBeNull();
    }

    [Test]
    public async Task SetPermissionsAsync_WhenRoleHasNoPermissionSet_AttachesOne()
    {
        await using var host = ChatHarnessHost.Create();
        var roles = host.Services.GetRequiredService<RoleService>();
        var role = new RoleDB { Name = "Operators" };
        var caller = new UserDB
        {
            Username = "admin",
            PasswordHash = [],
            PasswordSalt = [],
            IsUserAdmin = true
        };
        host.Db.Roles.Add(role);
        host.Db.Users.Add(caller);
        await host.Db.SaveChangesAsync();

        var response = await roles.SetPermissionsAsync(
            role.Id,
            new SetRolePermissionsRequest(
                GlobalFlags: new Dictionary<string, PermissionClearance>
                {
                    ["CanUseShell"] = PermissionClearance.Independent
                }),
            caller.Id);

        response.Should().NotBeNull();
        response!.GlobalFlags["CanUseShell"]
            .Should().Be(PermissionClearance.Independent);

        var stored = await host.Db.Roles
            .Include(storedRole => storedRole.PermissionSet!)
                .ThenInclude(permissionSet => permissionSet.GlobalFlags)
            .SingleAsync(storedRole => storedRole.Id == role.Id);

        stored.PermissionSetId.Should().NotBeNull();
        stored.PermissionSet!.GlobalFlags.Single().FlagKey
            .Should().Be("CanUseShell");
    }

    [Test]
    public async Task DeleteAsync_DetachesUsersAndRemovesOwnedPermissionRows()
    {
        await using var host = ChatHarnessHost.Create();
        var roles = host.Services.GetRequiredService<RoleService>();
        var roleId = Guid.NewGuid();
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
            PermissionSet = permissionSet
        };
        var user = new UserDB
        {
            Username = "operator",
            PasswordHash = [],
            PasswordSalt = [],
            RoleId = roleId,
            Role = role
        };
        host.Db.Roles.Add(role);
        host.Db.Users.Add(user);
        await host.Db.SaveChangesAsync();
        var permissionSetId = permissionSet.Id;
        var flagId = flag.Id;
        var accessId = access.Id;

        var deleted = await roles.DeleteAsync(roleId);

        deleted.Should().BeTrue();
        (await host.Db.Roles.FindAsync(roleId)).Should().BeNull();
        (await host.Db.PermissionSets.FindAsync(permissionSetId)).Should().BeNull();
        (await host.Db.GlobalFlags.FindAsync(flagId)).Should().BeNull();
        (await host.Db.ResourceAccesses.FindAsync(accessId)).Should().BeNull();
        (await host.Db.Users.SingleAsync(stored => stored.Id == user.Id))
            .RoleId.Should().BeNull();
    }
}
