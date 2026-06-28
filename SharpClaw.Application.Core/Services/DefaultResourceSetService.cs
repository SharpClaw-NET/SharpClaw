using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.DTOs.DefaultResources;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Core.Modules;
using SharpClaw.Core.Resources;

namespace SharpClaw.Application.Services;

/// <summary>
/// Manages <see cref="DefaultResourceSetDB"/> entities attached to
/// channels and contexts.  Valid resource keys come from core defaults
/// and registered module resource descriptors.
/// </summary>
public sealed class DefaultResourceSetService(
    SharpClawDbContext db,
    ModuleRegistry moduleRegistry,
    ChatCache chatCache)
{
    // -- Reads ------------------------------------------------------

    /// <summary>
    /// Gets the default resources for a channel.  Falls through to the
    /// context set for any unset keys.
    /// </summary>
    public async Task<DefaultResourcesResponse?> GetForChannelAsync(
        Guid channelId, CancellationToken ct = default)
    {
        var ch = await db.Channels
            .Include(c => c.DefaultResourceSet!).ThenInclude(drs => drs.Entries)
            .Include(c => c.AgentContext!).ThenInclude(ctx => ctx.DefaultResourceSet!).ThenInclude(drs => drs.Entries)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct);

        if (ch is null) return null;

        return DefaultResourceEngine.Merge(
            ch.Id,
            Snapshot(ch.DefaultResourceSet),
            Snapshot(ch.AgentContext?.DefaultResourceSet));
    }

    /// <summary>
    /// Gets the default resources for a context.
    /// </summary>
    public async Task<DefaultResourcesResponse?> GetForContextAsync(
        Guid contextId, CancellationToken ct = default)
    {
        var ctx = await db.AgentContexts
            .Include(c => c.DefaultResourceSet!).ThenInclude(drs => drs.Entries)
            .FirstOrDefaultAsync(c => c.Id == contextId, ct);

        if (ctx is null) return null;

        return ctx.DefaultResourceSet is { } drs
            ? DefaultResourceEngine.ToResponse(
                DefaultResourceSetSnapshot.FromDefaultResourceSet(drs))
            : DefaultResourceEngine.EmptyResponse(Guid.Empty);
    }

    // -- Bulk writes ------------------------------------------------

    /// <summary>
    /// Sets the default resources for a channel (creates or replaces).
    /// Keys not present in <paramref name="request"/> are left unchanged.
    /// </summary>
    public async Task<DefaultResourcesResponse?> SetForChannelAsync(
        Guid channelId, SetDefaultResourcesRequest request,
        CancellationToken ct = default)
    {
        var ch = await db.Channels
            .Include(c => c.DefaultResourceSet!).ThenInclude(drs => drs.Entries)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct);

        if (ch is null) return null;

        ch.DefaultResourceSet ??= await CreateAndAttachAsync(
            setId => ch.DefaultResourceSetId = setId, ct);

        Apply(ch.DefaultResourceSet, request);
        await db.SaveChangesAsync(ct);
        chatCache.RemoveDefaultResourceResolutionForChannel(channelId);
        return DefaultResourceEngine.ToResponse(
            DefaultResourceSetSnapshot.FromDefaultResourceSet(ch.DefaultResourceSet));
    }

    /// <summary>
    /// Sets the default resources for a context (creates or replaces).
    /// Keys not present in <paramref name="request"/> are left unchanged.
    /// </summary>
    public async Task<DefaultResourcesResponse?> SetForContextAsync(
        Guid contextId, SetDefaultResourcesRequest request,
        CancellationToken ct = default)
    {
        var ctx = await db.AgentContexts
            .Include(c => c.DefaultResourceSet!).ThenInclude(drs => drs.Entries)
            .FirstOrDefaultAsync(c => c.Id == contextId, ct);

        if (ctx is null) return null;

        ctx.DefaultResourceSet ??= await CreateAndAttachAsync(
            setId => ctx.DefaultResourceSetId = setId, ct);

        Apply(ctx.DefaultResourceSet, request);
        await db.SaveChangesAsync(ct);
        await InvalidateContextDefaultResourcesAsync(contextId, ct);
        return DefaultResourceEngine.ToResponse(
            DefaultResourceSetSnapshot.FromDefaultResourceSet(ctx.DefaultResourceSet));
    }

    // -- Per-key operations -----------------------------------------

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="key"/> is a
    /// default-resource key registered by any loaded module.
    /// </summary>
    public bool IsValidKey(string key) =>
        moduleRegistry.IsRegisteredDefaultResourceKey(key);

    /// <summary>
    /// Sets a single default resource by key for a channel.
    /// </summary>
    public async Task<DefaultResourcesResponse?> SetKeyForChannelAsync(
        Guid channelId, string key, Guid resourceId,
        CancellationToken ct = default)
    {
        var ch = await db.Channels
            .Include(c => c.DefaultResourceSet!).ThenInclude(drs => drs.Entries)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct);
        if (ch is null) return null;

        ch.DefaultResourceSet ??= await CreateAndAttachAsync(
            setId => ch.DefaultResourceSetId = setId, ct);

        ApplyKey(ch.DefaultResourceSet, key, resourceId);
        await db.SaveChangesAsync(ct);
        chatCache.RemoveDefaultResourceResolutionForChannel(channelId);
        return DefaultResourceEngine.ToResponse(
            DefaultResourceSetSnapshot.FromDefaultResourceSet(ch.DefaultResourceSet));
    }

    /// <summary>
    /// Clears a single default resource by key for a channel.
    /// </summary>
    public async Task<DefaultResourcesResponse?> ClearKeyForChannelAsync(
        Guid channelId, string key, CancellationToken ct = default)
    {
        var ch = await db.Channels
            .Include(c => c.DefaultResourceSet!).ThenInclude(drs => drs.Entries)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct);
        if (ch is null) return null;
        if (ch.DefaultResourceSet is null)
            return DefaultResourceEngine.EmptyResponse(Guid.Empty);

        ApplyKey(ch.DefaultResourceSet, key, null);
        await db.SaveChangesAsync(ct);
        chatCache.RemoveDefaultResourceResolutionForChannel(channelId);
        return DefaultResourceEngine.ToResponse(
            DefaultResourceSetSnapshot.FromDefaultResourceSet(ch.DefaultResourceSet));
    }

    /// <summary>
    /// Sets a single default resource by key for a context.
    /// </summary>
    public async Task<DefaultResourcesResponse?> SetKeyForContextAsync(
        Guid contextId, string key, Guid resourceId,
        CancellationToken ct = default)
    {
        var ctx = await db.AgentContexts
            .Include(c => c.DefaultResourceSet!).ThenInclude(drs => drs.Entries)
            .FirstOrDefaultAsync(c => c.Id == contextId, ct);
        if (ctx is null) return null;

        ctx.DefaultResourceSet ??= await CreateAndAttachAsync(
            setId => ctx.DefaultResourceSetId = setId, ct);

        ApplyKey(ctx.DefaultResourceSet, key, resourceId);
        await db.SaveChangesAsync(ct);
        await InvalidateContextDefaultResourcesAsync(contextId, ct);
        return DefaultResourceEngine.ToResponse(
            DefaultResourceSetSnapshot.FromDefaultResourceSet(ctx.DefaultResourceSet));
    }

    /// <summary>
    /// Clears a single default resource by key for a context.
    /// </summary>
    public async Task<DefaultResourcesResponse?> ClearKeyForContextAsync(
        Guid contextId, string key, CancellationToken ct = default)
    {
        var ctx = await db.AgentContexts
            .Include(c => c.DefaultResourceSet!).ThenInclude(drs => drs.Entries)
            .FirstOrDefaultAsync(c => c.Id == contextId, ct);
        if (ctx is null) return null;
        if (ctx.DefaultResourceSet is null)
            return DefaultResourceEngine.EmptyResponse(Guid.Empty);

        ApplyKey(ctx.DefaultResourceSet, key, null);
        await db.SaveChangesAsync(ct);
        await InvalidateContextDefaultResourcesAsync(contextId, ct);
        return DefaultResourceEngine.ToResponse(
            DefaultResourceSetSnapshot.FromDefaultResourceSet(ctx.DefaultResourceSet));
    }

    // -- Helpers ----------------------------------------------------

    private async Task<DefaultResourceSetDB> CreateAndAttachAsync(
        Action<Guid> assignId, CancellationToken ct)
    {
        var drs = new DefaultResourceSetDB();
        db.DefaultResourceSets.Add(drs);
        await db.SaveChangesAsync(ct);
        assignId(drs.Id);
        return drs;
    }

    private void Apply(DefaultResourceSetDB drs, SetDefaultResourcesRequest request)
    {
        foreach (var (key, value) in request.Entries)
            ApplyKey(drs, key, value);
    }

    private void ApplyKey(DefaultResourceSetDB drs, string key, Guid? value)
    {
        var normalised = DefaultResourceEngine.NormalizeKey(key);
        var existing = drs.Entries.FirstOrDefault(
            e => string.Equals(e.ResourceKey, normalised, StringComparison.OrdinalIgnoreCase));

        if (value is null)
        {
            if (existing is not null)
            {
                drs.Entries.Remove(existing);
                db.DefaultResourceEntries.Remove(existing);
            }

            return;
        }

        if (existing is not null)
        {
            existing.ResourceId = value.Value;
        }
        else
        {
            drs.Entries.Add(new DefaultResourceEntryDB
            {
                DefaultResourceSetId = drs.Id,
                ResourceKey = normalised,
                ResourceId = value.Value
            });
        }
    }

    private async Task InvalidateContextDefaultResourcesAsync(
        Guid contextId, CancellationToken ct)
    {
        var channelIds = await db.Channels
            .Where(c => c.AgentContextId == contextId)
            .Select(c => c.Id)
            .ToListAsync(ct);

        foreach (var channelId in channelIds)
            chatCache.RemoveDefaultResourceResolutionForChannel(channelId);
    }

    private static DefaultResourceSetSnapshot? Snapshot(DefaultResourceSetDB? drs) =>
        drs is null ? null : DefaultResourceSetSnapshot.FromDefaultResourceSet(drs);
}
