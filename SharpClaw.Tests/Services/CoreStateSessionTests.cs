using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Enums;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Tests.Services;

[TestFixture]
public sealed class CoreStateSessionTests
{
    [Test]
    public async Task SaveChangesAsync_PersistsNewNeutralOwnedGraph()
    {
        await using var db = CreateDbContext();
        var states = new CoreStateSession(db);
        var permissionSet = new PermissionSetState
        {
            GlobalFlags =
            [
                new GlobalFlagState
                {
                    FlagKey = "CanInspect",
                    Clearance = PermissionClearance.Independent
                }
            ]
        };
        var role = new RoleState
        {
            Name = "operators",
            PermissionSet = permissionSet
        };

        states.Track(role);
        await states.SaveChangesAsync(CancellationToken.None);

        role.Id.Should().NotBeEmpty();
        permissionSet.Id.Should().NotBeEmpty();
        role.PermissionSetId.Should().Be(permissionSet.Id);
        permissionSet.GlobalFlags.Single().PermissionSetId
            .Should().Be(permissionSet.Id);

        db.ChangeTracker.Clear();
        var stored = await db.Roles
            .Include(value => value.PermissionSet)
                .ThenInclude(value => value!.GlobalFlags)
            .SingleAsync();
        stored.PermissionSetId.Should().Be(permissionSet.Id);
        stored.PermissionSet!.GlobalFlags.Single().FlagKey
            .Should().Be("CanInspect");
    }

    [Test]
    public async Task SaveChangesAsync_TranslatesNeutralWhitelistIds()
    {
        await using var db = CreateDbContext();
        var oldUserId = Guid.NewGuid();
        var newUserId = Guid.NewGuid();
        var oldAgentId = Guid.NewGuid();
        var newAgentId = Guid.NewGuid();
        var entity = new PermissionSetDB
        {
            ClearanceUserWhitelist =
            [
                new ClearanceUserWhitelistEntryDB
                {
                    UserId = oldUserId,
                    User = null!
                }
            ],
            ClearanceAgentWhitelist =
            [
                new ClearanceAgentWhitelistEntryDB
                {
                    AgentId = oldAgentId,
                    Agent = null!
                }
            ]
        };
        db.PermissionSets.Add(entity);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        entity = await db.PermissionSets
            .Include(value => value.ClearanceUserWhitelist)
            .Include(value => value.ClearanceAgentWhitelist)
            .SingleAsync();
        var states = new CoreStateSession(db);
        var state = states.Map(entity);

        state.ClearanceUserWhitelist.Should().Equal(oldUserId);
        state.ClearanceAgentWhitelist.Should().Equal(oldAgentId);
        state.ClearanceUserWhitelist = new HashSet<Guid> { newUserId };
        state.ClearanceAgentWhitelist = new HashSet<Guid> { newAgentId };

        await states.SaveChangesAsync(CancellationToken.None);
        db.ChangeTracker.Clear();

        (await db.ClearanceUserWhitelistEntries.SingleAsync()).UserId
            .Should().Be(newUserId);
        (await db.ClearanceAgentWhitelistEntries.SingleAsync()).AgentId
            .Should().Be(newAgentId);
    }

    [Test]
    public async Task SaveChangesAsync_UsesChangedForeignKeyOverStaleReference()
    {
        await using var db = CreateDbContext();
        var originalRole = new RoleDB { Name = "original" };
        var replacementRole = new RoleDB { Name = "replacement" };
        var user = new UserDB
        {
            Username = "operator",
            PasswordHash = [1],
            PasswordSalt = [2],
        };
        db.AddRange(originalRole, replacementRole, user);
        db.Entry(user).Property<Guid?>("RoleId").CurrentValue = originalRole.Id;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        user = await db.Users.SingleAsync();
        var states = new CoreStateSession(db);
        var state = states.Map(user);
        state.RoleId = replacementRole.Id;

        await states.SaveChangesAsync(CancellationToken.None);
        db.ChangeTracker.Clear();

        var stored = await db.Users.SingleAsync();
        db.Entry(stored).Property<Guid?>("RoleId").CurrentValue
            .Should().Be(replacementRole.Id);
    }

    private static SharpClawDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<SharpClawDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new SharpClawDbContext(options);
    }
}
