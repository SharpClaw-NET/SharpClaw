using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.DependencyInjection;

using Mk8.Shell.Engine;
using Mk8.Shell.Models;
using Mk8.Shell.Safety;
using Mk8.Shell.Startup;
using SharpClaw.Contracts.DTOs.Containers;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Application.Services;

namespace SharpClaw.Modules.Mk8Shell;

/// <summary>
/// Module for sandboxed mk8.shell script execution and sandbox container lifecycle.
/// </summary>
public sealed class Mk8ShellModule : ISharpClawModule
{
    public string Id => "sharpclaw_mk8shell";
    public string DisplayName => "mk8.shell";
    public string ToolPrefix => "mk8";

    public void ConfigureServices(IServiceCollection services)
    {
        // No DI registrations needed — mk8.shell is self-contained.
    }

    public IReadOnlyList<ModuleContractExport> ExportedContracts => [];

    public IReadOnlyList<ModuleCliCommand>? GetCliCommands() => null;

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() =>
    [
        new("execute_mk8_shell",
            LoadEmbeddedResource("tool-description.md"),
            BuildMk8ShellToolSchema(),
            new ModuleToolPermission(
                IsPerResource: true, Check: null, DelegateTo: "AccessContainerAsync"),
            TimeoutSeconds: 300),

        new("create_mk8_sandbox",
            "Create an mk8.shell sandbox container. Name must be alphanumeric.",
            BuildCreateSandboxSchema(),
            new ModuleToolPermission(
                IsPerResource: false, Check: null, DelegateTo: "CreateContainerAsync"))
    ];

