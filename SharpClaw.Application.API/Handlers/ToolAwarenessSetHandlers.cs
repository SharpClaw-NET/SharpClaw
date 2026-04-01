using Microsoft.AspNetCore.Http;
using SharpClaw.Application.API.Routing;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.Tools;

namespace SharpClaw.Application.API.Handlers;

[RouteGroup("/tool-awareness-sets")]
public static class ToolAwarenessSetHandlers
{
    [MapPost]
    public static async Task<IResult> Create(
        CreateToolAwarenessSetRequest request, ToolAwarenessSetService svc)
        => Results.Ok(await svc.CreateAsync(request));

    [MapGet]
    public static async Task<IResult> List(ToolAwarenessSetService svc)
        => Results.Ok(await svc.ListAsync());

    [MapGet("/{id:guid}")]
    public static async Task<IResult> GetById(Guid id, ToolAwarenessSetService svc)
    {
        var set = await svc.GetByIdAsync(id);
        return set is not null ? Results.Ok(set) : Results.NotFound();
    }

    [MapPut("/{id:guid}")]
    public static async Task<IResult> Update(
        Guid id, UpdateToolAwarenessSetRequest request, ToolAwarenessSetService svc)
    {
        var set = await svc.UpdateAsync(id, request);
        return set is not null ? Results.Ok(set) : Results.NotFound();
    }

    [MapDelete("/{id:guid}")]
    public static async Task<IResult> Delete(Guid id, ToolAwarenessSetService svc)
        => await svc.DeleteAsync(id) ? Results.NoContent() : Results.NotFound();
}
