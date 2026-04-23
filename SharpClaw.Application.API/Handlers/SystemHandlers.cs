using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.API.Routing;
using SharpClaw.Application.Services;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Infrastructure.Persistence.JSON;
using SharpClaw.Utils.Instances;

namespace SharpClaw.Application.API.Handlers;

[RouteGroup("/system")]
public static class SystemHandlers
{
    /// <summary>
    /// Purges the server-side Data and Environment directories, performing
    /// a full factory reset of all persisted backend state. Requires admin.
    /// </summary>
    [MapPost("/factory-reset")]
    public static async Task<IResult> FactoryReset(
        JsonFileOptions jsonFileOptions,
        SharpClawInstancePaths instancePaths,
        SessionService session,
        SharpClawDbContext db)
    {
        if (session.UserId is not { } userId)
            return Results.Unauthorized();

        var caller = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (caller is null || !caller.IsUserAdmin)
            return Results.Json(new { error = "Only user admins can perform a factory reset." },
                statusCode: StatusCodes.Status403Forbidden);
        var errors = new List<string>();

        DeleteDirectory(jsonFileOptions.DataDirectory, "Data", errors);
        DeleteDirectory(instancePaths.SecretsDirectory, "Secrets", errors);
        DeleteDirectory(instancePaths.RuntimeDirectory, "Runtime", errors);
        DeleteDirectory(instancePaths.LogsDirectory, "Logs", errors);
        DeleteDirectory(instancePaths.ConfigDirectory, "Config", errors);
        DeleteFile(instancePaths.ManifestPath, "Instance manifest", errors);
        DeleteFile(instancePaths.DiscoveryEntryPath, "Discovery entry", errors);

        return errors.Count == 0
            ? Results.Ok(new { success = true })
            : Results.Ok(new { success = false, errors });
    }

    private static void DeleteDirectory(string path, string label, List<string> errors)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            errors.Add($"{label}: {ex.Message}");
        }
    }

    private static void DeleteFile(string path, string label, List<string> errors)
    {
        if (!File.Exists(path))
            return;

        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            errors.Add($"{label}: {ex.Message}");
        }
    }
}
