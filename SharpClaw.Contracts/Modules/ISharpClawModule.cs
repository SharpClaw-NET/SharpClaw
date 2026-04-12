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
    /// Optional. Return resource type descriptors this module owns.
    /// Used by the host to build grant labels for chat headers and to
    /// resolve wildcard grants (AllResources) into concrete resource IDs.
    /// </summary>
    IReadOnlyList<ModuleResourceTypeDescriptor> GetResourceTypeDescriptors() => [];

    /// <summary>
    /// Optional. Return global flag descriptors this module owns.
    /// These replace the hardcoded boolean properties on <c>PermissionSetDB</c>.
    /// See Module-System-Design §12.4.1.
    /// </summary>
    IReadOnlyList<ModuleGlobalFlagDescriptor> GetGlobalFlagDescriptors() => [];

    /// <summary>
    /// Optional. Return UI contribution descriptors for this module.
    /// Client applications use these to render module-specific UI elements
    /// at designated contribution points (e.g. <c>"chat_input_actions"</c>,
    /// <c>"settings_sidebar"</c>).
    /// </summary>
    IReadOnlyList<ModuleUiContribution> GetUiContributions() => [];

    /// <summary>
    /// Optional. Return CLI commands this module provides.
    /// Commands are registered in the CLI REPL at their declared
    /// <see cref="ModuleCliScope"/> (top-level verb or resource type).
    /// </summary>
    IReadOnlyList<ModuleCliCommand>? GetCliCommands() => null;

    /// <summary>
    /// Optional. Map HTTP endpoints owned by this module.
    /// Called during endpoint mapping in <c>Program.cs</c> for all enabled modules.
    /// <para>
    /// The <paramref name="app"/> parameter is always an
    /// <c>IEndpointRouteBuilder</c>. It is typed as <see cref="object"/> because
    /// <c>SharpClaw.Contracts</c> cannot reference ASP.NET Core (it is consumed
    /// by mobile/wasm client targets). Module implementations should cast:
    /// <code>var endpoints = (IEndpointRouteBuilder)app;</code>
    /// </para>
    /// <para>
    /// Module endpoints should use a route group filter that checks
    /// <c>ModuleRegistry.GetModule(Id)</c> and returns <c>503 Service Unavailable</c>
    /// when the module is disabled at runtime (routes cannot be removed after
    /// <c>app.Build()</c>).
    /// </para>
    /// </summary>
    /// <param name="app">An <c>IEndpointRouteBuilder</c> instance for registering routes.</param>
    void MapEndpoints(object app) { }

    /// <summary>
    /// Optional periodic health check. Called by the host on a configurable
    /// interval (default: every 60 seconds). Return a healthy status if the
    /// module's dependencies (external APIs, COM objects, file handles,
    /// database connections) are operational.
    /// <para>
    /// The default implementation always returns healthy. Modules that depend
    /// on external resources should override this to verify connectivity.
    /// </para>
    /// <para>
    /// Modules that fail consecutive checks (default threshold: 3) are
    /// automatically disabled. The admin is notified via structured logging
    /// and the <c>/modules/{id}/health</c> endpoint.
    /// </para>
    /// </summary>
    Task<ModuleHealthStatus> HealthCheckAsync(CancellationToken ct) =>
        Task.FromResult(new ModuleHealthStatus(IsHealthy: true));

    /// <summary>
    /// Optional streaming variant of <see cref="ExecuteToolAsync"/>. When a module
    /// implements this for a given tool name, the pipeline prefers it over the
    /// non-streaming variant and forwards each yielded string to the client as a
    /// partial result via SSE <c>tool_output.delta</c> events.
    /// <para>
    /// The final concatenated result of all yielded strings is stored in
    /// <c>job.ResultData</c> for audit and history, identical to the
    /// non-streaming path.
    /// </para>
    /// <para>
    /// Return <c>null</c> for tool names that do not support streaming —
    /// the pipeline falls back to <see cref="ExecuteToolAsync"/>.
    /// </para>
    /// </summary>
    IAsyncEnumerable<string>? ExecuteToolStreamingAsync(
        string toolName,
        JsonElement parameters,
        AgentJobContext job,
        IServiceProvider scopedServices,
        CancellationToken ct) => null;
}
