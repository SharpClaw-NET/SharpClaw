using SharpClaw.Runtime.BLL.Modules;
using SharpClaw.Contracts.DTOs.DefaultResources;
using SharpClaw.Core.Modules;
using SharpClaw.Core.Resources;

namespace SharpClaw.Runtime.BLL.Services;

/// <summary>
/// Manages default resources attached to channels and contexts.
/// </summary>
public sealed class DefaultResourceSetService(
    ModuleRegistry moduleRegistry,
    DefaultResourceAdministrationEngine administration,
    EfDefaultResourceAdministrationHost administrationHost)
{
    public async Task<DefaultResourcesResponse?> GetForChannelAsync(
        Guid channelId,
        CancellationToken ct = default)
    {
        return await administration.GetForChannelAsync(
            channelId,
            administrationHost,
            ct);
    }

    public async Task<DefaultResourcesResponse?> GetForContextAsync(
        Guid contextId,
        CancellationToken ct = default)
    {
        return await administration.GetForContextAsync(
            contextId,
            administrationHost,
            ct);
    }

    public async Task<DefaultResourcesResponse?> SetForChannelAsync(
        Guid channelId,
        SetDefaultResourcesRequest request,
        CancellationToken ct = default)
    {
        return await administration.SetForChannelAsync(
            channelId,
            request,
            administrationHost,
            ct);
    }

    public async Task<DefaultResourcesResponse?> SetForContextAsync(
        Guid contextId,
        SetDefaultResourcesRequest request,
        CancellationToken ct = default)
    {
        return await administration.SetForContextAsync(
            contextId,
            request,
            administrationHost,
            ct);
    }

    public bool IsValidKey(string key) =>
        moduleRegistry.IsRegisteredDefaultResourceKey(key);

    public async Task<DefaultResourcesResponse?> SetKeyForChannelAsync(
        Guid channelId,
        string key,
        Guid resourceId,
        CancellationToken ct = default)
    {
        return await administration.SetKeyForChannelAsync(
            channelId,
            key,
            resourceId,
            administrationHost,
            ct);
    }

    public async Task<DefaultResourcesResponse?> ClearKeyForChannelAsync(
        Guid channelId,
        string key,
        CancellationToken ct = default)
    {
        return await administration.ClearKeyForChannelAsync(
            channelId,
            key,
            administrationHost,
            ct);
    }

    public async Task<DefaultResourcesResponse?> SetKeyForContextAsync(
        Guid contextId,
        string key,
        Guid resourceId,
        CancellationToken ct = default)
    {
        return await administration.SetKeyForContextAsync(
            contextId,
            key,
            resourceId,
            administrationHost,
            ct);
    }

    public async Task<DefaultResourcesResponse?> ClearKeyForContextAsync(
        Guid contextId,
        string key,
        CancellationToken ct = default)
    {
        return await administration.ClearKeyForContextAsync(
            contextId,
            key,
            administrationHost,
            ct);
    }
}
