using System.Text.Json;
using SharpClaw.Helpers;

namespace SharpClaw.Services;

/// <summary>
/// Client-side cache of module enabled/disabled state. Populated from the
/// <c>GET /modules</c> API at startup and refreshed on demand. Used by
/// pages to conditionally show/hide module-dependent UI elements.
/// </summary>
internal sealed class ModuleStateCache
{
    private Dictionary<string, ModuleStateCacheEntry> _modules = new(StringComparer.Ordinal);

    /// <summary>Check whether a specific module is currently enabled.</summary>
    public bool IsEnabled(string moduleId)
        => _modules.TryGetValue(moduleId, out var entry) && entry.Enabled;

    /// <summary>Get all cached module entries.</summary>
    public IReadOnlyList<ModuleStateCacheEntry> GetAll()
        => [.. _modules.Values];

    /// <summary>Get a specific module's cached state, or <c>null</c>.</summary>
    public ModuleStateCacheEntry? Get(string moduleId)
        => _modules.GetValueOrDefault(moduleId);

    /// <summary>Refresh the cache from the API.</summary>
    public async Task RefreshAsync(SharpClawApiClient api)
    {
        try
        {
            using var resp = await api.GetAsync("/modules");
            if (!resp.IsSuccessStatusCode) return;

            using var stream = await resp.Content.ReadAsStreamAsync();
            var items = await JsonSerializer.DeserializeAsync<List<ModuleStateCacheEntry>>(stream, TerminalUI.Json);
            if (items is null) return;

            var dict = new Dictionary<string, ModuleStateCacheEntry>(items.Count, StringComparer.Ordinal);
            foreach (var item in items)
                dict[item.ModuleId] = item;
            _modules = dict;
        }
        catch
        {
            // API unreachable — keep previous cache
        }
    }

    [System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip)]
    internal sealed record ModuleStateCacheEntry(
        string ModuleId,
        string DisplayName,
        string ToolPrefix,
        bool Enabled,
        string? Version,
        bool IsExternal);
}
