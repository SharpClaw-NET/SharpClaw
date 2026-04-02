using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.API.Routing;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.Containers;
using SharpClaw.Contracts.DTOs.DisplayDevices;
using SharpClaw.Contracts.DTOs.Documents;
using SharpClaw.Contracts.DTOs.Editor;
using SharpClaw.Contracts.DTOs.NativeApplications;
using SharpClaw.Contracts.DTOs.Transcription;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.API.Handlers;

/// <summary>
/// Unified REST surface for all resource types (containers, audio devices,
/// etc.).  Each resource type lives under <c>/resources/{type}/...</c>
/// instead of having its own top-level route group.
/// </summary>
[RouteGroup("/resources")]
public static class ResourceHandlers
{
    // ═══════════════════════════════════════════════════════════════
    // Containers
    // ═══════════════════════════════════════════════════════════════

    [MapPost("/containers")]
    public static async Task<IResult> CreateContainer(
        CreateContainerRequest request, ContainerService svc)
        => Results.Ok(await svc.CreateAsync(request));

    [MapGet("/containers")]
    public static async Task<IResult> ListContainers(ContainerService svc)
        => Results.Ok(await svc.ListAsync());

    [MapGet("/containers/{id:guid}")]
    public static async Task<IResult> GetContainer(Guid id, ContainerService svc)
    {
        var container = await svc.GetByIdAsync(id);
        return container is not null ? Results.Ok(container) : Results.NotFound();
    }

    [MapPut("/containers/{id:guid}")]
    public static async Task<IResult> UpdateContainer(
        Guid id, UpdateContainerRequest request, ContainerService svc)
    {
        var container = await svc.UpdateAsync(id, request);
        return container is not null ? Results.Ok(container) : Results.NotFound();
    }

    [MapDelete("/containers/{id:guid}")]
    public static async Task<IResult> DeleteContainer(Guid id, ContainerService svc)
        => await svc.DeleteAsync(id) ? Results.NoContent() : Results.NotFound();

    [MapPost("/containers/sync")]
    public static async Task<IResult> SyncContainers(ContainerService svc)
        => Results.Ok(await svc.SyncLocalMk8ShellAsync());

    // ═══════════════════════════════════════════════════════════════
    // Audio Devices
    // ═══════════════════════════════════════════════════════════════

    [MapPost("/audiodevices")]
    public static async Task<IResult> CreateAudioDevice(
        CreateAudioDeviceRequest request, TranscriptionService svc)
        => Results.Ok(await svc.CreateDeviceAsync(request));

    [MapGet("/audiodevices")]
    public static async Task<IResult> ListAudioDevices(TranscriptionService svc)
        => Results.Ok(await svc.ListDevicesAsync());

    [MapGet("/audiodevices/{id:guid}")]
    public static async Task<IResult> GetAudioDevice(Guid id, TranscriptionService svc)
    {
        var device = await svc.GetDeviceByIdAsync(id);
        return device is not null ? Results.Ok(device) : Results.NotFound();
    }

    [MapPut("/audiodevices/{id:guid}")]
    public static async Task<IResult> UpdateAudioDevice(
        Guid id, UpdateAudioDeviceRequest request, TranscriptionService svc)
    {
        var device = await svc.UpdateDeviceAsync(id, request);
        return device is not null ? Results.Ok(device) : Results.NotFound();
    }

    [MapDelete("/audiodevices/{id:guid}")]
    public static async Task<IResult> DeleteAudioDevice(Guid id, TranscriptionService svc)
        => await svc.DeleteDeviceAsync(id) ? Results.NoContent() : Results.NotFound();

    [MapPost("/audiodevices/sync")]
    public static async Task<IResult> SyncAudioDevices(TranscriptionService svc)
        => Results.Ok(await svc.SyncDevicesAsync());

    // ═══════════════════════════════════════════════════════════════
    // Display Devices
    // ═══════════════════════════════════════════════════════════════

