using System.Text.Json;
using SharpClaw.Application.Core.Modules;

namespace SharpClaw.Application.API;

public sealed class BundledModuleStorageGateway : IModuleStorageGateway
{
    public IReadOnlyList<ModuleStorageContractDescriptor> ListContracts() => [];

    public Task<JsonElement> InvokeAsync(
        string moduleId,
        string storageName,
        string operation,
        JsonElement parameters,
        CancellationToken ct = default) =>
        throw new NotSupportedException(
            $"Module storage '{moduleId}/{storageName}' is not registered.");
}
