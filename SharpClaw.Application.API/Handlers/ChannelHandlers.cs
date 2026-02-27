using Microsoft.AspNetCore.Http;
using SharpClaw.Application.API.Routing;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.Channels;
using SharpClaw.Contracts.DTOs.DefaultResources;

namespace SharpClaw.Application.API.Handlers;

[RouteGroup("/channels")]
public static class ChannelHandlers
{
    [MapPost]
    public static async Task<IResult> Create(CreateChannelRequest request, ChannelService svc)
        => Results.Ok(await svc.CreateAsync(request));

    [MapGet]
    public static async Task<IResult> List(ChannelService svc, Guid? agentId = null)
        => Results.Ok(await svc.ListAsync(agentId));

    [MapGet("/{id:guid}")]
    public static async Task<IResult> GetById(Guid id, ChannelService svc)
    {
        var channel = await svc.GetByIdAsync(id);
        return channel is not null ? Results.Ok(channel) : Results.NotFound();
    }

    [MapPut("/{id:guid}")]
    public static async Task<IResult> Update(Guid id, UpdateChannelRequest request, ChannelService svc)
    {
        var channel = await svc.UpdateAsync(id, request);
        return channel is not null ? Results.Ok(channel) : Results.NotFound();
    }

    [MapDelete("/{id:guid}")]
    public static async Task<IResult> Delete(Guid id, ChannelService svc)
        => await svc.DeleteAsync(id) ? Results.NoContent() : Results.NotFound();

    [MapGet("/{id:guid}/defaults")]
    public static async Task<IResult> GetDefaults(Guid id, DefaultResourceSetService svc)
    {
        var result = await svc.GetForChannelAsync(id);
        return result is not null ? Results.Ok(result) : Results.NotFound();
    }

    [MapPut("/{id:guid}/defaults")]
    public static async Task<IResult> SetDefaults(
        Guid id, SetDefaultResourcesRequest request, DefaultResourceSetService svc)
    {
        var result = await svc.SetForChannelAsync(id, request);
        return result is not null ? Results.Ok(result) : Results.NotFound();
    }
}
