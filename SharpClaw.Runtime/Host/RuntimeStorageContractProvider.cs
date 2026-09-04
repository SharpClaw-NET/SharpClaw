using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.Host;

/// <summary>Provides the storage contracts from the configured kernel composition.</summary>
internal sealed class RuntimeScopedStorageContractProvider : IStorageContractProvider
{
    private readonly IReadOnlyList<ScopedStorageContractDescriptor> _contracts;

    public RuntimeScopedStorageContractProvider(
        IEnumerable<ScopedStorageContractDescriptor> contracts)
    {
        ArgumentNullException.ThrowIfNull(contracts);

        _contracts = contracts
            .GroupBy(contract => (contract.SourceId, contract.StorageName))
            .Select(group => group.Count() == 1
                ? group.Single()
                : throw new InvalidOperationException(
                    $"Storage contract '{group.Key.SourceId}/{group.Key.StorageName}' was declared more than once."))
            .ToArray();
    }

    public IReadOnlyList<ScopedStorageContractDescriptor> GetStorageContracts() => _contracts;

    public ScopedStorageContractDescriptor? FindStorageContract(
        string SourceId,
        string storageName) =>
        _contracts.FirstOrDefault(contract =>
            string.Equals(contract.SourceId, SourceId, StringComparison.Ordinal)
            && string.Equals(contract.StorageName, storageName, StringComparison.Ordinal));

}
