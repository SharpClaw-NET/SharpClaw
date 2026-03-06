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

    // ── Default agent ─────────────────────────────────────────────

    [MapPut("/{id:guid}/agent")]
    public static async Task<IResult> SetAgent(
        Guid id, SetChannelAgentRequest request, ChannelService svc)
    {
        var result = await svc.SetAgentAsync(id, request.AgentId);
        return result is not null ? Results.Ok(result) : Results.NotFound();
    }

    // ── Allowed agents ────────────────────────────────────────────

    [MapGet("/{id:guid}/agents")]
    public static async Task<IResult> ListAllowedAgents(Guid id, ChannelService svc)
    {
        var result = await svc.ListAllowedAgentsAsync(id);
        return result is not null ? Results.Ok(result) : Results.NotFound();
    }

    [MapPost("/{id:guid}/agents")]
    public static async Task<IResult> AddAllowedAgent(
        Guid id, AddAllowedAgentRequest request, ChannelService svc)
    {
        var result = await svc.AddAllowedAgentAsync(id, request.AgentId);
        return result is not null ? Results.Ok(result) : Results.NotFound();
    }

    [MapDelete("/{id:guid}/agents/{agentId:guid}")]
    public static async Task<IResult> RemoveAllowedAgent(
        Guid id, Guid agentId, ChannelService svc)
    {
        var result = await svc.RemoveAllowedAgentAsync(id, agentId);
        return result is not null ? Results.Ok(result) : Results.NotFound();
    }

    // ── Default resources (bulk) ──────────────────────────────────

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

    // ── Default resources (per-key) ───────────────────────────────

    [MapPut("/{id:guid}/defaults/{key}")]
    public static async Task<IResult> SetDefaultByKey(
        Guid id, string key, SetDefaultResourceByKeyRequest request,
        DefaultResourceSetService svc)
    {
        if (!DefaultResourceSetService.IsValidKey(key))
            return Results.BadRequest($"Unknown default resource key: {key}");

        var result = await svc.SetKeyForChannelAsync(id, key, request.ResourceId);
        return result is not null ? Results.Ok(result) : Results.NotFound();
    }

    [MapDelete("/{id:guid}/defaults/{key}")]
    public static async Task<IResult> ClearDefaultByKey(
        Guid id, string key, DefaultResourceSetService svc)
    {
        if (!DefaultResourceSetService.IsValidKey(key))
            return Results.BadRequest($"Unknown default resource key: {key}");

        var result = await svc.ClearKeyForChannelAsync(id, key);
        return result is not null ? Results.Ok(result) : Results.NotFound();
    }
}
