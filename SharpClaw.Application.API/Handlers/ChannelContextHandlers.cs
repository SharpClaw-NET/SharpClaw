using Microsoft.AspNetCore.Http;
using SharpClaw.Application.API.Routing;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.Contexts;
using SharpClaw.Contracts.DTOs.DefaultResources;

namespace SharpClaw.Application.API.Handlers;

[RouteGroup("/channel-contexts")]
public static class ChannelContextHandlers
{
    [MapPost]
    public static async Task<IResult> Create(CreateContextRequest request, ContextService svc)
        => Results.Ok(await svc.CreateAsync(request));

    [MapGet]
    public static async Task<IResult> List(ContextService svc, Guid? agentId = null)
        => Results.Ok(await svc.ListAsync(agentId));

    [MapGet("/{id:guid}")]
    public static async Task<IResult> GetById(Guid id, ContextService svc)
    {
        var context = await svc.GetByIdAsync(id);
        return context is not null ? Results.Ok(context) : Results.NotFound();
    }

    [MapPut("/{id:guid}")]
    public static async Task<IResult> Update(Guid id, UpdateContextRequest request, ContextService svc)
    {
        var context = await svc.UpdateAsync(id, request);
        return context is not null ? Results.Ok(context) : Results.NotFound();
    }

    [MapDelete("/{id:guid}")]
    public static async Task<IResult> Delete(Guid id, ContextService svc)
        => await svc.DeleteAsync(id) ? Results.NoContent() : Results.NotFound();

    [MapGet("/{id:guid}/defaults")]
    public static async Task<IResult> GetDefaults(Guid id, DefaultResourceSetService svc)
    {
        var result = await svc.GetForContextAsync(id);
        return result is not null ? Results.Ok(result) : Results.NotFound();
    }

    [MapPut("/{id:guid}/defaults")]
    public static async Task<IResult> SetDefaults(
        Guid id, SetDefaultResourcesRequest request, DefaultResourceSetService svc)
    {
        var result = await svc.SetForContextAsync(id, request);
        return result is not null ? Results.Ok(result) : Results.NotFound();
    }
}
