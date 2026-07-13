using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Clients;
using SharpClaw.Core.Modules;
using SharpClaw.Core.Tasks.Preflight;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.BLL.Services;

public sealed class EfTaskPreflightHost(
    SharpClawDbContext db,
    ModuleRegistry moduleRegistry,
    ProviderApiClientFactory clientFactory) : ITaskPreflightHost
{
    public IEnumerable<IProviderPlugin> ProviderPlugins => clientFactory.Plugins;

    public async Task<IReadOnlyList<TaskPreflightConfiguredProvider>> ListConfiguredProvidersAsync(
        CancellationToken ct)
    {
        return await db.Providers
            .AsNoTracking()
            .Select(provider => new TaskPreflightConfiguredProvider(
                provider.ProviderKey,
                provider.EncryptedApiKey))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TaskPreflightConfiguredModel>> ListConfiguredModelsAsync(
        CancellationToken ct)
    {
        return await db.Models
            .AsNoTracking()
            .Select(model => new TaskPreflightConfiguredModel(
                model.Id,
                model.Name,
                model.CustomId,
                model.CapabilityTagsRaw))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlySet<string>> ListPersistedEnabledModuleIdsAsync(
        CancellationToken ct)
    {
        var enabledModuleIds = await db.ModuleStates
            .AsNoTracking()
            .Where(state => state.Enabled)
            .Select(state => state.ModuleId)
            .ToListAsync(ct);

        return new HashSet<string>(
            enabledModuleIds,
            StringComparer.Ordinal);
    }

    public Task<IReadOnlySet<string>> ListRuntimeEnabledModuleIdsAsync(
        CancellationToken ct)
    {
        IReadOnlySet<string> moduleIds = moduleRegistry.GetAllModules()
            .Where(module => moduleRegistry.IsExternal(module.Id))
            .Select(module => module.Id)
            .ToHashSet(StringComparer.Ordinal);
        return Task.FromResult(moduleIds);
    }

    public async Task<IReadOnlySet<string>> ListCallerPermissionFlagsAsync(
        Guid callerAgentId,
        CancellationToken ct)
    {
        var agent = await db.Agents
            .AsNoTracking()
            .Include(candidate => candidate.Role)
            .FirstOrDefaultAsync(candidate => candidate.Id == callerAgentId, ct);

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
}
