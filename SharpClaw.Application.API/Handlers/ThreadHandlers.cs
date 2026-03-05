using Microsoft.AspNetCore.Http;
using SharpClaw.Application.API.Routing;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.Threads;

namespace SharpClaw.Application.API.Handlers;

[RouteGroup("/channels/{channelId:guid}/threads")]
public static class ThreadHandlers
{
    [MapPost]
    public static async Task<IResult> Create(Guid channelId, CreateThreadRequest request, ThreadService svc)
        => Results.Ok(await svc.CreateAsync(channelId, request));

    [MapGet]
    public static async Task<IResult> List(Guid channelId, ThreadService svc)
        => Results.Ok(await svc.ListAsync(channelId));

    [MapGet("/{threadId:guid}")]
    public static async Task<IResult> GetById(Guid channelId, Guid threadId, ThreadService svc)
    {
        var thread = await svc.GetByIdAsync(threadId);
        return thread is not null ? Results.Ok(thread) : Results.NotFound();
    }

    [MapPut("/{threadId:guid}")]
    public static async Task<IResult> Update(Guid channelId, Guid threadId, UpdateThreadRequest request, ThreadService svc)
    {
        var thread = await svc.UpdateAsync(threadId, request);
        return thread is not null ? Results.Ok(thread) : Results.NotFound();
    }

    [MapDelete("/{threadId:guid}")]
    public static async Task<IResult> Delete(Guid channelId, Guid threadId, ThreadService svc)
        => await svc.DeleteAsync(threadId) ? Results.NoContent() : Results.NotFound();
}
