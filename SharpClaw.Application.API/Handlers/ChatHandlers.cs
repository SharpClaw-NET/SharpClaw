using Microsoft.AspNetCore.Http;
using SharpClaw.Application.API.Routing;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.Chat;

namespace SharpClaw.Application.API.Handlers;

[RouteGroup("/channels/{id:guid}/chat")]
public static class ChatHandlers
{
    [MapPost]
    public static async Task<IResult> Send(Guid id, ChatRequest request, ChatService svc)
        => Results.Ok(await svc.SendMessageAsync(id, request));

    [MapGet]
    public static async Task<IResult> History(Guid id, ChatService svc)
        => Results.Ok(await svc.GetHistoryAsync(id));

    [MapGet("/cost")]
    public static async Task<IResult> ChannelCost(Guid id, ChatService svc)
        => Results.Ok(await svc.GetChannelCostAsync(id));

    [MapGet("/threads/{threadId:guid}/cost")]
    public static async Task<IResult> ThreadCost(Guid id, Guid threadId, ChatService svc)
    {
        var result = await svc.GetThreadCostAsync(id, threadId);
        return result is not null ? Results.Ok(result) : Results.NotFound();
    }
}
