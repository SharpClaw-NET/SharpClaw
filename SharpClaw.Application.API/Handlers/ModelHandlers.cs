using Microsoft.AspNetCore.Http;
using SharpClaw.Application.API.Routing;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.Models;

namespace SharpClaw.Application.API.Handlers;

[RouteGroup("/models")]
public static class ModelHandlers
{
    [MapPost]
    public static async Task<IResult> Create(CreateModelRequest request, ModelService svc)
        => Results.Ok(await svc.CreateAsync(request));

    [MapGet]
    public static async Task<IResult> List(ModelService svc, Guid? providerId = null)
        => Results.Ok(await svc.ListAsync(providerId));

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
