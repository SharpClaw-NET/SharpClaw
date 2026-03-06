using Microsoft.AspNetCore.Http;
using SharpClaw.Application.API.Routing;
using SharpClaw.Application.Services;
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
        return Results.Ok(new DeviceCodeResponse(
            session.DeviceCode, session.UserCode, session.VerificationUri,
            session.ExpiresInSeconds, session.IntervalSeconds));
    }

    [MapPost("/{id:guid}/auth/device-code/poll")]
    public static async Task<IResult> PollDeviceCodeFlow(
        Guid id, DeviceCodePollRequest request, ProviderService svc)
    {
        try
        {
            var session = new DeviceCodeSession(
                request.DeviceCode, request.UserCode,
                request.VerificationUri, request.ExpiresInSeconds, request.IntervalSeconds);
            await svc.CompleteDeviceCodeFlowAsync(id, session);
            return Results.Ok(new { status = "completed" });
        }
        catch (TimeoutException)
        {
            return Results.Json(new { status = "expired" }, statusCode: StatusCodes.Status408RequestTimeout);
        }
    }
}
