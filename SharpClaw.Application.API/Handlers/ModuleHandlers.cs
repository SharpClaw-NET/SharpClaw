using Microsoft.AspNetCore.Http;

using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.API.Routing;
using SharpClaw.Application.Services;

namespace SharpClaw.Application.API.Handlers;

[RouteGroup("/modules")]
public static class ModuleHandlers
{
    /// <summary>List all bundled modules with their enabled/disabled state.</summary>
    [MapGet]
    public static async Task<IResult> List(ModuleService svc)
    {
        var modules = await svc.ListAsync();
        return Results.Ok(modules);
    }

    /// <summary>Get state for a single module.</summary>
    [MapGet("/{moduleId}")]
    public static async Task<IResult> Get(string moduleId, ModuleService svc)
    {
        var state = await svc.GetStateAsync(moduleId);
        return state is not null
            ? Results.Ok(state)
            : Results.NotFound(new { error = $"Unknown module: {moduleId}" });
    }

    /// <summary>Enable a bundled module. Registers it, runs initialization, persists state.</summary>
    [MapPost("/{moduleId}/enable")]
    public static async Task<IResult> Enable(string moduleId, ModuleService svc, ModuleLoader moduleLoader)
    {
        try
        {
            var result = await svc.EnableAsync(moduleId, moduleLoader.RootServices);
            return Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <summary>Disable a bundled module. Shuts it down, unregisters, persists state.</summary>
    [MapPost("/{moduleId}/disable")]
    public static async Task<IResult> Disable(string moduleId, ModuleService svc)
    {
        try
        {
            var result = await svc.DisableAsync(moduleId);
            return Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
