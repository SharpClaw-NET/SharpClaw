using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SharpClaw.Contracts.DTOs.SearchEngines;
using SharpClaw.Modules.WebAccess.Services;

namespace SharpClaw.Modules.WebAccess.Handlers;

/// <summary>
/// Registers minimal-API REST endpoints for Search Engine resource CRUD.
/// Routes are placed under <c>/searchengines</c> to maintain backward
/// compatibility with the original endpoint paths.
/// </summary>
public static class SearchEngineEndpoints
{
    /// <summary>Maps the Search Engine CRUD endpoints.</summary>
    public static IEndpointRouteBuilder MapSearchEngineEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/searchengines");

        group.MapPost("/", async (CreateSearchEngineRequest request, SearchEngineService svc)
            => Results.Ok(await svc.CreateAsync(request)));

        group.MapGet("/", async (SearchEngineService svc)
            => Results.Ok(await svc.ListAsync()));

        group.MapGet("/{id:guid}", async (Guid id, SearchEngineService svc) =>
        {
            var engine = await svc.GetByIdAsync(id);
            return engine is not null ? Results.Ok(engine) : Results.NotFound();
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateSearchEngineRequest request, SearchEngineService svc) =>
        {
            var engine = await svc.UpdateAsync(id, request);
            return engine is not null ? Results.Ok(engine) : Results.NotFound();
        });

        group.MapDelete("/{id:guid}", async (Guid id, SearchEngineService svc)
            => await svc.DeleteAsync(id) ? Results.NoContent() : Results.NotFound());

        return routes;
    }
}
