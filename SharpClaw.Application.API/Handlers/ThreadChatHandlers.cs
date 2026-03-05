using Microsoft.AspNetCore.Http;
using SharpClaw.Application.API.Routing;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.Chat;

namespace SharpClaw.Application.API.Handlers;

[RouteGroup("/channels/{id:guid}/chat/threads/{threadId:guid}")]
public static class ThreadChatHandlers
{
    [MapPost]
    public static async Task<IResult> Send(Guid id, Guid threadId, ChatRequest request, ChatService svc)
        => Results.Ok(await svc.SendMessageAsync(id, request, threadId));

    [MapGet]
    public static async Task<IResult> History(Guid id, Guid threadId, ChatService svc)
        => Results.Ok(await svc.GetHistoryAsync(id, threadId));
}