    [MapPost("/displaydevices")]
    public static async Task<IResult> CreateDisplayDevice(
        CreateDisplayDeviceRequest request, DisplayDeviceService svc)
        => Results.Ok(await svc.CreateAsync(request));

    [MapGet("/displaydevices")]
    public static async Task<IResult> ListDisplayDevices(DisplayDeviceService svc)
        => Results.Ok(await svc.ListAsync());

    [MapGet("/displaydevices/{id:guid}")]
    public static async Task<IResult> GetDisplayDevice(Guid id, DisplayDeviceService svc)
    {
        var device = await svc.GetByIdAsync(id);
        return device is not null ? Results.Ok(device) : Results.NotFound();
    }

    [MapPut("/displaydevices/{id:guid}")]
    public static async Task<IResult> UpdateDisplayDevice(
        Guid id, UpdateDisplayDeviceRequest request, DisplayDeviceService svc)
    {
        var device = await svc.UpdateAsync(id, request);
        return device is not null ? Results.Ok(device) : Results.NotFound();
    }

    [MapDelete("/displaydevices/{id:guid}")]
    public static async Task<IResult> DeleteDisplayDevice(Guid id, DisplayDeviceService svc)
        => await svc.DeleteAsync(id) ? Results.NoContent() : Results.NotFound();

    [MapPost("/displaydevices/sync")]
    public static async Task<IResult> SyncDisplayDevices(DisplayDeviceService svc)
        => Results.Ok(await svc.SyncAsync());

    // ── Editor Sessions ───────────────────────────────────────────

    [MapPost("/editorsessions")]
    public static async Task<IResult> CreateEditorSession(
        CreateEditorSessionRequest request, EditorSessionService svc)
        => Results.Ok(await svc.CreateAsync(request));

    [MapGet("/editorsessions")]
    public static async Task<IResult> ListEditorSessions(EditorSessionService svc)
        => Results.Ok(await svc.ListAsync());

    [MapGet("/editorsessions/{id}")]
    public static async Task<IResult> GetEditorSession(Guid id, EditorSessionService svc)
        => await svc.GetByIdAsync(id) is { } r ? Results.Ok(r) : Results.NotFound();

    [MapPut("/editorsessions/{id}")]
    public static async Task<IResult> UpdateEditorSession(
        Guid id, UpdateEditorSessionRequest request, EditorSessionService svc)
        => await svc.UpdateAsync(id, request) is { } r ? Results.Ok(r) : Results.NotFound();

    [MapDelete("/editorsessions/{id}")]
    public static async Task<IResult> DeleteEditorSession(Guid id, EditorSessionService svc)
        => await svc.DeleteAsync(id) ? Results.NoContent() : Results.NotFound();

