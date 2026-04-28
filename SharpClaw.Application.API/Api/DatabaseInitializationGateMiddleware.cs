using Microsoft.AspNetCore.Http;
using SharpClaw.Application.API;

namespace SharpClaw.Application.API.Api;

/// <summary>
/// Prevents HTTP request execution until database cold-storage initialization is complete.
/// </summary>
public sealed class DatabaseInitializationGateMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, DatabaseInitializationGate gate)
    {
        if (!gate.IsInitialized)
            await gate.WaitAsync(context.RequestAborted);

        await next(context);
    }
}