    public async Task<string> ExecuteToolAsync(
        string toolName,
        JsonElement parameters,
        AgentJobContext context,
        IServiceProvider scopedServices,
        CancellationToken ct)
    {
        return toolName switch
        {
            "execute_mk8_shell" => await ExecuteMk8ShellAsync(parameters, context, scopedServices, ct),
            "create_mk8_sandbox" => await CreateSandboxAsync(parameters, context, scopedServices, ct),
            _ => throw new NotSupportedException($"Unknown tool: {toolName}")
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // TOOL EXECUTION HANDLERS
    // ═══════════════════════════════════════════════════════════════

    private async Task<string> ExecuteMk8ShellAsync(
        JsonElement parameters,
        AgentJobContext context,
        IServiceProvider scopedServices,
        CancellationToken ct)
    {
        var containerService = scopedServices.GetRequiredService<ContainerService>();

        var resourceIdStr = parameters.TryGetProperty("resourceId", out var rid) ? rid.GetString() : null;
        var sandboxId = parameters.TryGetProperty("sandboxId", out var sid) ? sid.GetString() : null;

        if (!parameters.TryGetProperty("script", out var scriptElement))
            throw new InvalidOperationException("Safe shell requires a 'script' parameter.");

        // Resolve container by resourceId or sandboxId
        ContainerResponse? container;
        if (!string.IsNullOrWhiteSpace(resourceIdStr) && Guid.TryParse(resourceIdStr, out var resourceGuid))
        {
            container = await containerService.GetByIdAsync(resourceGuid, ct)
                ?? throw new InvalidOperationException($"Container {resourceGuid} not found.");
        }
        else if (!string.IsNullOrWhiteSpace(sandboxId))
        {
            container = await containerService.GetBySandboxNameAsync(sandboxId, ct)
                ?? throw new InvalidOperationException($"Sandbox '{sandboxId}' not found.");
        }
        else
        {
            throw new InvalidOperationException("Safe shell requires either 'resourceId' or 'sandboxId'.");
        }

        if (container.Type != ContainerType.Mk8Shell)
            throw new InvalidOperationException($"Container '{container.Name}' is not an mk8shell container.");

        if (string.IsNullOrWhiteSpace(container.SandboxName))
            throw new InvalidOperationException($"Container '{container.Name}' has no sandbox name configured.");

        var script = JsonSerializer.Deserialize<Mk8ShellScript>(
            scriptElement.GetRawText(),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            })
            ?? throw new InvalidOperationException("Failed to deserialise mk8.shell script.");

        // mk8.shell is self-contained: pass the sandbox name and it
        // resolves everything from its own local registry.
        await using var taskContainer = await Mk8TaskContainer.CreateAsync(container.SandboxName!, ct: ct);

        var effectiveOptions = script.Options ?? Mk8ExecutionOptions.Default;

        // Compile through the full mk8.shell pipeline
        // (expand → resolve → sanitize → compile).
        var compiler = new Mk8ShellCompiler(
            Mk8CommandWhitelist.CreateDefault(
                taskContainer.RuntimeConfig,
                taskContainer.FreeTextConfig,
                taskContainer.EnvVocabularies,
                taskContainer.GigaBlacklist));
        var compiled = compiler.Compile(
            script, taskContainer.Workspace, effectiveOptions);

        // Execute all compiled commands (safe — never spawns a real shell).
        var executor = new Mk8ShellExecutor(
            sandboxContainer: taskContainer.SandboxContainer);
        var result = await executor.ExecuteAsync(compiled, ct);

        // Build a human-readable summary
        var summary = new StringBuilder();
        summary.AppendLine($"AllSucceeded: {result.AllSucceeded}");
        summary.AppendLine($"Duration: {result.TotalDuration}");
        summary.AppendLine($"Steps: {result.Steps.Count}");

        foreach (var step in result.Steps)
        {
            var status = step.Success ? "OK" : "FAIL";
            summary.AppendLine(
                $"  [{step.StepIndex}] {step.Verb} {status} " +
                $"({step.Attempts} attempt(s), {step.Duration.TotalMilliseconds:F0}ms)");

            if (!string.IsNullOrWhiteSpace(step.Error))
                summary.AppendLine($"    Error: {step.Error}");
        }

        if (!result.AllSucceeded)
            throw new InvalidOperationException(
                $"mk8.shell script execution failed.{Environment.NewLine}{summary}");

        return summary.ToString();
    }

    private async Task<string> CreateSandboxAsync(
        JsonElement parameters,
        AgentJobContext context,
        IServiceProvider scopedServices,
        CancellationToken ct)
    {
        var containerService = scopedServices.GetRequiredService<ContainerService>();

        if (!parameters.TryGetProperty("name", out var nameElement) || string.IsNullOrWhiteSpace(nameElement.GetString()))
            throw new InvalidOperationException("CreateSandbox requires a 'name' field.");

        if (!parameters.TryGetProperty("path", out var pathElement) || string.IsNullOrWhiteSpace(pathElement.GetString()))
            throw new InvalidOperationException("CreateSandbox requires a 'path' field.");

        var sandboxName = nameElement.GetString()!;
        var basePath = pathElement.GetString()!;
        var description = parameters.TryGetProperty("description", out var descElement) ? descElement.GetString() : null;

        var sandboxDir = Path.Combine(Path.GetFullPath(basePath), sandboxName);

        // Check if sandbox already exists
        var existing = await containerService.GetBySandboxNameAsync(sandboxName, ct);
        if (existing is not null)
            throw new InvalidOperationException(
                $"An mk8shell container with sandbox name '{sandboxName}' already exists.");

        // Register with mk8.shell
        await Mk8SandboxRegistrar.RegisterAsync(sandboxName, sandboxDir, ct: ct);

        // Create container via service
        var request = new CreateContainerRequest(
            ContainerType.Mk8Shell,
            sandboxName,
            basePath,
            description);

        var created = await containerService.CreateAsync(request, ct);

        return $"Created mk8shell container '{sandboxName}' at '{sandboxDir}' (id={created.Id}).";
    }

    // ═══════════════════════════════════════════════════════════════
    // TOOL SCHEMAS
    // ═══════════════════════════════════════════════════════════════

    private static JsonElement BuildMk8ShellToolSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "resourceId": {
                        "type": "string",
                        "description": "Container GUID."
                    },
                    "sandboxId": {
                        "type": "string",
                        "description": "Sandbox name."
                    },
                    "script": {
                        "type": "object",
                        "description": "Script object.",
                        "properties": {
                            "operations": {
                                "type": "array",
                                "description": "Ordered operations.",
                                "items": {
                                    "type": "object",
                                    "properties": {
                                        "verb": { "type": "string" },
                                        "args": { "type": "array", "items": { "type": "string" } },
                                        "workingDirectory": {
                                            "type": "string",
                                            "description": "Per-step CWD override (e.g. '$WORKSPACE/subdir')."
                                        }
                                    },
                                    "required": ["verb", "args"]
                                }
                            },
                            "options": { "type": "object" },
                            "cleanup": {
                                "type": "array",
                                "items": {
                                    "type": "object",
                                    "properties": {
                                        "verb": { "type": "string" },
                                        "args": { "type": "array", "items": { "type": "string" } },
                                        "workingDirectory": {
                                            "type": "string",
                                            "description": "Per-step CWD override."
                                        }
                                    },
                                    "required": ["verb", "args"]
                                }
                            }
                        },
                        "required": ["operations"]
                    }
                },
                "required": ["script"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildCreateSandboxSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "name": {
                        "type": "string",
                        "description": "Name (alphanumeric)."
                    },
                    "path": {
                        "type": "string",
                        "description": "Absolute parent directory."
                    },
                    "description": {
                        "type": "string",
                        "description": "Description."
                    }
                },
                "required": ["name", "path"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static string LoadEmbeddedResource(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"{assembly.GetName().Name}.Resources.{fileName}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
