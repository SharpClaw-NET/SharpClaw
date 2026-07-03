using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.API.Routing;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.API.Handlers;

[RouteGroup("/system")]
public static class HealthHandlers
{
    /// <summary>
    /// Returns the persistence health status. No authentication required — suitable for
    /// monitoring probes. Responds 200 (Healthy), 207 (Degraded), or 503 (Unhealthy).
    /// </summary>
    [MapGet("/health")]
    public static async Task<IResult> GetHealth(
        SharpClawDbContext db,
        CancellationToken ct)
    {
        var canConnect = await db.Database.CanConnectAsync(ct);
        var status = canConnect ? "Healthy" : "Unhealthy";

        var body = new
        {
            status,
            checks = new[]
            {
                new
                {
                    name = "Database",
                    status,
                    description = canConnect
                        ? "Configured EF provider is reachable."
                        : "Configured EF provider is not reachable."
                }
            }
        };

        return canConnect
            ? Results.Ok(body)
            : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
