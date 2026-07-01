using SharpClaw.Contracts.DTOs.Models;
using SharpClaw.Core.Providers;

namespace SharpClaw.Application.Services;

public sealed class ModelService(
    ProviderModelAdministrationEngine administration,
    EfProviderModelAdministrationHost administrationHost)
{
    public async Task<ModelResponse> CreateAsync(
        CreateModelRequest request,
        CancellationToken ct = default)
    {
        return await administration.CreateModelAsync(
            request,
            administrationHost,
            ct);
    }

    public async Task<IReadOnlyList<ModelResponse>> ListAsync(
        Guid? providerId = null,
        CancellationToken ct = default)
    {
        return await administration.ListModelsAsync(
            providerId,
            administrationHost,
            ct);
    }

    public async Task<ModelResponse?> GetByIdAsync(
        Guid id,
        CancellationToken ct = default)
    {
        return await administration.GetModelAsync(
            id,
            administrationHost,
            ct);
    }

    public async Task<ModelResponse?> UpdateAsync(
        Guid id,
        UpdateModelRequest request,
        CancellationToken ct = default)
    {
        return await administration.UpdateModelAsync(
            id,
            request,
            administrationHost,
            ct);
    }

    public async Task<bool> DeleteAsync(
        Guid id,
        CancellationToken ct = default)
    {
        return await administration.DeleteModelAsync(
            id,
            administrationHost,
            ct);
    }
}
