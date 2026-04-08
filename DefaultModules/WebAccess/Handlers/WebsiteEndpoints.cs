using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SharpClaw.Contracts.DTOs.Websites;
using SharpClaw.Modules.WebAccess.Services;

namespace SharpClaw.Modules.WebAccess.Handlers;

/// <summary>
/// Registers minimal-API REST endpoints for Website resource CRUD.
/// Routes are placed under <c>/resources/websites</c>.
/// </summary>
public static class WebsiteEndpoints
{
    /// <summary>Maps the Website CRUD endpoints.</summary>
    public static IEndpointRouteBuilder MapWebsiteEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/resources/websites");

        group.MapPost("/", async (CreateWebsiteRequest request, WebsiteService svc)
            => Results.Ok(await svc.CreateAsync(request)));

        group.MapGet("/", async (WebsiteService svc)
            => Results.Ok(await svc.ListAsync()));

        group.MapGet("/{id:guid}", async (Guid id, WebsiteService svc) =>
        {
            var website = await svc.GetByIdAsync(id);
            return website is not null ? Results.Ok(website) : Results.NotFound();
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateWebsiteRequest request, WebsiteService svc) =>
        {
            var website = await svc.UpdateAsync(id, request);
            return website is not null ? Results.Ok(website) : Results.NotFound();
        });

        group.MapDelete("/{id:guid}", async (Guid id, WebsiteService svc)
            => await svc.DeleteAsync(id) ? Results.NoContent() : Results.NotFound());

        return routes;
    }
}
