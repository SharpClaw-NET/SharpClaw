using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.BLL.Services;

internal static class UserRoleAccess
{
    private const string RoleIdProperty = "RoleId";

    public static Guid? GetRoleId(SharpClawDbContext db, UserDB user) =>
        db.Entry(user).Property<Guid?>(RoleIdProperty).CurrentValue;

    public static void SetRoleId(
        SharpClawDbContext db,
        UserDB user,
        Guid? roleId) =>
        db.Entry(user).Property<Guid?>(RoleIdProperty).CurrentValue = roleId;

    public static async Task<RoleDB?> LoadRoleAsync(
        SharpClawDbContext db,
        UserDB user,
        CancellationToken ct = default)
    {
        var roleId = GetRoleId(db, user);
        return roleId is { } id
            ? await db.Roles.FirstOrDefaultAsync(role => role.Id == id, ct)
            : null;
    }
}
