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

    // ── Allowed agents ────────────────────────────────────────────

    [MapGet("/{id:guid}/agents")]
    public static async Task<IResult> ListAllowedAgents(Guid id, ContextService svc)
    {
        var result = await svc.ListAllowedAgentsAsync(id);
        return result is not null ? Results.Ok(result) : Results.NotFound();
    }

    [MapPost("/{id:guid}/agents")]
    public static async Task<IResult> AddAllowedAgent(
        Guid id, AddContextAllowedAgentRequest request, ContextService svc)
    {
        var result = await svc.AddAllowedAgentAsync(id, request.AgentId);
        return result is not null ? Results.Ok(result) : Results.NotFound();
    }

    [MapDelete("/{id:guid}/agents/{agentId:guid}")]
    public static async Task<IResult> RemoveAllowedAgent(
        Guid id, Guid agentId, ContextService svc)
    {
        var result = await svc.RemoveAllowedAgentAsync(id, agentId);
        return result is not null ? Results.Ok(result) : Results.NotFound();
    }

    // ── Default resources (bulk) ──────────────────────────────────

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

    // ── Default resources (per-key) ───────────────────────────────

    [MapPut("/{id:guid}/defaults/{key}")]
    public static async Task<IResult> SetDefaultByKey(
        Guid id, string key, SetDefaultResourceByKeyRequest request,
        DefaultResourceSetService svc)
    {
        if (!DefaultResourceSetService.IsValidKey(key))
            return Results.BadRequest($"Unknown default resource key: {key}");

        var result = await svc.SetKeyForContextAsync(id, key, request.ResourceId);
        return result is not null ? Results.Ok(result) : Results.NotFound();
    }

    [MapDelete("/{id:guid}/defaults/{key}")]
    public static async Task<IResult> ClearDefaultByKey(
        Guid id, string key, DefaultResourceSetService svc)
    {
        if (!DefaultResourceSetService.IsValidKey(key))
            return Results.BadRequest($"Unknown default resource key: {key}");

        var result = await svc.ClearKeyForContextAsync(id, key);
        return result is not null ? Results.Ok(result) : Results.NotFound();
    }
}
