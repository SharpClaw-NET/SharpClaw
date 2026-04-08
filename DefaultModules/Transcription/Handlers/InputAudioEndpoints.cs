using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SharpClaw.Contracts.DTOs.Transcription;
using SharpClaw.Modules.Transcription.Services;

namespace SharpClaw.Modules.Transcription.Handlers;

/// <summary>
/// Registers minimal-API REST endpoints for Input Audio resource CRUD.
/// Routes are placed under <c>/resources/inputaudios</c> to match the
/// unified resource surface.
/// </summary>
public static class InputAudioEndpoints
{
    /// <summary>
    /// Maps the Input Audio CRUD endpoints.
    /// </summary>
    public static IEndpointRouteBuilder MapInputAudioEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/resources/inputaudios");

        group.MapPost("/", async (CreateInputAudioRequest request, TranscriptionService svc)
            => Results.Ok(await svc.CreateDeviceAsync(request)));

        group.MapGet("/", async (TranscriptionService svc)
            => Results.Ok(await svc.ListDevicesAsync()));

        group.MapGet("/{id:guid}", async (Guid id, TranscriptionService svc) =>
        {
            var device = await svc.GetDeviceByIdAsync(id);
            return device is not null ? Results.Ok(device) : Results.NotFound();
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateInputAudioRequest request, TranscriptionService svc) =>
        {
            var device = await svc.UpdateDeviceAsync(id, request);
            return device is not null ? Results.Ok(device) : Results.NotFound();
        });

        group.MapDelete("/{id:guid}", async (Guid id, TranscriptionService svc)
            => await svc.DeleteDeviceAsync(id) ? Results.NoContent() : Results.NotFound());

        group.MapPost("/sync", async (TranscriptionService svc)
            => Results.Ok(await svc.SyncDevicesAsync()));

        return routes;
    }
}
