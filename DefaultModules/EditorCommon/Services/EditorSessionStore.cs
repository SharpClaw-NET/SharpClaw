using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.EditorCommon.Models;

namespace SharpClaw.Modules.EditorCommon.Services;

public sealed class EditorSessionStore
{
    private const string ModuleId = "sharpclaw_editor_common";
    private const string StorageName = "editor_sessions";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ModuleDocumentStore<EditorSessionDB> _store;

    public EditorSessionStore(IModuleStorageGateway storageGateway)
    {
        _store = new ModuleDocumentStore<EditorSessionDB>(
            storageGateway,
            ModuleId,
            StorageName,
            JsonOptions);
    }

    public async Task<EditorSessionDB> CreateAsync(
        EditorSessionDB session,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (session.Id == Guid.Empty)
            session.Id = Guid.NewGuid();
        if (session.CreatedAt == default)
            session.CreatedAt = now;
        session.UpdatedAt = now;
        await SaveAsync(session, ct);
        return session;
    }

    public async Task<IReadOnlyList<EditorSessionDB>> ListAsync(CancellationToken ct = default) =>
        [.. (await _store.ListAsync(ct)).OrderBy(session => session.Name, StringComparer.Ordinal)];

    public Task<EditorSessionDB?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _store.GetAsync(Key(id), ct);

    public async Task<EditorSessionDB?> UpdateAsync(
        Guid id,
        Action<EditorSessionDB> update,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var session = await GetByIdAsync(id, ct);
        if (session is null)
            return null;

        update(session);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveAsync(session, ct);
        return session;
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) =>
        _store.DeleteAsync(Key(id), ct);

    public async Task<EditorSessionDB> GetOrCreateAsync(
        string name,
        EditorType editorType,
        string? editorVersion,
        string? workspacePath,
        CancellationToken ct = default)
    {
        var workspaceKey = WorkspaceIndex(editorType, workspacePath);
        var existing = (await _store.Query()
                .WhereIndex("editorWorkspace").EqualTo(workspaceKey)
                .Take(1)
                .ToListAsync(ct))
            .FirstOrDefault();

        if (existing is not null)
        {
            existing.EditorVersion = editorVersion;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveAsync(existing, ct);
            return existing;
        }

        var now = DateTimeOffset.UtcNow;
        var session = new EditorSessionDB
        {
            Id = Guid.NewGuid(),
            Name = name,
            EditorType = editorType,
            EditorVersion = editorVersion,
            WorkspacePath = workspacePath,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await SaveAsync(session, ct);
        return session;
    }

    private Task SaveAsync(EditorSessionDB session, CancellationToken ct) =>
        _store.UpsertAsync(
            Key(session.Id),
            session,
            new
            {
                name = session.Name,
                editorType = session.EditorType.ToString(),
                workspacePath = session.WorkspacePath ?? "",
                editorWorkspace = WorkspaceIndex(session.EditorType, session.WorkspacePath),
            },
            ct);

    private static string WorkspaceIndex(EditorType editorType, string? workspacePath) =>
        $"{editorType}|{workspacePath ?? "<null>"}";

    private static string Key(Guid id) => id.ToString("N");
}
