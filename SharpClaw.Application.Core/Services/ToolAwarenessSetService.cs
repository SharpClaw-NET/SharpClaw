using SharpClaw.Contracts.DTOs.Tools;
using SharpClaw.Core.Tools;

namespace SharpClaw.Application.Services;

public sealed class ToolAwarenessSetService(
    ToolAwarenessAdministrationEngine administration,
    EfToolAwarenessAdministrationHost administrationHost)
{
    public async Task<ToolAwarenessSetResponse> CreateAsync(
        CreateToolAwarenessSetRequest request,
        CancellationToken ct = default)
    {
        return await administration.CreateAsync(
            request,
            administrationHost,
            ct);
    }

    public async Task<ToolAwarenessSetResponse?> GetByIdAsync(
        Guid id,
        CancellationToken ct = default)
    {
        return await administration.GetByIdAsync(
            id,
            administrationHost,
            ct);
    }

    public async Task<IReadOnlyList<ToolAwarenessSetResponse>> ListAsync(
        CancellationToken ct = default)
    {
        return await administration.ListAsync(
            administrationHost,
            ct);
    }

    public async Task<ToolAwarenessSetResponse?> UpdateAsync(
        Guid id,
        UpdateToolAwarenessSetRequest request,
        CancellationToken ct = default)
    {
        return await administration.UpdateAsync(
            id,
            request,
            administrationHost,
            ct);
    }

    public async Task<bool> DeleteAsync(
        Guid id,
        CancellationToken ct = default)
    {
        return await administration.DeleteAsync(
            id,
            administrationHost,
            ct);
    }
}
