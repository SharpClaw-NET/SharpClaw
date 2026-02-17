using Microsoft.AspNetCore.Http;
using SharpClaw.Application.API.Routing;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.Agents;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.DTOs.Contexts;
using SharpClaw.Contracts.DTOs.Conversations;
using SharpClaw.Contracts.DTOs.Models;
using SharpClaw.Contracts.DTOs.Providers;

namespace SharpClaw.Application.API.Handlers;

[RouteGroup("/providers")]
public static class ProviderHandlers
{
    [MapPost]
    public static async Task<IResult> Create(CreateProviderRequest request, ProviderService svc)
        => Results.Ok(await svc.CreateAsync(request));

    [MapGet]
    public static async Task<IResult> List(ProviderService svc)
        => Results.Ok(await svc.ListAsync());

    [MapGet("/{id:guid}")]
    public static async Task<IResult> GetById(Guid id, ProviderService svc)
    {
        var provider = await svc.GetByIdAsync(id);
        return provider is not null ? Results.Ok(provider) : Results.NotFound();
    }

    [MapPut("/{id:guid}")]
    public static async Task<IResult> Update(Guid id, UpdateProviderRequest request, ProviderService svc)
    {
        var provider = await svc.UpdateAsync(id, request);
        return provider is not null ? Results.Ok(provider) : Results.NotFound();
    }

    [MapDelete("/{id:guid}")]
    public static async Task<IResult> Delete(Guid id, ProviderService svc)
        => await svc.DeleteAsync(id) ? Results.NoContent() : Results.NotFound();

    [MapPost("/{id:guid}/sync-models")]
    public static async Task<IResult> SyncModels(Guid id, ProviderService svc)
        => Results.Ok(await svc.SyncModelsAsync(id));

    [MapPost("/{id:guid}/set-key")]
    public static async Task<IResult> SetApiKey(Guid id, SetApiKeyRequest request, ProviderService svc)
    {
        await svc.SetApiKeyAsync(id, request.ApiKey);
        return Results.NoContent();
    }

    [MapPost("/{id:guid}/auth/device-code")]
    public static async Task<IResult> StartDeviceCodeFlow(Guid id, ProviderService svc)
    {
        var session = await svc.StartDeviceCodeFlowAsync(id);
        return Results.Ok(new DeviceCodeResponse(session.UserCode, session.VerificationUri, session.ExpiresInSeconds));
    }
}

[RouteGroup("/models")]
public static class ModelHandlers
{
    [MapPost]
    public static async Task<IResult> Create(CreateModelRequest request, ModelService svc)
        => Results.Ok(await svc.CreateAsync(request));

    [MapGet]
    public static async Task<IResult> List(ModelService svc)
        => Results.Ok(await svc.ListAsync());

    [MapGet("/{id:guid}")]
    public static async Task<IResult> GetById(Guid id, ModelService svc)
    {
        var model = await svc.GetByIdAsync(id);
        return model is not null ? Results.Ok(model) : Results.NotFound();
    }

    [MapPut("/{id:guid}")]
    public static async Task<IResult> Update(Guid id, UpdateModelRequest request, ModelService svc)
    {
        var model = await svc.UpdateAsync(id, request);
        return model is not null ? Results.Ok(model) : Results.NotFound();
    }

    [MapDelete("/{id:guid}")]
    public static async Task<IResult> Delete(Guid id, ModelService svc)
        => await svc.DeleteAsync(id) ? Results.NoContent() : Results.NotFound();
}

[RouteGroup("/agents")]
public static class AgentHandlers
{
    [MapPost]
    public static async Task<IResult> Create(CreateAgentRequest request, AgentService svc)
        => Results.Ok(await svc.CreateAsync(request));

    [MapGet]
    public static async Task<IResult> List(AgentService svc)
        => Results.Ok(await svc.ListAsync());

    [MapGet("/{id:guid}")]
    public static async Task<IResult> GetById(Guid id, AgentService svc)
    {
        var agent = await svc.GetByIdAsync(id);
        return agent is not null ? Results.Ok(agent) : Results.NotFound();
    }

    [MapPut("/{id:guid}")]
    public static async Task<IResult> Update(Guid id, UpdateAgentRequest request, AgentService svc)
    {
        var agent = await svc.UpdateAsync(id, request);
        return agent is not null ? Results.Ok(agent) : Results.NotFound();
    }

    [MapDelete("/{id:guid}")]
    public static async Task<IResult> Delete(Guid id, AgentService svc)
        => await svc.DeleteAsync(id) ? Results.NoContent() : Results.NotFound();
}

[RouteGroup("/contexts")]
public static class ContextHandlers
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

    [MapPost("/{id:guid}/grant")]
    public static async Task<IResult> Grant(
        Guid id, PermissionGrantRequest request, ContextService svc)
    {
        var context = await svc.GrantPermissionAsync(id, request);
        return context is not null ? Results.Ok(context) : Results.NotFound();
    }
}

[RouteGroup("/conversations")]
public static class ConversationHandlers
{
    [MapPost]
    public static async Task<IResult> Create(CreateConversationRequest request, ConversationService svc)
        => Results.Ok(await svc.CreateAsync(request));

    [MapGet]
    public static async Task<IResult> List(ConversationService svc, Guid? agentId = null)
        => Results.Ok(await svc.ListAsync(agentId));

    [MapGet("/{id:guid}")]
    public static async Task<IResult> GetById(Guid id, ConversationService svc)
    {
        var conversation = await svc.GetByIdAsync(id);
        return conversation is not null ? Results.Ok(conversation) : Results.NotFound();
    }

    [MapPut("/{id:guid}")]
    public static async Task<IResult> Update(Guid id, UpdateConversationRequest request, ConversationService svc)
    {
        var conversation = await svc.UpdateAsync(id, request);
        return conversation is not null ? Results.Ok(conversation) : Results.NotFound();
    }

    [MapDelete("/{id:guid}")]
    public static async Task<IResult> Delete(Guid id, ConversationService svc)
        => await svc.DeleteAsync(id) ? Results.NoContent() : Results.NotFound();

    [MapPost("/{id:guid}/grant")]
    public static async Task<IResult> Grant(
        Guid id, ConversationPermissionGrantRequest request, ConversationService svc)
    {
        var conversation = await svc.GrantPermissionAsync(id, request);
        return conversation is not null ? Results.Ok(conversation) : Results.NotFound();
    }
}

[RouteGroup("/conversations/{id:guid}/chat")]
public static class ChatHandlers
{
    [MapPost]
    public static async Task<IResult> Send(Guid id, ChatRequest request, ChatService svc)
        => Results.Ok(await svc.SendMessageAsync(id, request));

    [MapGet]
    public static async Task<IResult> History(Guid id, ChatService svc)
        => Results.Ok(await svc.GetHistoryAsync(id));
}
