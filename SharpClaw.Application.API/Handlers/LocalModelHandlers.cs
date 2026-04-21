using Microsoft.AspNetCore.Http;
using SharpClaw.Application.API.Routing;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.LocalModels;

namespace SharpClaw.Application.API.Handlers;

[RouteGroup("/models/local")]
public static class LocalModelHandlers
{
    [MapPost("/download")]
    public static async Task<IResult> Download(DownloadModelRequest request, LocalModelService svc)
        => request.ProviderType is null
            ? Results.Ok(await svc.DownloadAndRegisterBothAsync(request))
            : Results.Ok(await svc.DownloadAndRegisterAsync(request));

    [MapGet("/download/list")]
    public static async Task<IResult> ListFiles(string url, LocalModelService svc)
        => Results.Ok(await svc.ListAvailableFilesAsync(url));

    [MapGet]
    public static async Task<IResult> List(LocalModelService svc)
        => Results.Ok(await svc.ListLocalModelsAsync());

    [MapPost("/{modelId:guid}/load")]
    public static async Task<IResult> Load(Guid modelId, LoadModelRequest request, LocalModelService svc)
    {
        await svc.LoadModelAsync(modelId, request);
        return Results.Ok(new { modelId, pinned = true });
    }

    [MapPost("/{modelId:guid}/unload")]
    public static async Task<IResult> Unload(Guid modelId, LocalModelService svc)
    {
        await svc.UnloadModelAsync(modelId);
        return Results.Ok();
    }

    [MapDelete("/{modelId:guid}")]
    public static async Task<IResult> Delete(Guid modelId, LocalModelService svc)
        => await svc.DeleteLocalModelAsync(modelId) ? Results.NoContent() : Results.NotFound();

    [MapPut("/{modelId:guid}/mmproj")]
    public static async Task<IResult> SetMmproj(Guid modelId, SetMmprojRequest request, LocalModelService svc)
    {
        await svc.SetMmprojPathAsync(modelId, request.MmprojPath);
        return Results.Ok(new { modelId, mmprojPath = request.MmprojPath });
    }
}
