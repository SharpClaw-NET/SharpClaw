using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.EditorCommon.Models;

namespace SharpClaw.Modules.EditorCommon.Services;

public sealed class EditorSessionStore(IModuleConfigStore configStore)
{
    private const string StoreKey = "editor_sessions.v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<EditorSessionDB> CreateAsync(
        EditorSessionDB session,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var sessions = await LoadUnlockedAsync(ct);
            var now = DateTimeOffset.UtcNow;
            if (session.Id == Guid.Empty)
                session.Id = Guid.NewGuid();
            if (session.CreatedAt == default)
                session.CreatedAt = now;
            session.UpdatedAt = now;
            sessions.Add(session);
            await SaveUnlockedAsync(sessions, ct);
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<EditorSessionDB>> ListAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return [.. await LoadUnlockedAsync(ct)];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EditorSessionDB?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return (await LoadUnlockedAsync(ct)).FirstOrDefault(session => session.Id == id);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EditorSessionDB?> UpdateAsync(
        Guid id,
        Action<EditorSessionDB> update,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var sessions = await LoadUnlockedAsync(ct);
            var session = sessions.FirstOrDefault(candidate => candidate.Id == id);
            if (session is null)
                return null;

            update(session);
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveUnlockedAsync(sessions, ct);
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var sessions = await LoadUnlockedAsync(ct);
            var removed = sessions.RemoveAll(session => session.Id == id) > 0;
            if (removed)
                await SaveUnlockedAsync(sessions, ct);
            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EditorSessionDB> GetOrCreateAsync(
        string name,
        EditorType editorType,
        string? editorVersion,
        string? workspacePath,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var sessions = await LoadUnlockedAsync(ct);
            var existing = sessions.FirstOrDefault(session =>
                session.EditorType == editorType
                && string.Equals(session.WorkspacePath, workspacePath, StringComparison.Ordinal));

            if (existing is not null)
            {
                existing.EditorVersion = editorVersion;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                await SaveUnlockedAsync(sessions, ct);
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

            sessions.Add(session);
            await SaveUnlockedAsync(sessions, ct);
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<EditorSessionDB>> LoadUnlockedAsync(CancellationToken ct)
    {
        var json = await configStore.GetAsync(StoreKey, ct);
        if (string.IsNullOrWhiteSpace(json))
            return [];

        return JsonSerializer.Deserialize<List<EditorSessionDB>>(json, JsonOptions) ?? [];
    }

    private async Task SaveUnlockedAsync(List<EditorSessionDB> sessions, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(sessions, JsonOptions);
        await configStore.SetAsync(StoreKey, json, ct);
    }
}
