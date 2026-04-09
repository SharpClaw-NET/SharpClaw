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
    /// Return all job-pipeline tool definitions this module exposes.
    /// Each definition includes the schema sent to the LLM.
    /// These tools flow through the full AgentJobService lifecycle.
    /// </summary>
    IReadOnlyList<ModuleToolDefinition> GetToolDefinitions();

    /// <summary>
    /// Return inline tool definitions — lightweight tools that execute directly
    /// within the ChatService streaming loop without creating a job record.
    /// Use for fast, stateless operations (e.g. wait, list threads, read history).
    /// The host evaluates permissions from each tool's Permission descriptor
    /// before calling <see cref="ExecuteInlineToolAsync"/>.
    /// </summary>
    IReadOnlyList<ModuleInlineToolDefinition> GetInlineToolDefinitions() => [];

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
    /// Execute a job-pipeline tool by name. Called from <c>DispatchExecutionAsync</c>
    /// when the <c>ActionKey</c> resolves to this module via <c>ModuleRegistry</c>.
    /// </summary>
    Task<string> ExecuteToolAsync(
        string toolName,
        JsonElement parameters,
        AgentJobContext job,
        IServiceProvider scopedServices,
        CancellationToken ct);

    /// <summary>
    /// Execute an inline tool by name. Called directly from the ChatService
    /// streaming loop for tools declared in <see cref="GetInlineToolDefinitions"/>.
    /// Must be fast and lightweight — no job record is created.
    /// The host has already evaluated the tool's Permission descriptor
    /// before this call.
    /// </summary>
    Task<string> ExecuteInlineToolAsync(
        string toolName,
        JsonElement parameters,
        InlineToolContext context,
        IServiceProvider scopedServices,
        CancellationToken ct) =>
        throw new NotImplementedException(
            $"Module '{Id}' does not implement ExecuteInlineToolAsync for tool '{toolName}'.");

    /// <summary>
    /// Optional. Return header tag definitions this module provides.
    /// Tags are expanded by <c>HeaderTagProcessor</c> in custom chat headers.
    /// </summary>
    IReadOnlyList<ModuleHeaderTag>? GetHeaderTags() => null;

    /// <summary>
    /// Optional. Return CLI commands this module provides.
    /// Commands are registered in the CLI REPL at their declared
    /// <see cref="ModuleCliScope"/> (top-level verb or resource type).
    /// </summary>
    IReadOnlyList<ModuleCliCommand>? GetCliCommands() => null;
}
