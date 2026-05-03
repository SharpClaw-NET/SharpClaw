using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.DTOs.DefaultResources;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

/// <summary>
/// Manages <see cref="DefaultResourceSetDB"/> entities attached to
/// channels and contexts.  The set of valid resource keys is owned
/// entirely by registered modules via
/// <c>ModuleResourceTypeDescriptor.DefaultResourceKey</c>.
/// </summary>
public sealed class DefaultResourceSetService(SharpClawDbContext db, ModuleRegistry moduleRegistry)
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

        return Merge(ch.Id, ch.DefaultResourceSet, ch.AgentContext?.DefaultResourceSet);
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
            ? ToResponse(drs)
            : EmptyResponse(Guid.Empty);
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
        return ToResponse(ch.DefaultResourceSet);
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
        return ToResponse(ctx.DefaultResourceSet);
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
        return ToResponse(ch.DefaultResourceSet);
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
        if (ch.DefaultResourceSet is null) return EmptyResponse(Guid.Empty);

        ApplyKey(ch.DefaultResourceSet, key, null);
        await db.SaveChangesAsync(ct);
        return ToResponse(ch.DefaultResourceSet);
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
        return ToResponse(ctx.DefaultResourceSet);
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
        if (ctx.DefaultResourceSet is null) return EmptyResponse(Guid.Empty);

        ApplyKey(ctx.DefaultResourceSet, key, null);
        await db.SaveChangesAsync(ct);
        return ToResponse(ctx.DefaultResourceSet);
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

    private static void Apply(DefaultResourceSetDB drs, SetDefaultResourcesRequest request)
    {
        foreach (var (key, value) in request.Entries)
            ApplyKey(drs, key, value);
    }

    private static void ApplyKey(DefaultResourceSetDB drs, string key, Guid? value)
    {
        var normalised = key.ToLowerInvariant();
        var existing = drs.Entries.FirstOrDefault(
            e => string.Equals(e.ResourceKey, normalised, StringComparison.OrdinalIgnoreCase));

        if (value is null)
        {
            if (existing is not null)
                drs.Entries.Remove(existing);
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

    private static DefaultResourcesResponse ToResponse(DefaultResourceSetDB drs)
    {
        var entries = drs.Entries.ToDictionary(
            e => e.ResourceKey,
            e => e.ResourceId,
            StringComparer.OrdinalIgnoreCase);
        return new(drs.Id, entries);
    }

    private static DefaultResourcesResponse EmptyResponse(Guid id) =>
        new(id, new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Merges channel and context default resource sets.
    /// Channel values take precedence; context fills in any gaps.
    /// </summary>
    private static DefaultResourcesResponse Merge(
        Guid primaryId,
        DefaultResourceSetDB? ch,
        DefaultResourceSetDB? ctx)
    {
        if (ch is null && ctx is null)
            return EmptyResponse(Guid.Empty);

        if (ch is null) return ToResponse(ctx!);
        if (ctx is null) return ToResponse(ch);

        var merged = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in ctx.Entries)
            merged[e.ResourceKey] = e.ResourceId;

        // Channel overrides context.
        foreach (var e in ch.Entries)
            merged[e.ResourceKey] = e.ResourceId;

        return new(primaryId, merged);
    }
}
