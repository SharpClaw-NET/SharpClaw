using System.Text.Json;

namespace SharpClaw.Application.Core.Modules;

public interface IModuleStorageGateway
{
    IReadOnlyList<ModuleStorageContractDescriptor> ListContracts();

    Task<JsonElement> InvokeAsync(
        string moduleId,
        string storageName,
        string operation,
        JsonElement parameters,
        CancellationToken ct = default);
}

public sealed record ModuleStorageContractDescriptor(
    string ModuleId,
    string StorageName,
    IReadOnlyList<ModuleStorageOperationDescriptor> Operations,
    string? Description = null);

public sealed record ModuleStorageOperationDescriptor(
    string Name,
    string? Description = null);
