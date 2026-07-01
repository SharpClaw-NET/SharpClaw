using SharpClaw.Contracts.DTOs.Models;
using SharpClaw.Contracts.DTOs.Providers;
using SharpClaw.Core.Providers;

namespace SharpClaw.Application.Services;

public sealed class ProviderService(
    ProviderModelAdministrationEngine administration,
    EfProviderModelAdministrationHost administrationHost)
{
    public async Task<ProviderResponse> CreateAsync(
        CreateProviderRequest request,
        CancellationToken ct = default)
    {
        return await administration.CreateProviderAsync(
            request,
            administrationHost,
            ct);
    }

    public IReadOnlyList<ProviderTypeResponse> ListAvailableTypes()
    {
        return administration.ListAvailableTypes(administrationHost);
    }

    public async Task<IReadOnlyList<ProviderResponse>> ListAsync(
        CancellationToken ct = default)
    {
        return await administration.ListProvidersAsync(
            administrationHost,
            ct);
    }

    public async Task<ProviderResponse?> GetByIdAsync(
        Guid id,
        CancellationToken ct = default)
    {
        return await administration.GetProviderAsync(
            id,
            administrationHost,
            ct);
    }

    public async Task<ProviderResponse?> UpdateAsync(
        Guid id,
        UpdateProviderRequest request,
        CancellationToken ct = default)
    {
        return await administration.UpdateProviderAsync(
            id,
            request,
            administrationHost,
            ct);
    }

    public async Task<bool> DeleteAsync(
        Guid id,
        CancellationToken ct = default)
    {
        return await administration.DeleteProviderAsync(
            id,
            administrationHost,
            ct);
    }

    /// <summary>
    /// Sets the API key for an existing provider.
    /// </summary>
    public async Task SetApiKeyAsync(
        Guid providerId,
        string apiKey,
        CancellationToken ct = default)
    {
        await administration.SetProviderApiKeyAsync(
            providerId,
            apiKey,
            administrationHost,
            ct);
    }

    /// <summary>
    /// Starts a device code flow for a provider that supports it.
    /// Returns the session containing the user code and verification URI.
    /// </summary>
    public async Task<DeviceCodeSession> StartDeviceCodeFlowAsync(
        Guid providerId,
        CancellationToken ct = default)
    {
        return await administration.StartDeviceCodeFlowAsync(
            providerId,
            administrationHost,
            ct);
    }

    /// <summary>
    /// Polls for the device code flow to complete and stores the resulting access token.
    /// </summary>
    public async Task CompleteDeviceCodeFlowAsync(
        Guid providerId,
        DeviceCodeSession session,
        CancellationToken ct = default)
    {
        await administration.CompleteDeviceCodeFlowAsync(
            providerId,
            session,
            administrationHost,
            ct);
    }

    /// <summary>
    /// Returns true if the given provider key supports device code authentication.
    /// </summary>
    public bool SupportsDeviceCodeAuth(
        string providerKey,
        string? apiEndpoint = null)
    {
        return administration.SupportsDeviceCodeAuth(
            providerKey,
            administrationHost);
    }

    /// <summary>
    /// Re-infers model capabilities for all models belonging to a provider.
    /// </summary>
    public async Task<int> RefreshCapabilitiesAsync(
        Guid providerId,
        CancellationToken ct = default)
    {
        return await administration.RefreshCapabilitiesAsync(
            providerId,
            administrationHost,
            ct);
    }

    /// <summary>
    /// Queries the provider's API for available models and upserts them.
    /// </summary>
    public async Task<IReadOnlyList<ModelResponse>> SyncModelsAsync(
        Guid providerId,
        CancellationToken ct = default)
    {
        return await administration.SyncModelsAsync(
            providerId,
            administrationHost,
            ct);
    }
}
