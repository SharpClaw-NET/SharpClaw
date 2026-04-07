namespace SharpClaw.Contracts.Modules;

/// <summary>
/// Describes a service contract that a module provides to other modules.
/// The providing module must register an implementation of <see cref="ServiceType"/>
/// in DI via <see cref="ISharpClawModule.ConfigureServices"/>.
/// Any module that declares a <see cref="ModuleContractRequirement"/> with
/// the same <see cref="ContractName"/> is considered a dependent and will be
/// initialized after this module.
/// </summary>
/// <remarks>
/// Contract interfaces should live in shared assemblies (e.g.
/// <c>SharpClaw.Contracts</c>) so that both provider and consumer modules
/// reference the same CLR type. Assemblies loaded from the default
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> are shared across
/// all module load contexts, ensuring type identity.
/// </remarks>
public sealed record ModuleContractExport(
    /// <summary>
    /// Unique contract identifier (e.g. <c>"desktop_capture"</c>).
    /// Format: <c>^[a-z][a-z0-9_]{0,59}$</c> — lowercase alphanumeric
    /// plus underscores, starting with a letter, max 60 characters.
    /// Only one module may export a given contract name at a time.
    /// </summary>
    string ContractName,

    /// <summary>
    /// The service interface type registered in DI by the providing module
    /// (e.g. <c>typeof(IDesktopCapture)</c>). Consuming modules resolve
    /// this type from their scoped <see cref="IServiceProvider"/>.
    /// </summary>
    Type ServiceType,

    /// <summary>Optional human-readable description for discoverability.</summary>
    string? Description = null
);
