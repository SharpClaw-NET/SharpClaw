using SharpClaw.Application.Infrastructure.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Contracts.Entities.Core.Tasks;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Application.Services;

/// <summary>
/// Translates parsed <see cref="TaskTriggerDefinition"/> entries into
/// <see cref="TaskTriggerBindingDB"/> rows, delegating per-kind ownership
/// (cron scheduled jobs, OS shortcut files, etc.) to module-owned
/// <see cref="ITaskTriggerSource"/> implementations that opt into
/// <see cref="ITaskTriggerSource.OwnsBindingPersistence"/> or attach a
/// matching <see cref="ITaskTriggerBindingSideEffect"/>.
/// </summary>
public sealed class TaskTriggerRegistrar(
    SharpClawDbContext db,
    ILogger<TaskTriggerRegistrar> logger,
    ITaskTriggerSourceRegistry? sourceRegistry = null)
{
    /// <summary>
    /// Upsert all trigger bindings derived from <paramref name="triggers"/>.
    /// Stale rows (present in the DB but absent from the new list) are removed.
    /// Does not call <see cref="SharpClawDbContext.SaveChangesAsync"/> —
    /// the caller is responsible for the surrounding unit of work.
    /// After EF changes are staged this method signals the host service so it
    /// reloads active sources.
    /// </summary>
    /// <returns><see langword="true"/> if any EF change-tracked rows were added or removed.</returns>
    public async Task<bool> SyncTriggersAsync(
        TaskDefinitionDB entity,
        IReadOnlyList<TaskTriggerDefinition> triggers,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(triggers);

        var ownedChanged   = await DispatchOwnedBindingsAsync(entity, triggers, ct);
        var bindingChanged = await SyncBindingRowsAsync(entity, triggers, ct);
        return ownedChanged || bindingChanged;
    }

    /// <summary>Remove all trigger bindings for a definition, including any
    /// owned-persistence state contributed by module-owned sources.</summary>
    public async Task RemoveTriggersAsync(Guid definitionId, CancellationToken ct = default)
    {
        await RemoveBindingRowsAsync(definitionId, ct);
        await RemoveOwnedBindingsAsync(definitionId, ct);
    }

    // ─────────────────────────────────────────────────────────────
    // Module-owned persistence dispatch
    // ─────────────────────────────────────────────────────────────

    private async Task<bool> DispatchOwnedBindingsAsync(
        TaskDefinitionDB entity,
        IReadOnlyList<TaskTriggerDefinition> triggers,
        CancellationToken ct)
    {
        if (sourceRegistry is null) return false;

        var changed = false;
        var grouped = triggers
            .Where(t => !string.IsNullOrWhiteSpace(t.TriggerKey))
            .GroupBy(t => sourceRegistry.ResolveByKey(t.TriggerKey))
            .Where(g => g.Key is not null && g.Key.OwnsBindingPersistence);

        var descriptor = new TaskDefinitionDescriptor(entity.Id, entity.Name);

        foreach (var group in grouped)
        {
            var source = group.Key!;
            var owned = group.ToList();
            var sourceChanged = await source.SyncBindingsAsync(descriptor, owned, ct);
            if (sourceChanged) changed = true;
        }

        return changed;
    }

    private async Task RemoveOwnedBindingsAsync(Guid definitionId, CancellationToken ct)
    {
        if (sourceRegistry is null) return;

        foreach (var source in sourceRegistry.Sources)
        {
            if (!source.OwnsBindingPersistence) continue;
            await source.RemoveBindingsAsync(definitionId, ct);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Default binding-row CRUD
    // ─────────────────────────────────────────────────────────────

    private async Task<bool> SyncBindingRowsAsync(
        TaskDefinitionDB entity,
        IReadOnlyList<TaskTriggerDefinition> triggers,
        CancellationToken ct)
    {
        // Skip triggers whose owning source manages persistence itself.
        var defaultPath = triggers
            .Where(t => !IsOwnedBySource(t.TriggerKey))
            .ToList();

        var existing = await db.TaskTriggerBindings
            .Where(b => b.TaskDefinitionId == entity.Id)
            .ToListAsync(ct);

        // Build a lookup key for each incoming trigger
        var incomingKeys = defaultPath
            .Select(t => BindingKey(entity.Id, t))
            .ToHashSet();

        var changed = false;

        // Remove stale bindings (default-path rows only; owned-source state
        // is reconciled by the source itself in SyncBindingsAsync).
        foreach (var stale in existing.Where(b => !IsOwnedBySource(b.Kind) && !incomingKeys.Contains(BindingKey(b))))
        {
            await FireRemovedSideEffectAsync(stale, ct);
            db.TaskTriggerBindings.Remove(stale);
            changed = true;
            logger.LogDebug(
                "Removed stale trigger binding for definition {DefinitionId}, kind {Kind}.",
                entity.Id, stale.Kind);
        }

        // Upsert new bindings
        var existingKeys = existing
            .ToDictionary(b => BindingKey(b));

        foreach (var t in defaultPath)
        {
            var key = BindingKey(entity.Id, t);
            if (existingKeys.ContainsKey(key))
                continue; // already present

            var kindColumn = t.TriggerKey ?? string.Empty;
            var binding = new TaskTriggerBindingDB
            {
                TaskDefinitionId = entity.Id,
                Kind             = kindColumn,
                TriggerValue     = TriggerValueFor(t),
                Filter           = TriggerFilterFor(t),
                DefinitionJson   = System.Text.Json.JsonSerializer.Serialize(t),
            };
            db.TaskTriggerBindings.Add(binding);
            changed = true;

            await FireCreatedSideEffectAsync(entity, t, binding, ct);

            logger.LogDebug(
                "Created trigger binding for definition {DefinitionId}, kind {Kind}.",
                entity.Id, kindColumn);
        }

        return changed;
    }

    private async Task RemoveBindingRowsAsync(Guid definitionId, CancellationToken ct)
    {
        var bindings = await db.TaskTriggerBindings
            .Where(b => b.TaskDefinitionId == definitionId)
            .ToListAsync(ct);

        foreach (var b in bindings)
            await FireRemovedSideEffectAsync(b, ct);

        db.TaskTriggerBindings.RemoveRange(bindings);
    }

    private bool IsOwnedBySource(string? triggerKey)
    {
        if (string.IsNullOrWhiteSpace(triggerKey)) return false;
        var source = sourceRegistry?.ResolveByKey(triggerKey);
        return source is { OwnsBindingPersistence: true };
    }

    private async Task FireCreatedSideEffectAsync(
        TaskDefinitionDB entity,
        TaskTriggerDefinition trigger,
        TaskTriggerBindingDB binding,
        CancellationToken ct)
    {
        var sideEffect = sourceRegistry?.ResolveSideEffect(binding.Kind);
        if (sideEffect is null) return;

        await sideEffect.OnBindingCreatedAsync(
            new TaskDefinitionDescriptor(entity.Id, entity.Name),
            trigger,
            new TaskTriggerBindingDescriptor(
                binding.TaskDefinitionId,
                binding.Kind,
                binding.TriggerValue,
                binding.Filter),
            ct);
    }

    private async Task FireRemovedSideEffectAsync(
        TaskTriggerBindingDB binding,
        CancellationToken ct)
    {
        var sideEffect = sourceRegistry?.ResolveSideEffect(binding.Kind);
        if (sideEffect is null) return;

        await sideEffect.OnBindingRemovedAsync(
            new TaskTriggerBindingDescriptor(
                binding.TaskDefinitionId,
                binding.Kind,
                binding.TriggerValue,
                binding.Filter),
            ct);
    }

    // ─────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────

    private string BindingKey(Guid definitionId, TaskTriggerDefinition t) =>
        $"{definitionId}|{t.TriggerKey}|{TriggerValueFor(t)}";

    private static string BindingKey(TaskTriggerBindingDB b) =>
        $"{b.TaskDefinitionId}|{b.Kind}|{b.TriggerValue}";

    /// <summary>
    /// Computes the <c>TriggerValue</c> column for a binding by delegating to
    /// the owning <see cref="ITaskTriggerSource"/>. Returns <see langword="null"/>
    /// when no source is registered for the key (the binding row will then
    /// carry a null discriminator).
    /// </summary>
    private string? TriggerValueFor(TaskTriggerDefinition t) =>
        sourceRegistry?.ResolveByKey(t.TriggerKey)?.GetBindingValue(t);

    private string? TriggerFilterFor(TaskTriggerDefinition t) =>
        sourceRegistry?.ResolveByKey(t.TriggerKey)?.GetBindingFilter(t);
}
