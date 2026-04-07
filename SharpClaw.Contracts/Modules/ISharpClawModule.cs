using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

namespace SharpClaw.Contracts.Modules;

/// <summary>
/// Contract for a SharpClaw module. Implemented by each module assembly.
/// Discovered and loaded at startup.
/// </summary>
public interface ISharpClawModule
{
    /// <summary>Unique module identifier (e.g. "computer_use").</summary>
    string Id { get; }

    /// <summary>Human-readable name (e.g. "Computer Use").</summary>
    string DisplayName { get; }

    /// <summary>Tool name prefix. Must be unique across all loaded modules.</summary>
    string ToolPrefix { get; }

    /// <summary>
    /// Register services into the DI container.
    /// Called once at startup before any tool execution.
    /// </summary>
    void ConfigureServices(IServiceCollection services);

    /// <summary>
    /// Return all tool definitions this module exposes.
    /// Each definition includes the schema sent to the LLM.
    /// </summary>
    IReadOnlyList<ModuleToolDefinition> GetToolDefinitions();

    /// <summary>
    /// Contracts (service interfaces) this module provides to other modules.
    /// Any module declaring a <see cref="ModuleContractRequirement"/> with a
    /// matching <see cref="ModuleContractExport.ContractName"/> is considered
    /// a dependent and will be initialized after this module.
    /// </summary>
    IReadOnlyList<ModuleContractExport> ExportedContracts => [];

    /// <summary>
    /// Contracts this module depends on. Satisfied by any loaded module that
    /// exports a <see cref="ModuleContractExport"/> with the same
    /// <see cref="ModuleContractRequirement.ContractName"/>. Non-optional
    /// requirements that cannot be satisfied prevent the module from loading.
    /// </summary>
    IReadOnlyList<ModuleContractRequirement> RequiredContracts => [];

    /// <summary>
    /// Called once after the DI container is built but before the first HTTP
    /// request is served. Use for database migrations, warm-up, validation,
    /// or one-time setup that requires resolved services.
    /// If this method throws, the module is disabled for the current session
    /// and its manifest is set to <c>enabled=false</c> to prevent boot loops.
    /// </summary>
    Task InitializeAsync(IServiceProvider services, CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>
    /// Called once during graceful application shutdown
    /// (<see cref="Microsoft.Extensions.Hosting.IHostApplicationLifetime.ApplicationStopping"/>).
    /// Use to release unmanaged resources, flush caches, or cancel background work.
    /// </summary>
    Task ShutdownAsync() => Task.CompletedTask;

    /// <summary>
    /// Called once after a module is freshly installed and loaded for the first time.
    /// Use to insert seed data into the database. Subsequent launches skip this —
    /// only triggered when the <c>.seeded</c> marker file does not exist.
    /// </summary>
    Task SeedDataAsync(IServiceProvider services, CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>
    /// Execute a tool by name. Called from <c>DispatchExecutionAsync</c>
    /// when <c>ActionType</c> is <c>ModuleAction</c> and the envelope targets this module.
    /// </summary>
    Task<string> ExecuteToolAsync(
        string toolName,
        JsonElement parameters,
        AgentJobContext job,
        IServiceProvider scopedServices,
        CancellationToken ct);

    /// <summary>
    /// Optional. Return header tag definitions this module provides.
    /// Tags are expanded by <c>HeaderTagProcessor</c> in custom chat headers.
    /// </summary>
    IReadOnlyList<ModuleHeaderTag>? GetHeaderTags() => null;
}
