using Microsoft.EntityFrameworkCore;
using SharpClaw.Core.Clients;
using SharpClaw.Core.Modules;
using SharpClaw.Core.Tasks.Models;
using SharpClaw.Core.Tasks.Preflight;
using SharpClaw.Contracts.Enums;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

/// <summary>
/// Builds host facts for task requirement checks and delegates the
/// requirement semantics to SharpClaw.Core.
/// </summary>
public sealed class TaskPreflightChecker(
    SharpClawDbContext db,
    ModuleRegistry moduleRegistry,
    ProviderApiClientFactory clientFactory,
    TaskPreflightEngine preflight)
{
    public TaskPreflightResult CheckStatic(
        IReadOnlyList<TaskRequirementDefinition> requirements)
        => preflight.CheckStatic(requirements);

    public async Task<TaskPreflightResult> CheckRuntimeAsync(
        IReadOnlyList<TaskRequirementDefinition> requirements,
        IReadOnlyDictionary<string, object?> paramValues,
        Guid? callerAgentId,
        CancellationToken ct = default)
    {
        var facts = await BuildRuntimeFactsAsync(callerAgentId, ct);
        return preflight.CheckRuntime(
            requirements,
            paramValues,
            facts,
            callerAgentId is not null);
    }

    private async Task<TaskPreflightRuntimeFacts> BuildRuntimeFactsAsync(
        Guid? callerAgentId,
        CancellationToken ct)
    {
        var providers = await BuildProviderStatesAsync(ct);
        var models = await BuildModelStatesAsync(ct);
        var enabledModuleIds = await BuildEnabledModuleSetAsync(ct);
        var callerPermissionFlags =
            await BuildCallerPermissionFlagsAsync(callerAgentId, ct);

        return new TaskPreflightRuntimeFacts(
            providers,
            models,
            enabledModuleIds,
            callerPermissionFlags);
    }

    private async Task<IReadOnlyList<TaskPreflightProviderState>>
        BuildProviderStatesAsync(CancellationToken ct)
    {
        var configuredProviders = await db.Providers
            .AsNoTracking()
            .Select(provider => new
            {
                provider.ProviderKey,
                provider.EncryptedApiKey
            })
            .ToListAsync(ct);

        return clientFactory.Plugins
            .Select(plugin =>
            {
                var configured = configuredProviders.FirstOrDefault(
                    provider => provider.ProviderKey == plugin.ProviderKey);
                return new TaskPreflightProviderState(
                    plugin.ProviderKey,
                    plugin.RequiresApiKey,
                    configured is not null,
                    configured?.EncryptedApiKey is not null);
            })
            .ToList();
    }

    private async Task<IReadOnlyList<TaskPreflightModelState>>
        BuildModelStatesAsync(CancellationToken ct)
    {
        var models = await db.Models
            .AsNoTracking()
            .Select(model => new
            {
                model.Id,
                model.Name,
                model.CustomId,
                model.CapabilityTagsRaw
            })
            .ToListAsync(ct);

        return models
            .Select(model => new TaskPreflightModelState(
                model.Id,
                model.Name,
                model.CustomId,
                ParseCapabilityTags(model.CapabilityTagsRaw)))
            .ToList();
    }

    private async Task<IReadOnlySet<string>> BuildEnabledModuleSetAsync(
        CancellationToken ct)
    {
        var enabledModuleIds = await db.ModuleStates
            .AsNoTracking()
            .Where(state => state.Enabled)
            .Select(state => state.ModuleId)
            .ToListAsync(ct);

        var result = new HashSet<string>(
            enabledModuleIds,
            StringComparer.Ordinal);

        foreach (var module in moduleRegistry.GetAllModules())
        {
            if (moduleRegistry.IsExternal(module.Id))
                result.Add(module.Id);
        }

        return result;
    }

    private async Task<IReadOnlySet<string>> BuildCallerPermissionFlagsAsync(
        Guid? callerAgentId,
        CancellationToken ct)
    {
        if (callerAgentId is null)
            return new HashSet<string>(StringComparer.Ordinal);

        var agent = await db.Agents
            .AsNoTracking()
            .Include(agent => agent.Role)
            .FirstOrDefaultAsync(agent => agent.Id == callerAgentId.Value, ct);

        if (agent?.Role?.PermissionSetId is not { } permissionSetId)
            return new HashSet<string>(StringComparer.Ordinal);

        var flags = await db.GlobalFlags
            .AsNoTracking()
            .Where(flag => flag.PermissionSetId == permissionSetId
                           && flag.Clearance != PermissionClearance.Unset)
            .Select(flag => flag.FlagKey)
            .ToListAsync(ct);

        return new HashSet<string>(flags, StringComparer.Ordinal);
    }

    private static IReadOnlySet<string> ParseCapabilityTags(string? tagsRaw)
    {
        return string.IsNullOrWhiteSpace(tagsRaw)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(
                tagsRaw.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
    }
}
