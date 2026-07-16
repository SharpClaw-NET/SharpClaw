using Microsoft.AspNetCore.Http;
using SharpClaw.Runtime.Host.Routing;
using SharpClaw.Runtime.BLL.Services;

namespace SharpClaw.Runtime.Host.Handlers;

[RouteGroup("/env")]
public static class EnvHandlers
{
    /// <summary>
    /// Returns whether the current user is authorised to edit the Core .env.
    /// </summary>
    [MapGet("/core/auth")]
    public static async Task<IResult> CheckAuth(EnvFileService svc)
    {
        var authorised = await svc.IsAuthorisedAsync();
        return Results.Ok(new { authorised });
    }

    /// <summary>
    /// Returns the raw content of the Core .env file.
    /// Requires admin (or AllowNonAdmin override).
    /// </summary>
    [MapGet("/core")]
    public static async Task<IResult> Read(EnvFileService svc)
    {
        try
        {
            var content = await svc.ReadAsync();
            return Results.Ok(new { content });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
    }

    /// <summary>
    /// Writes the raw content to the Core .env file.
    /// Requires admin (or AllowNonAdmin override).
    /// </summary>
    [MapPut("/core")]
    public static async Task<IResult> Write(EnvWriteRequest request, EnvFileService svc)
    {
        try
        {
            await svc.WriteAsync(request.Content);
            return Results.Ok(new { saved = true });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
    }
}

public sealed record EnvWriteRequest(string Content);
