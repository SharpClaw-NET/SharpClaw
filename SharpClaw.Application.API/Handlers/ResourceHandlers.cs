using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.API.Routing;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.Containers;
using SharpClaw.Contracts.DTOs.DisplayDevices;
using SharpClaw.Contracts.DTOs.Databases;
using SharpClaw.Contracts.DTOs.Documents;
using SharpClaw.Contracts.DTOs.Editor;
using SharpClaw.Contracts.DTOs.NativeApplications;
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
    // Internal Databases
    // ═══════════════════════════════════════════════════════════════

    [MapPost("/internaldatabases")]
    public static async Task<IResult> CreateInternalDatabase(
        CreateInternalDatabaseRequest request, DatabaseResourceService svc)
        => Results.Ok(await svc.CreateInternalAsync(request));

    [MapGet("/internaldatabases")]
    public static async Task<IResult> ListInternalDatabases(DatabaseResourceService svc)
        => Results.Ok(await svc.ListInternalAsync());

    [MapGet("/internaldatabases/{id:guid}")]
    public static async Task<IResult> GetInternalDatabase(Guid id, DatabaseResourceService svc)
    {
        var item = await svc.GetInternalByIdAsync(id);
        return item is not null ? Results.Ok(item) : Results.NotFound();
    }

    [MapPut("/internaldatabases/{id:guid}")]
    public static async Task<IResult> UpdateInternalDatabase(
        Guid id, UpdateInternalDatabaseRequest request, DatabaseResourceService svc)
    {
        var item = await svc.UpdateInternalAsync(id, request);
        return item is not null ? Results.Ok(item) : Results.NotFound();
    }

    [MapDelete("/internaldatabases/{id:guid}")]
    public static async Task<IResult> DeleteInternalDatabase(Guid id, DatabaseResourceService svc)
        => await svc.DeleteInternalAsync(id) ? Results.NoContent() : Results.NotFound();

    // ═══════════════════════════════════════════════════════════════
    // External Databases
    // ═══════════════════════════════════════════════════════════════

    [MapPost("/externaldatabases")]
    public static async Task<IResult> CreateExternalDatabase(
        CreateExternalDatabaseRequest request, DatabaseResourceService svc)
        => Results.Ok(await svc.CreateExternalAsync(request));

    [MapGet("/externaldatabases")]
    public static async Task<IResult> ListExternalDatabases(DatabaseResourceService svc)
        => Results.Ok(await svc.ListExternalAsync());

    [MapGet("/externaldatabases/{id:guid}")]
    public static async Task<IResult> GetExternalDatabase(Guid id, DatabaseResourceService svc)
    {
        var item = await svc.GetExternalByIdAsync(id);
        return item is not null ? Results.Ok(item) : Results.NotFound();
    }

    [MapPut("/externaldatabases/{id:guid}")]
    public static async Task<IResult> UpdateExternalDatabase(
        Guid id, UpdateExternalDatabaseRequest request, DatabaseResourceService svc)
    {
        var item = await svc.UpdateExternalAsync(id, request);
        return item is not null ? Results.Ok(item) : Results.NotFound();
    }

    [MapDelete("/externaldatabases/{id:guid}")]
    public static async Task<IResult> DeleteExternalDatabase(Guid id, DatabaseResourceService svc)
        => await svc.DeleteExternalAsync(id) ? Results.NoContent() : Results.NotFound();

    // ═══════════════════════════════════════════════════════════════
    // Universal resource lookup
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns lightweight <c>[{id, name}]</c> items for the resource type
    /// that backs a given permission access category.  The <paramref name="type"/>
    /// value matches the JSON property names used in the permissions API
    /// (e.g. <c>inputAudioAccesses</c>, <c>containerAccesses</c>).
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
            "internalDatabaseAccesses" => db.InternalDatabases
                .Select(e => new ResourceItem(e.Id, e.Name)),
            "externalDatabaseAccesses" => db.ExternalDatabases
                .Select(e => new ResourceItem(e.Id, e.Name)),
            "inputAudioAccesses" => db.InputAudios
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
