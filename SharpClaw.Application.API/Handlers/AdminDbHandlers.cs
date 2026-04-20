using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.API.Routing;
using SharpClaw.Application.Services;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.API.Handlers;

[RouteGroup("/admin/db")]
public static class AdminDbHandlers
{
    /// <summary>
    /// Returns the current migration gate state plus applied/pending migration lists.
    /// Requires an authenticated admin user.
    /// </summary>
    [MapGet("/status")]
    public static async Task<IResult> GetStatus(
        MigrationService svc,
        SessionService session,
        SharpClawDbContext db,
        CancellationToken ct)
    {
        if (!await IsAdminAsync(session, db, ct))
            return Results.Json(new { message = "Admin access required." }, statusCode: 403);

        var status = await svc.GetStatusAsync(ct);
        return Results.Ok(new
        {
            state = status.State.ToString(),
            applied = status.Applied,
            pending = status.Pending
        });
    }

    /// <summary>
    /// Drains in-flight requests and applies all pending EF Core migrations.
    /// Requires an authenticated admin user.
    /// </summary>
    [MapPost("/migrate")]
    public static async Task<IResult> Migrate(
        MigrationService svc,
        SessionService session,
        SharpClawDbContext db,
        CancellationToken ct)
    {
        if (!await IsAdminAsync(session, db, ct))
            return Results.Json(new { message = "Admin access required." }, statusCode: 403);

        var result = await svc.MigrateAsync(ct);

        if (result.AlreadyInProgress)
            return Results.Conflict(new { message = result.Message });

        return Results.Ok(new
        {
            applied = result.Applied,
            migrations = result.Migrations,
            message = result.Message
        });
    }

    private static async Task<bool> IsAdminAsync(
        SessionService session,
        SharpClawDbContext db,
        CancellationToken ct)
    {
        if (session.UserId is not { } userId)
            return false;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        return user is { IsUserAdmin: true };
    }
}
