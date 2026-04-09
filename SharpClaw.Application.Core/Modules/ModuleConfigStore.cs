using System.Globalization;

using Microsoft.EntityFrameworkCore;

using SharpClaw.Application.Infrastructure.Models;
using SharpClaw.Contracts.Modules;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Core.Modules;

/// <summary>
/// Per-module persistent key-value store backed by the host <see cref="SharpClawDbContext"/>.
/// Each instance is scoped to a single module ID (set via <see cref="ModuleExecutionContext"/>).
/// </summary>
public sealed class ModuleConfigStore(SharpClawDbContext db, string moduleId) : IModuleConfigStore
{
    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        var entry = await db.ModuleConfigEntries
            .FirstOrDefaultAsync(e => e.ModuleId == moduleId && e.Key == key, ct);
        return entry?.Value;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct) where T : IParsable<T>
    {
        var raw = await GetAsync(key, ct);
        if (raw is null) return default;
        return T.TryParse(raw, CultureInfo.InvariantCulture, out var result) ? result : default;
    }

    public async Task SetAsync(string key, string? value, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        var entry = await db.ModuleConfigEntries
            .FirstOrDefaultAsync(e => e.ModuleId == moduleId && e.Key == key, ct);

        if (value is null)
        {
            // Delete
            if (entry is not null) db.ModuleConfigEntries.Remove(entry);
        }
        else if (entry is not null)
        {
            entry.Value = value;
        }
        else
        {
            db.ModuleConfigEntries.Add(new ModuleConfigEntryDB
            {
                ModuleId = moduleId,
                Key = key,
                Value = value,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct)
    {
        var entries = await db.ModuleConfigEntries
            .Where(e => e.ModuleId == moduleId)
            .ToListAsync(ct);

        return entries
            .Where(e => e.Value is not null)
            .ToDictionary(e => e.Key, e => e.Value!);
    }
}
