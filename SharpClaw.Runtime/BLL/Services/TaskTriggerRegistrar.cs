using SharpClaw.Core.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Contracts.Entities.Core.Tasks;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Core.Tasks.Triggers;

namespace SharpClaw.Runtime.BLL.Services;

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
    TaskTriggerBindingPlanner bindingPlanner,
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

        var existing = await db.TaskTriggerBindings
            .Where(b => b.TaskDefinitionId == entity.Id)
            .ToListAsync(ct);
        var plan = bindingPlanner.BuildSyncPlan(
            new TaskTriggerBindingSyncRequest(
                new TaskDefinitionDescriptor(entity.Id, entity.Name),
                triggers,
                existing.Select(ToSnapshot).ToList(),
                sourceRegistry));

        var ownedChanged = await DispatchOwnedBindingsAsync(
            new TaskDefinitionDescriptor(entity.Id, entity.Name),
            plan.OwnedSourceSyncs,
            ct);
        var bindingChanged = await ApplyBindingPlanAsync(
            entity,
            existing,
            plan,
            ct);
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

    private static async Task<bool> DispatchOwnedBindingsAsync(
        TaskDefinitionDescriptor descriptor,
        IReadOnlyList<TaskTriggerOwnedSourceSync> syncs,
        CancellationToken ct)
    {
        var changed = false;
        foreach (var sync in syncs)
        {
            var sourceChanged = await sync.Source.SyncBindingsAsync(
                descriptor,
                sync.Triggers,
                ct);
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

    private async Task<bool> ApplyBindingPlanAsync(
        TaskDefinitionDB entity,
        IReadOnlyList<TaskTriggerBindingDB> existing,
        TaskTriggerBindingSyncPlan plan,
        CancellationToken ct)
    {
        var changed = false;

        var staleKeys = plan.DefaultBindingsToRemove
            .Select(TaskTriggerBindingPlanner.BindingKey)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var stale in existing.Where(binding =>
            staleKeys.Contains(TaskTriggerBindingPlanner.BindingKey(
                ToSnapshot(binding)))))
        {
            await FireRemovedSideEffectAsync(stale, ct);
            db.TaskTriggerBindings.Remove(stale);
            changed = true;
            logger.LogDebug(
                "Removed stale trigger binding for definition {DefinitionId}, kind {Kind}.",
                entity.Id, stale.Kind);
        }

        foreach (var creation in plan.DefaultBindingsToCreate)
        {
            var binding = new TaskTriggerBindingDB
            {
                TaskDefinitionId = entity.Id,
                Kind             = creation.Kind,
                TriggerValue     = creation.TriggerValue,
                Filter           = creation.Filter,
                DefinitionJson   = creation.DefinitionJson,
            };
            db.TaskTriggerBindings.Add(binding);
            changed = true;

            await FireCreatedSideEffectAsync(
                entity,
                creation.Trigger,
                creation.ToDescriptor(),
                ct);

            logger.LogDebug(
                "Created trigger binding for definition {DefinitionId}, kind {Kind}.",
                entity.Id, creation.Kind);
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

    private async Task FireCreatedSideEffectAsync(
        TaskDefinitionDB entity,
        TaskTriggerDefinition trigger,
        TaskTriggerBindingDescriptor binding,
        CancellationToken ct)
    {
        var sideEffect = sourceRegistry?.ResolveSideEffect(binding.Kind);
        if (sideEffect is null) return;

        await sideEffect.OnBindingCreatedAsync(
            new TaskDefinitionDescriptor(entity.Id, entity.Name),
            trigger,
            binding,
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

    private static TaskTriggerBindingSnapshot ToSnapshot(
        TaskTriggerBindingDB binding) =>
        new(
            binding.TaskDefinitionId,
            binding.Kind,
            binding.TriggerValue,
            binding.Filter);
}
