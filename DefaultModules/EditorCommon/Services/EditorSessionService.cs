using SharpClaw.Contracts.DTOs.Editor;
using SharpClaw.Modules.EditorCommon.Models;

namespace SharpClaw.Modules.EditorCommon.Services;

/// <summary>
/// CRUD for <see cref="EditorSessionDB"/> resources. Sessions are
/// typically auto-created when an IDE extension connects via the
/// <see cref="EditorBridgeService"/>, but can also be managed manually.
/// </summary>
public sealed class EditorSessionService(EditorSessionStore store)
{
    public async Task<EditorSessionResponse> CreateAsync(
        CreateEditorSessionRequest request, CancellationToken ct = default)
    {
        var session = new EditorSessionDB
        {
            Name = request.Name,
            EditorType = Enum.TryParse<EditorType>(request.EditorKey, ignoreCase: true, out var et)
                ? et
                : EditorType.Other,
            EditorVersion = request.EditorVersion,
            WorkspacePath = request.WorkspacePath,
            Description = request.Description
        };

        return ToResponse(await store.CreateAsync(session, ct));
    }

    public async Task<IReadOnlyList<EditorSessionResponse>> ListAsync(
        CancellationToken ct = default)
    {
        var sessions = await store.ListAsync(ct);
        return [.. sessions.OrderByDescending(s => s.CreatedAt).Select(ToResponse)];
    }

    public async Task<EditorSessionResponse?> GetByIdAsync(
        Guid id, CancellationToken ct = default)
    {
        var session = await store.GetByIdAsync(id, ct);
        return session is null ? null : ToResponse(session);
    }

    public async Task<EditorSessionResponse?> UpdateAsync(
        Guid id, UpdateEditorSessionRequest request,
        CancellationToken ct = default)
    {
        var session = await store.UpdateAsync(id, session =>
        {
            if (request.Name is not null) session.Name = request.Name;
            if (request.Description is not null) session.Description = request.Description;
        }, ct);
        return session is null ? null : ToResponse(session);
    }

    public async Task<bool> DeleteAsync(
        Guid id, CancellationToken ct = default)
        => await store.DeleteAsync(id, ct);

    /// <summary>
    /// Finds an existing session by workspace path + editor type, or
    /// creates a new one.  Used by <see cref="EditorBridgeService"/>
    /// during auto-registration.
    /// </summary>
    public async Task<EditorSessionDB> GetOrCreateAsync(
        string name,
        EditorType editorType,
        string? editorVersion,
        string? workspacePath,
        CancellationToken ct = default)
        => await store.GetOrCreateAsync(name, editorType, editorVersion, workspacePath, ct);

    public async Task SetConnectionIdAsync(
        Guid id,
        string? connectionId,
        CancellationToken ct = default)
    {
        await store.UpdateAsync(id, session => session.ConnectionId = connectionId, ct);
    }

    internal static EditorSessionResponse ToResponse(EditorSessionDB s) =>
        new(s.Id, s.Name, s.EditorType.ToString(), s.EditorVersion,
            s.WorkspacePath, s.Description,
            s.ConnectionId is not null, s.CreatedAt);
}
