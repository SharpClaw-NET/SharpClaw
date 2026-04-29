using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mk8.Shell.Engine;
using Mk8.Shell.Models;
using Mk8.Shell.Safety;
using Mk8.Shell.Startup;
using SharpClaw.Modules.Mk8Shell.Contracts;
using SharpClaw.Modules.Mk8Shell.Dtos;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Modules.Mk8Shell.Services;
using SharpClaw.Modules.Mk8Shell.Handlers;

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
        services.AddScoped(sp => sp.GetRequiredService<IModuleDbContextFactory>()
            .CreateDbContext<Mk8ShellDbContext>());
        services.AddScoped<ContainerService>();
        services.AddScoped<IContainerSandboxResolver, ContainerSandboxResolver>();
    }

    public void MapEndpoints(object app)
    {
        var endpoints = (IEndpointRouteBuilder)app;
        endpoints.MapContainerResourceEndpoints();
    }

    public IReadOnlyList<ModuleContractExport> ExportedContracts => [];

    // ═══════════════════════════════════════════════════════════════
    // Resource Type Descriptors
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleResourceTypeDescriptor> GetResourceTypeDescriptors() =>
    [
        new("Mk8Shell", "SafeShell", "ExecuteAsSafeShellAsync", static async (sp, ct) =>
        {
            var db = sp.GetRequiredService<Mk8ShellDbContext>();
            return await db.Containers.Select(c => c.Id).ToListAsync(ct);
        },
        LoadLookupItems: static async (sp, ct) =>
        {
            var db = sp.GetRequiredService<Mk8ShellDbContext>();
            return await db.Containers.Select(c => new ValueTuple<Guid, string>(c.Id, c.Name)).ToListAsync(ct);
        }),
        new("Container", "ContainerAccess", "AccessContainerAsync", static async (sp, ct) =>
        {
            var db = sp.GetRequiredService<Mk8ShellDbContext>();
            return await db.Containers.Select(c => c.Id).ToListAsync(ct);
        },
        LoadLookupItems: static async (sp, ct) =>
        {
            var db = sp.GetRequiredService<Mk8ShellDbContext>();
            return await db.Containers.Select(c => new ValueTuple<Guid, string>(c.Id, c.Name)).ToListAsync(ct);
        },
        DefaultResourceKey: "container"),
    ];

    // ═══════════════════════════════════════════════════════════════
    // Global Flag Descriptors
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleGlobalFlagDescriptor> GetGlobalFlagDescriptors() =>
    [
        new("CanCreateContainers", "Create Containers", "Create new sandbox / VM / container environments.", "CreateContainerAsync"),
    ];

    public IReadOnlyList<ModuleCliCommand> GetCliCommands() =>
    [
        new(
            Name: "container",
            Aliases: [],
            Scope: ModuleCliScope.ResourceType,
            Description: "Sandbox container CRUD",
            UsageLines:
            [
                "resource container add mk8shell <name> <path>  Create an mk8shell sandbox",
                "resource container get <id>                    Show a container",
                "resource container list                        List all containers",
                "resource container update <id> [description]   Update a container",
                "resource container delete <id>                 Delete a container",
                "resource container sync                        Import from mk8.shell registry",
            ],
            Handler: HandleContainerResourceCliAsync),
    ];

    private static async Task HandleContainerResourceCliAsync(
        string[] args, IServiceProvider sp, CancellationToken ct)
    {
        var ids = sp.GetRequiredService<ICliIdResolver>();

        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage:");
            Console.Error.WriteLine("  resource container add mk8shell <name> <path>  Create an mk8shell sandbox");
            Console.Error.WriteLine("  resource container get <id>                    Show a container");
            Console.Error.WriteLine("  resource container list                        List all containers");
            Console.Error.WriteLine("  resource container update <id> [description]   Update a container");
            Console.Error.WriteLine("  resource container delete <id>                 Delete a container");
            Console.Error.WriteLine("  resource container sync                        Import from mk8.shell registry");
            return;
        }

        var sub = args[2].ToLowerInvariant();
        var svc = sp.GetRequiredService<ContainerService>();

        switch (sub)
        {
            case "add" when args.Length >= 6
                && args[3].Equals("mk8shell", StringComparison.OrdinalIgnoreCase):
                ids.PrintJson(await svc.CreateAsync(
                    new CreateContainerRequest(
                        ContainerType.Mk8Shell,
                        args[4],
                        args[5],
                        args.Length >= 7 ? string.Join(' ', args[6..]) : null),
                    userId: null));
                break;
            case "add":
                Console.Error.WriteLine("resource container add mk8shell <name> <parentPath>");
                Console.Error.WriteLine("  Example: resource container add mk8shell Banana D:/");
                break;

            case "get" when args.Length >= 4:
                var container = await svc.GetByIdAsync(ids.Resolve(args[3]), ct);
                if (container is null) { Console.Error.WriteLine("Not found."); return; }
                ids.PrintJson(container);
                break;
            case "get":
                Console.Error.WriteLine("resource container get <id>");
                break;

            case "list":
                ids.PrintJson(await svc.ListAsync(ct));
                break;

            case "update" when args.Length >= 5:
                var updated = await svc.UpdateAsync(
                    ids.Resolve(args[3]),
                    new UpdateContainerRequest(
                        Description: string.Join(' ', args[4..])));
                if (updated is null) { Console.Error.WriteLine("Not found."); return; }
                ids.PrintJson(updated);
                break;
            case "update":
                Console.Error.WriteLine("resource container update <id> [description]");
                break;

            case "delete" when args.Length >= 4:
                Console.WriteLine(
                    await svc.DeleteAsync(ids.Resolve(args[3]))
                        ? "Done." : "Not found.");
                break;
            case "delete":
                Console.Error.WriteLine("resource container delete <id>");
                break;

            case "sync":
                ids.PrintJson(await svc.SyncLocalMk8ShellAsync(ct));
                break;

            default:
                Console.Error.WriteLine($"Unknown command: resource container {sub}");
                break;
        }
    }

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

        var created = await containerService.CreateAsync(request, userId: null, ct);

        return $"Created mk8shell container '{sandboxName}' at '{sandboxDir}' (id={created.Id}).";
    }

    // ═══════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════

    public Task InitializeAsync(IServiceProvider services, CancellationToken ct)
    {
        // Seed mk8.shell base env on first startup.
        Mk8GlobalEnv.Load();
        return Task.CompletedTask;
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