    // ═══════════════════════════════════════════════════════════════
    // Universal resource lookup
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns lightweight <c>[{id, name}]</c> items for the resource type
    /// that backs a given permission access category.  The <paramref name="type"/>
    /// value matches the JSON property names used in the permissions API
    /// (e.g. <c>audioDeviceAccesses</c>, <c>containerAccesses</c>).
    /// </summary>
    [MapGet("/lookup/{type}")]
    public static async Task<IResult> LookupByAccessType(string type, SharpClawDbContext db)
    {
        IQueryable<ResourceItem>? query = type switch
        {
            "dangerousShellAccesses" => db.SystemUsers
                .Select(e => new ResourceItem(e.Id, e.Username)),
            "safeShellAccesses" or "containerAccesses" => db.Containers
                .Select(e => new ResourceItem(e.Id, e.Name)),
            "websiteAccesses" => db.Websites
                .Select(e => new ResourceItem(e.Id, e.Name)),
            "searchEngineAccesses" => db.SearchEngines
                .Select(e => new ResourceItem(e.Id, e.Name)),
            "localInfoStoreAccesses" => db.LocalInformationStores
                .Select(e => new ResourceItem(e.Id, e.Name)),
            "externalInfoStoreAccesses" => db.ExternalInformationStores
                .Select(e => new ResourceItem(e.Id, e.Name)),
            "audioDeviceAccesses" => db.AudioDevices
                .Select(e => new ResourceItem(e.Id, e.Name)),
            "displayDeviceAccesses" => db.DisplayDevices
                .Select(e => new ResourceItem(e.Id, e.Name)),
            "editorSessionAccesses" => db.EditorSessions
                .Select(e => new ResourceItem(e.Id, e.Name)),
            "agentAccesses" => db.Agents
                .Select(e => new ResourceItem(e.Id, e.Name)),
            "taskAccesses" => db.ScheduledTasks
                .Select(e => new ResourceItem(e.Id, e.Name)),
            "skillAccesses" => db.Skills
                .Select(e => new ResourceItem(e.Id, e.Name)),
            "documentSessionAccesses" => db.DocumentSessions
                .Select(e => new ResourceItem(e.Id, e.Name)),
            "nativeApplicationAccesses" => db.NativeApplications
                .Select(e => new ResourceItem(e.Id, e.Name)),
            _ => null,
        };

        if (query is null)
            return Results.BadRequest(new { error = $"Unknown resource type '{type}'." });

        return Results.Ok(await query.OrderBy(r => r.Name).ToListAsync());
    }

    private sealed record ResourceItem(Guid Id, string Name);

    // ═══════════════════════════════════════════════════════════════
    // Document Sessions
    // ═══════════════════════════════════════════════════════════════

    [MapPost("/documents")]
    public static async Task<IResult> CreateDocumentSession(
        CreateDocumentSessionRequest request, DocumentSessionService svc)
        => Results.Ok(await svc.CreateAsync(request));

    [MapGet("/documents")]
    public static async Task<IResult> ListDocumentSessions(DocumentSessionService svc)
        => Results.Ok(await svc.ListAsync());

    [MapGet("/documents/{id}")]
    public static async Task<IResult> GetDocumentSession(Guid id, DocumentSessionService svc)
        => await svc.GetByIdAsync(id) is { } r ? Results.Ok(r) : Results.NotFound();

    [MapPut("/documents/{id}")]
    public static async Task<IResult> UpdateDocumentSession(
        Guid id, UpdateDocumentSessionRequest request, DocumentSessionService svc)
        => await svc.UpdateAsync(id, request) is { } r ? Results.Ok(r) : Results.NotFound();

    [MapDelete("/documents/{id}")]
    public static async Task<IResult> DeleteDocumentSession(Guid id, DocumentSessionService svc)
        => await svc.DeleteAsync(id) ? Results.NoContent() : Results.NotFound();

    // ═══════════════════════════════════════════════════════════════
    // Native Applications
    // ═══════════════════════════════════════════════════════════════

    [MapPost("/nativeapplications")]
    public static async Task<IResult> CreateNativeApplication(
        CreateNativeApplicationRequest request, NativeApplicationService svc)
        => Results.Ok(await svc.CreateAsync(request));

    [MapGet("/nativeapplications")]
    public static async Task<IResult> ListNativeApplications(NativeApplicationService svc)
        => Results.Ok(await svc.ListAsync());

    [MapGet("/nativeapplications/{id}")]
    public static async Task<IResult> GetNativeApplication(Guid id, NativeApplicationService svc)
        => await svc.GetByIdAsync(id) is { } r ? Results.Ok(r) : Results.NotFound();

    [MapPut("/nativeapplications/{id}")]
    public static async Task<IResult> UpdateNativeApplication(
        Guid id, UpdateNativeApplicationRequest request, NativeApplicationService svc)
        => await svc.UpdateAsync(id, request) is { } r ? Results.Ok(r) : Results.NotFound();

    [MapDelete("/nativeapplications/{id}")]
    public static async Task<IResult> DeleteNativeApplication(Guid id, NativeApplicationService svc)
        => await svc.DeleteAsync(id) ? Results.NoContent() : Results.NotFound();
}
