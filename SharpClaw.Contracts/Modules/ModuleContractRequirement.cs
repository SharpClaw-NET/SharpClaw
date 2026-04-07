namespace SharpClaw.Contracts.Modules;

/// <summary>
/// Declares a contract that a module depends on. The dependency is satisfied
/// by any loaded module that exports a <see cref="ModuleContractExport"/>
/// with the same <see cref="ContractName"/> and a compatible
/// <see cref="ServiceType"/>. This is contract-bound: the consuming module
/// does not name a specific provider module — any module that fits the
/// contract satisfies the dependency.
/// </summary>
public sealed record ModuleContractRequirement(
    /// <summary>
    /// Contract identifier that must be exported by some loaded module.
    /// Matched against <see cref="ModuleContractExport.ContractName"/>.
    /// </summary>
    string ContractName,

    /// <summary>
    /// The service interface type this module expects to resolve from DI.
    /// When non-null, validated for type compatibility against the provider's
    /// <see cref="ModuleContractExport.ServiceType"/> using
    /// <see cref="Type.IsAssignableFrom"/>. When <c>null</c>, the dependency
    /// is purely logical — the provider module must be loaded, but no
    /// specific DI service resolution is required.
    /// </summary>
    Type? ServiceType = null,

    /// <summary>
    /// If <c>true</c>, the module loads even when no provider exists.
    /// The module is expected to degrade gracefully when the contract is
    /// absent (e.g. skip optional features, return reduced results).
    /// </summary>
    bool Optional = false,

    /// <summary>Optional description for diagnostics and discoverability.</summary>
    string? Description = null
);
