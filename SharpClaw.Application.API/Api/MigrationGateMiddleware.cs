using Microsoft.AspNetCore.Http;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.API.Api;

/// <summary>
/// Wraps every incoming request in the <see cref="MigrationGate"/>.
/// While a migration is active, new requests await until the migration completes.
/// Admin DB endpoints (<c>/admin/db</c>) bypass the gate so they can trigger
/// and monitor migrations without deadlocking.
/// </summary>
public sealed class MigrationGateMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, MigrationGate gate)
    {
        // Admin DB endpoints bypass the gate (monitoring + trigger).
        if (context.Request.Path.StartsWithSegments("/admin/db"))
        {
            await next(context);
            return;
        }

        using var handle = await gate.EnterRequestAsync(context.RequestAborted);
        await next(context);
    }
}
