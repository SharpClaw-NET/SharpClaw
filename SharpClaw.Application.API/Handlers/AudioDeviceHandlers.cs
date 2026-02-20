using Microsoft.AspNetCore.Http;
using SharpClaw.Application.API.Routing;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.Transcription;

namespace SharpClaw.Application.API.Handlers;

[RouteGroup("/audio-devices")]
public static class AudioDeviceHandlers
{
    [MapPost]
    public static async Task<IResult> Create(CreateAudioDeviceRequest request, TranscriptionService svc)
        => Results.Ok(await svc.CreateDeviceAsync(request));

    [MapGet]
    public static async Task<IResult> List(TranscriptionService svc)
        => Results.Ok(await svc.ListDevicesAsync());

    [MapGet("/{id:guid}")]
    public static async Task<IResult> GetById(Guid id, TranscriptionService svc)
    {
        var device = await svc.GetDeviceByIdAsync(id);
        return device is not null ? Results.Ok(device) : Results.NotFound();
    }

    [MapPut("/{id:guid}")]
    public static async Task<IResult> Update(Guid id, UpdateAudioDeviceRequest request, TranscriptionService svc)
    {
        var device = await svc.UpdateDeviceAsync(id, request);
        return device is not null ? Results.Ok(device) : Results.NotFound();
    }

    [MapDelete("/{id:guid}")]
    public static async Task<IResult> Delete(Guid id, TranscriptionService svc)
        => await svc.DeleteDeviceAsync(id) ? Results.NoContent() : Results.NotFound();
}
