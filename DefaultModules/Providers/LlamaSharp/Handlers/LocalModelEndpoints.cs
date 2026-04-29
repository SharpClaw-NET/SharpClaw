using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SharpClaw.Contracts.DTOs.LocalModels;
using SharpClaw.Modules.Providers.LlamaSharp.Services;

namespace SharpClaw.Modules.Providers.LlamaSharp.Handlers;

public static class LocalModelEndpoints
{
    public static IEndpointRouteBuilder MapLocalModelEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/models/local");

        group.MapPost("/download", async (DownloadModelRequest request, LocalModelService svc) =>
        {
            if (request.ProviderKey is null)
                return Results.BadRequest("ProviderKey is required. Specify a local provider (e.g. llamasharp).");
            return Results.Ok(await svc.DownloadAndRegisterAsync(request));
        });

        group.MapGet("/download/list", async (string url, LocalModelService svc)
            => Results.Ok(await svc.ListAvailableFilesAsync(url)));

        group.MapGet("/", async (LocalModelService svc)
            => Results.Ok(await svc.ListLocalModelsAsync()));

        group.MapPost("/{modelId:guid}/load", async (Guid modelId, LoadModelRequest request, LocalModelService svc) =>
        {
            await svc.LoadModelAsync(modelId, request);
            return Results.Ok(new { modelId, pinned = true });
        });

        group.MapPost("/{modelId:guid}/unload", async (Guid modelId, LocalModelService svc) =>
        {
            await svc.UnloadModelAsync(modelId);
            return Results.Ok();
        });

        group.MapDelete("/{modelId:guid}", async (Guid modelId, LocalModelService svc)
            => await svc.DeleteLocalModelAsync(modelId) ? Results.NoContent() : Results.NotFound());

        group.MapPut("/{modelId:guid}/mmproj", async (Guid modelId, SetMmprojRequest request, LocalModelService svc) =>
        {
            await svc.SetMmprojPathAsync(modelId, request.MmprojPath);
            return Results.Ok(new { modelId, mmprojPath = request.MmprojPath });
        });

        return routes;
    }
}
