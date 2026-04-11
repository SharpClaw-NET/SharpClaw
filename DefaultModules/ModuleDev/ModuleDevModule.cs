using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Modules.Contracts;
using SharpClaw.Modules.ModuleDev.Handlers;
using SharpClaw.Modules.ModuleDev.Services;

namespace SharpClaw.Modules.ModuleDev;

/// <summary>
/// Module Development Kit — lets an LLM agent autonomously scaffold, build,
/// hot-load, and iterate on new SharpClaw modules, plus inspect live processes
/// and the host development environment.
/// </summary>
public sealed class ModuleDevModule : ISharpClawModule
{
    public string Id => "sharpclaw_module_dev";
    public string DisplayName => "Module Development Kit";
    public string ToolPrefix => "mdk";

    // ═══════════════════════════════════════════════════════════════
    // DI Registration
    // ═══════════════════════════════════════════════════════════════

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ModuleWorkspaceService>();
        services.AddSingleton<ModuleBuildService>();
        services.AddSingleton<ModuleScaffoldService>();
        services.AddSingleton<DevEnvironmentService>();
        services.AddSingleton<ProcessInspectionService>();
        services.AddSingleton<ComTypeLibInspector>();
    }

    public void MapEndpoints(object app)
    {
        var endpoints = (IEndpointRouteBuilder)app;
        endpoints.MapModuleDevEndpoints();
    }

    // ═══════════════════════════════════════════════════════════════
    // Contracts
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleContractExport> ExportedContracts => [];

    public IReadOnlyList<ModuleContractRequirement> RequiredContracts =>
    [
        new("window_management", typeof(IWindowManager), Optional: true,
            Description: "Window-title → PID resolution for process inspection. Falls back to Process.GetProcessesByName."),
    ];

    // ═══════════════════════════════════════════════════════════════
    // Global Flag Descriptors
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleGlobalFlagDescriptor> GetGlobalFlagDescriptors() =>
    [
        new("CanScaffoldModules",  "Scaffold Modules",    "Create new module project skeletons.",                         "ScaffoldModuleAsync"),
        new("CanWriteModuleFiles", "Write Module Files",  "Read/write files in external module workspaces.",              "WriteModuleFileAsync"),
        new("CanBuildModules",     "Build Modules",       "Compile module projects via dotnet build.",                     "BuildModuleAsync"),
        new("CanLoadModules",      "Load Modules",        "Hot-load or unload external modules into the running host.",   "LoadModuleAsync"),
        new("CanTestModuleTools",  "Test Module Tools",   "Invoke tools from loaded modules for testing.",                "TestModuleToolAsync"),
        new("CanInspectProcesses", "Inspect Processes",   "Enumerate loaded DLLs, exports, COM type libraries of live processes.", "InspectProcessAsync"),
    ];

    // ═══════════════════════════════════════════════════════════════
    // CLI Commands
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleCliCommand> GetCliCommands() =>
    [
        new(
            Name: "mdk",
            Aliases: ["module-dev"],
            Scope: ModuleCliScope.TopLevel,
            Description: "Module Development Kit commands",
            UsageLines:
            [
                "mdk scaffold <module_id> <display_name> <prefix>  Scaffold a new module",
                "mdk build <module_id> [--release]                 Build a module",
                "mdk load <module_id>                              Hot-load a module",
                "mdk unload <module_id>                            Unload a module",
                "mdk reload <module_id>                            Reload a module",
                "mdk inspect <process_name_or_pid>                 Inspect a process",
                "mdk env                                           Show dev environment",
                "mdk list                                          List module workspaces",
            ],
            Handler: HandleMdkCommandAsync),
    ];

    private static readonly JsonSerializerOptions CliJsonPrint = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private static async Task HandleMdkCommandAsync(
        string[] args, IServiceProvider sp, CancellationToken ct)
    {
        if (args.Length < 2)
        {
            PrintMdkUsage();
            return;
        }

        var sub = args[1].ToLowerInvariant();

        switch (sub)
        {
            case "scaffold" when args.Length >= 5:
            {
                var scaffold = sp.GetRequiredService<ModuleScaffoldService>();
                var spec = new ModuleScaffoldService.ScaffoldSpec(
                    ModuleId: args[2],
                    DisplayName: args[3],
                    ToolPrefix: args[4],
                    Description: args.Length >= 6 ? string.Join(' ', args[5..]) : null);
                var result = await scaffold.ScaffoldAsync(spec, ct);
                Console.WriteLine($"Scaffolded at: {result.ModuleDir}");
                Console.WriteLine("Files:");
                foreach (var f in result.Files)
                    Console.WriteLine($"  {f}");
                break;
            }
            case "scaffold":
                Console.Error.WriteLine("mdk scaffold <module_id> <display_name> <prefix> [description]");
                break;

            case "build" when args.Length >= 3:
            {
                var build = sp.GetRequiredService<ModuleBuildService>();
                var config = args.Contains("--release") ? "Release" : "Debug";
                var result = await build.BuildAsync(args[2], config, ct);
                if (result.Success)
                {
                    Console.WriteLine($"Build succeeded. DLL: {result.OutputDll}");
                    if (result.Warnings.Count > 0)
                        Console.WriteLine($"  {result.Warnings.Count} warning(s)");
                }
                else
                {
                    Console.Error.WriteLine("Build failed:");
                    foreach (var e in result.Errors)
                        Console.Error.WriteLine($"  {e.File}({e.Line},{e.Column}): {e.Code} {e.Message}");
                }
                break;
            }
            case "build":
                Console.Error.WriteLine("mdk build <module_id> [--release]");
                break;

            case "load" when args.Length >= 3:
            {
                var moduleSvc = sp.GetRequiredService<ModuleService>();
                var loader = sp.GetRequiredService<ModuleLoader>();
                var workspace = sp.GetRequiredService<ModuleWorkspaceService>();
                var moduleDir = workspace.ResolveModuleDir(args[2]);
                var result = await moduleSvc.LoadExternalAsync(moduleDir, loader.RootServices, ct);
                Console.WriteLine(JsonSerializer.Serialize(result, CliJsonPrint));
                break;
            }
            case "load":
                Console.Error.WriteLine("mdk load <module_id>");
                break;

            case "unload" when args.Length >= 3:
            {
                var moduleSvc = sp.GetRequiredService<ModuleService>();
                await moduleSvc.UnloadExternalAsync(args[2], ct);
                Console.WriteLine($"Module '{args[2]}' unloaded.");
                break;
            }
            case "unload":
                Console.Error.WriteLine("mdk unload <module_id>");
                break;

            case "reload" when args.Length >= 3:
            {
                var moduleSvc = sp.GetRequiredService<ModuleService>();
                var loader = sp.GetRequiredService<ModuleLoader>();
                var result = await moduleSvc.ReloadExternalAsync(args[2], loader.RootServices, ct);
                Console.WriteLine(JsonSerializer.Serialize(result, CliJsonPrint));
                break;
            }
            case "reload":
                Console.Error.WriteLine("mdk reload <module_id>");
                break;

            case "inspect" when args.Length >= 3:
            {
                var inspection = sp.GetRequiredService<ProcessInspectionService>();
                var result = await inspection.InspectAsync(args[2], ct: ct);
                Console.WriteLine(result);
                break;
            }
            case "inspect":
                Console.Error.WriteLine("mdk inspect <process_name_or_pid>");
                break;

            case "env":
            {
                var devEnv = sp.GetRequiredService<DevEnvironmentService>();
                var info = await devEnv.GetEnvironmentAsync(ct);
                Console.WriteLine(devEnv.ToJson(info));
                break;
            }

            case "list":
            {
                var workspace = sp.GetRequiredService<ModuleWorkspaceService>();
                var dir = workspace.ExternalModulesDir;
                if (!Directory.Exists(dir))
                {
                    Console.WriteLine("No external-modules directory found.");
                    break;
                }
                foreach (var moduleDir in Directory.GetDirectories(dir))
                    Console.WriteLine($"  {Path.GetFileName(moduleDir)}");
                break;
            }

            default:
                Console.Error.WriteLine($"Unknown mdk command: {sub}");
                PrintMdkUsage();
                break;
        }
    }

    private static void PrintMdkUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  mdk scaffold <id> <name> <prefix>  Scaffold a module");
        Console.WriteLine("  mdk build <id> [--release]         Build a module");
        Console.WriteLine("  mdk load <id>                      Hot-load a module");
        Console.WriteLine("  mdk unload <id>                    Unload a module");
        Console.WriteLine("  mdk reload <id>                    Reload a module");
        Console.WriteLine("  mdk inspect <process>              Inspect a process");
        Console.WriteLine("  mdk env                            Show dev environment");
        Console.WriteLine("  mdk list                           List module workspaces");
    }

    // ═══════════════════════════════════════════════════════════════
    // Tool Definitions
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions()
    {
        var scaffold = new ModuleToolPermission(IsPerResource: false, Check: null, DelegateTo: "ScaffoldModuleAsync");
        var write    = new ModuleToolPermission(IsPerResource: false, Check: null, DelegateTo: "WriteModuleFileAsync");
        var build    = new ModuleToolPermission(IsPerResource: false, Check: null, DelegateTo: "BuildModuleAsync");
        var load     = new ModuleToolPermission(IsPerResource: false, Check: null, DelegateTo: "LoadModuleAsync");
        var test     = new ModuleToolPermission(IsPerResource: false, Check: null, DelegateTo: "TestModuleToolAsync");
        var inspect  = new ModuleToolPermission(IsPerResource: false, Check: null, DelegateTo: "InspectProcessAsync");

        return
        [
            new("scaffold_module",
                "Generate a complete module project from a specification. Creates .csproj, module class, and module.json in external-modules/{module_id}/.",
                BuildScaffoldModuleSchema(), scaffold),

            new("write_file",
                "Write or overwrite a file inside a module workspace. Scoped to external-modules/ — cannot write outside it. Cannot write binary files (.dll, .exe, etc.).",
                BuildWriteFileSchema(), write),

            new("read_file",
                "Read a file from a module workspace. Returns content with optional line truncation.",
                BuildReadFileSchema(), write),

            new("list_files",
                "List the file tree of a module workspace, with optional glob filtering.",
                BuildListFilesSchema(), write),

            new("build_module",
                "Compile a module project using dotnet build. Returns structured diagnostics (errors, warnings) and the output DLL path on success.",
                BuildBuildModuleSchema(), build, TimeoutSeconds: 120),

            new("load_module",
                "Hot-load a compiled module into the running host. If already loaded, reloads it (drain → unload → reload).",
                BuildModuleIdOnlySchema(), load),

            new("unload_module",
                "Unload a hot-loaded module from the running host.",
                BuildModuleIdOnlySchema(), load),

            new("test_tool",
                "Invoke a tool from any loaded module by name. Useful for verifying a freshly built module's tools work.",
                BuildTestToolSchema(), test),

            new("inspect_process",
                "Inspect a live process: loaded modules (DLLs), exported functions, window classes, and thread info. Read-only reconnaissance.",
                BuildInspectProcessSchema(), inspect, TimeoutSeconds: 60),

            new("discover_com_interfaces",
                "Deep-dive into a COM type library: enumerate interfaces, coclasses, methods, parameters, return types. Windows only.",
                BuildDiscoverComSchema(), inspect, TimeoutSeconds: 60),

            new("enumerate_dev_environment",
                "Report the development environment: installed SDKs, runtimes, global tools, contracts version, loaded modules, available contracts.",
                BuildEmptySchema(), scaffold),
        ];
    }

    // ═══════════════════════════════════════════════════════════════
    // Inline Tool Definitions
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleInlineToolDefinition> GetInlineToolDefinitions() =>
    [
        new("describe_module_system",
            "Return a concise reference card of the SharpClaw module contract: ISharpClawModule interface, tool definitions, permissions, contract system, and manifest format.",
            BuildEmptySchema()),

        new("list_loaded_modules",
            "Return the current list of loaded modules with IDs, prefixes, tool counts, exported contracts, and health status.",
            BuildEmptySchema()),
    ];

    // ═══════════════════════════════════════════════════════════════
    // Tool Execution — Job Pipeline
    // ═══════════════════════════════════════════════════════════════

    public async Task<string> ExecuteToolAsync(
        string toolName, JsonElement parameters, AgentJobContext job,
        IServiceProvider sp, CancellationToken ct)
    {
        return toolName switch
        {
            "scaffold_module"           => await ScaffoldModuleAsync(parameters, sp, ct),
            "write_file"                => await WriteFileAsync(parameters, sp, ct),
            "read_file"                 => await ReadFileAsync(parameters, sp, ct),
            "list_files"                => ListFiles(parameters, sp),
            "build_module"              => await BuildModuleAsync(parameters, sp, ct),
            "load_module"               => await LoadModuleAsync(parameters, sp, ct),
            "unload_module"             => await UnloadModuleAsync(parameters, sp, ct),
            "test_tool"                 => await TestToolAsync(parameters, sp, ct),
            "inspect_process"           => await InspectProcessAsync(parameters, sp, ct),
            "discover_com_interfaces"   => await DiscoverComInterfacesAsync(parameters, sp, ct),
            "enumerate_dev_environment" => await EnumerateDevEnvironmentAsync(sp, ct),
            _ => throw new NotSupportedException($"Unknown MDK tool: {toolName}"),
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // Tool Execution — Inline
    // ═══════════════════════════════════════════════════════════════

    public Task<string> ExecuteInlineToolAsync(
        string toolName, JsonElement parameters, InlineToolContext context,
        IServiceProvider sp, CancellationToken ct)
    {
        return toolName switch
        {
            "describe_module_system" => Task.FromResult(DescribeModuleSystem()),
            "list_loaded_modules"    => Task.FromResult(ListLoadedModules(sp)),
            _ => throw new NotSupportedException($"Unknown MDK inline tool: {toolName}"),
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // Tool Handlers
    // ═══════════════════════════════════════════════════════════════

    private static async Task<string> ScaffoldModuleAsync(
        JsonElement p, IServiceProvider sp, CancellationToken ct)
    {
        var scaffold = sp.GetRequiredService<ModuleScaffoldService>();

        List<ModuleScaffoldService.ToolStub>? tools = null;
        if (p.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind == JsonValueKind.Array)
        {
            tools = [];
            foreach (var t in toolsEl.EnumerateArray())
            {
                tools.Add(new ModuleScaffoldService.ToolStub(
                    Name: t.GetProperty("name").GetString()!,
                    Description: t.TryGetProperty("description", out var d) ? d.GetString() : null,
                    ParametersHint: t.TryGetProperty("parameters_hint", out var ph) ? ph.GetString() : null));
            }
        }

        var spec = new ModuleScaffoldService.ScaffoldSpec(
            ModuleId: Str(p, "module_id") ?? throw new InvalidOperationException("module_id is required."),
            DisplayName: Str(p, "display_name") ?? throw new InvalidOperationException("display_name is required."),
            ToolPrefix: Str(p, "tool_prefix") ?? throw new InvalidOperationException("tool_prefix is required."),
            Description: Str(p, "description"),
            Tools: tools);

        var result = await scaffold.ScaffoldAsync(spec, ct);
        return JsonSerializer.Serialize(new { result.ModuleDir, result.Files }, ToolJsonOpts);
    }

    private static async Task<string> WriteFileAsync(
        JsonElement p, IServiceProvider sp, CancellationToken ct)
    {
        var workspace = sp.GetRequiredService<ModuleWorkspaceService>();
        var moduleId = Str(p, "module_id") ?? throw new InvalidOperationException("module_id is required.");
        var relativePath = Str(p, "relative_path") ?? throw new InvalidOperationException("relative_path is required.");
        var content = Str(p, "content") ?? throw new InvalidOperationException("content is required.");

        var (path, bytesWritten) = await workspace.WriteFileAsync(moduleId, relativePath, content, ct);
        return JsonSerializer.Serialize(new { path, bytes_written = bytesWritten }, ToolJsonOpts);
    }

    private static async Task<string> ReadFileAsync(
        JsonElement p, IServiceProvider sp, CancellationToken ct)
    {
        var workspace = sp.GetRequiredService<ModuleWorkspaceService>();
        var moduleId = Str(p, "module_id") ?? throw new InvalidOperationException("module_id is required.");
        var relativePath = Str(p, "relative_path") ?? throw new InvalidOperationException("relative_path is required.");
        var maxLines = Int(p, "max_lines") ?? 500;

        var content = await workspace.ReadFileAsync(moduleId, relativePath, maxLines, ct);
        return content;
    }

    private static string ListFiles(JsonElement p, IServiceProvider sp)
    {
        var workspace = sp.GetRequiredService<ModuleWorkspaceService>();
        var moduleId = Str(p, "module_id") ?? throw new InvalidOperationException("module_id is required.");
        var pattern = Str(p, "include_pattern");

        var files = workspace.ListFiles(moduleId, pattern);
        return JsonSerializer.Serialize(files, ToolJsonOpts);
    }

    private static async Task<string> BuildModuleAsync(
        JsonElement p, IServiceProvider sp, CancellationToken ct)
    {
        var build = sp.GetRequiredService<ModuleBuildService>();
        var moduleId = Str(p, "module_id") ?? throw new InvalidOperationException("module_id is required.");
        var config = Str(p, "configuration") ?? "Debug";

        var result = await build.BuildAsync(moduleId, config, ct);
        return JsonSerializer.Serialize(new
        {
            result.Success,
            errors = result.Errors,
            warnings = result.Warnings,
            output_dll = result.OutputDll,
        }, ToolJsonOpts);
    }

    private static async Task<string> LoadModuleAsync(
        JsonElement p, IServiceProvider sp, CancellationToken ct)
    {
        var moduleSvc = sp.GetRequiredService<ModuleService>();
        var loader = sp.GetRequiredService<ModuleLoader>();
        var workspace = sp.GetRequiredService<ModuleWorkspaceService>();

        var moduleId = Str(p, "module_id") ?? throw new InvalidOperationException("module_id is required.");

        // Check if already loaded → reload
        var registry = sp.GetRequiredService<ModuleRegistry>();
        if (registry.GetModule(moduleId) is not null)
        {
            var reloaded = await moduleSvc.ReloadExternalAsync(moduleId, loader.RootServices, ct);
            return JsonSerializer.Serialize(reloaded, ToolJsonOpts);
        }

        var moduleDir = workspace.ResolveModuleDir(moduleId);
        var result = await moduleSvc.LoadExternalAsync(moduleDir, loader.RootServices, ct);
        return JsonSerializer.Serialize(result, ToolJsonOpts);
    }

    private static async Task<string> UnloadModuleAsync(
        JsonElement p, IServiceProvider sp, CancellationToken ct)
    {
        var moduleSvc = sp.GetRequiredService<ModuleService>();
        var moduleId = Str(p, "module_id") ?? throw new InvalidOperationException("module_id is required.");

        await moduleSvc.UnloadExternalAsync(moduleId, ct);
        return JsonSerializer.Serialize(new { moduleId, unloaded = true }, ToolJsonOpts);
    }

    private static Task<string> TestToolAsync(
        JsonElement p, IServiceProvider sp, CancellationToken ct)
    {
        var toolName = Str(p, "tool_name") ?? throw new InvalidOperationException("tool_name is required.");
        var paramsEl = p.TryGetProperty("parameters", out var pe) ? pe : default;

        // Resolve the tool via the registry and execute directly
        var registry = sp.GetRequiredService<ModuleRegistry>();
        var toolEntry = registry.FindToolByName(toolName)
            ?? throw new InvalidOperationException($"Tool '{toolName}' not found in any loaded module.");

        var timeoutSeconds = Int(p, "timeout_seconds") ?? 30;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var dummyContext = new AgentJobContext(
            JobId: Guid.NewGuid(),
            AgentId: Guid.Empty,
            ChannelId: Guid.Empty,
            ResourceId: null,
            ActionKey: toolName,
            Language: null);

        return toolEntry.Module.ExecuteToolAsync(
            toolEntry.ToolName, paramsEl, dummyContext, sp, timeoutCts.Token);
    }

    private static async Task<string> InspectProcessAsync(
        JsonElement p, IServiceProvider sp, CancellationToken ct)
    {
        var inspection = sp.GetRequiredService<ProcessInspectionService>();
        var target = Str(p, "target") ?? throw new InvalidOperationException("target is required.");
        var include = p.TryGetProperty("include", out var incEl)
            ? incEl.EnumerateArray().Select(e => e.GetString()!).ToList()
            : null;
        var exportFilter = Str(p, "export_filter");

        return await inspection.InspectAsync(target, include, exportFilter, ct);
    }

    private static async Task<string> DiscoverComInterfacesAsync(
        JsonElement p, IServiceProvider sp, CancellationToken ct)
    {
        var inspector = sp.GetRequiredService<ComTypeLibInspector>();
        var typelibPath = Str(p, "typelib_path") ?? throw new InvalidOperationException("typelib_path is required.");
        var interfaceFilter = Str(p, "interface_filter");
        var includeInherited = Bool(p, "include_inherited") ?? false;

        return await inspector.InspectAsync(typelibPath, interfaceFilter, includeInherited, ct);
    }

    private static async Task<string> EnumerateDevEnvironmentAsync(
        IServiceProvider sp, CancellationToken ct)
    {
        var devEnv = sp.GetRequiredService<DevEnvironmentService>();
        var info = await devEnv.GetEnvironmentAsync(ct);
        return devEnv.ToJson(info);
    }

    // ── Inline tool handlers ─────────────────────────────────────

    private static string DescribeModuleSystem()
    {
        return """
            # SharpClaw Module System Reference

            ## ISharpClawModule Interface
            Every module implements `ISharpClawModule` from `SharpClaw.Contracts.Modules`.

            Required members:
            - `string Id` — unique module identifier (e.g. "my_module")
            - `string DisplayName` — human-readable name
            - `string ToolPrefix` — unique tool prefix (e.g. "mm")
            - `void ConfigureServices(IServiceCollection)` — register DI services
            - `IReadOnlyList<ModuleToolDefinition> GetToolDefinitions()` — declare tools
            - `Task<string> ExecuteToolAsync(toolName, parameters, job, sp, ct)` — execute tools

            Optional members:
            - `GetInlineToolDefinitions()` — lightweight tools (no job record)
            - `ExportedContracts` / `RequiredContracts` — inter-module contracts
            - `InitializeAsync(sp, ct)` — one-time setup after DI is built
            - `ShutdownAsync()` — graceful cleanup
            - `SeedDataAsync(sp, ct)` — first-run seed data
            - `MapEndpoints(object app)` — register REST endpoints (cast to IEndpointRouteBuilder)
            - `GetResourceTypeDescriptors()` — per-resource permission types
            - `GetGlobalFlagDescriptors()` — global permission flags
            - `GetCliCommands()` — CLI REPL commands
            - `GetHeaderTags()` — chat header tag definitions
            - `HealthCheckAsync(ct)` — periodic health check

            ## Tool Definition
            `ModuleToolDefinition(Name, Description, ParametersSchema, Permission, TimeoutSeconds?, Aliases?)`
            - ParametersSchema is a JSON Schema (JsonElement)
            - Permission: `ModuleToolPermission(IsPerResource, Check, DelegateTo)`

            ## module.json Manifest
            Required fields: id, displayName, version, toolPrefix, entryAssembly, minHostVersion
            Optional: description, platforms, enabled, executionTimeoutSeconds, exports, requires

            ## Hot-Loading
            External modules are loaded from `external-modules/{id}/` directories.
            Each gets a collectible AssemblyLoadContext for clean unload.
            Build output DLL + module.json must be in the module directory.
            """;
    }

    private static string ListLoadedModules(IServiceProvider sp)
    {
        var registry = sp.GetRequiredService<ModuleRegistry>();
        var modules = registry.GetAllModules();
        var result = modules.Select(m => new
        {
            m.Id,
            m.DisplayName,
            m.ToolPrefix,
            ToolCount = m.GetToolDefinitions().Count,
            InlineToolCount = m.GetInlineToolDefinitions().Count,
            ExportedContracts = m.ExportedContracts.Select(c => c.ContractName).ToList(),
        });

        return JsonSerializer.Serialize(result, ToolJsonOpts);
    }

    // ═══════════════════════════════════════════════════════════════
    // Tool Schemas
    // ═══════════════════════════════════════════════════════════════

    private static JsonElement BuildScaffoldModuleSchema() => ParseSchema("""
        {
            "type": "object",
            "properties": {
                "module_id":   { "type": "string", "description": "Module ID (^[a-z][a-z0-9_]{0,39}$)." },
                "display_name": { "type": "string", "description": "Human-readable name." },
                "tool_prefix": { "type": "string", "description": "Tool prefix (^[a-z][a-z0-9]{0,19}$)." },
                "description": { "type": "string", "description": "Module description." },
                "tools": {
                    "type": "array",
                    "description": "Tool stubs to generate.",
                    "items": {
                        "type": "object",
                        "properties": {
                            "name": { "type": "string" },
                            "description": { "type": "string" },
                            "parameters_hint": { "type": "string" }
                        },
                        "required": ["name"]
                    }
                }
            },
            "required": ["module_id", "display_name", "tool_prefix"]
        }
        """);

    private static JsonElement BuildWriteFileSchema() => ParseSchema("""
        {
            "type": "object",
            "properties": {
                "module_id":     { "type": "string", "description": "Target module ID." },
                "relative_path": { "type": "string", "description": "Path relative to module root (e.g. Services/MyService.cs)." },
                "content":       { "type": "string", "description": "Full file content." }
            },
            "required": ["module_id", "relative_path", "content"]
        }
        """);

    private static JsonElement BuildReadFileSchema() => ParseSchema("""
        {
            "type": "object",
            "properties": {
                "module_id":     { "type": "string", "description": "Target module ID." },
                "relative_path": { "type": "string", "description": "Path relative to module root." },
                "max_lines":     { "type": "integer", "description": "Truncation limit (default: 500)." }
            },
            "required": ["module_id", "relative_path"]
        }
        """);

    private static JsonElement BuildListFilesSchema() => ParseSchema("""
        {
            "type": "object",
            "properties": {
                "module_id":       { "type": "string", "description": "Target module ID." },
                "include_pattern": { "type": "string", "description": "Glob filter (default: **/*)." }
            },
            "required": ["module_id"]
        }
        """);

    private static JsonElement BuildBuildModuleSchema() => ParseSchema("""
        {
            "type": "object",
            "properties": {
                "module_id":     { "type": "string", "description": "Module to build." },
                "configuration": { "type": "string", "description": "Debug (default) or Release." }
            },
            "required": ["module_id"]
        }
        """);

    private static JsonElement BuildModuleIdOnlySchema() => ParseSchema("""
        {
            "type": "object",
            "properties": {
                "module_id": { "type": "string", "description": "Module ID." }
            },
            "required": ["module_id"]
        }
        """);

    private static JsonElement BuildTestToolSchema() => ParseSchema("""
        {
            "type": "object",
            "properties": {
                "tool_name":       { "type": "string", "description": "Fully qualified tool name." },
                "parameters":      { "type": "object", "description": "JSON parameters to pass." },
                "timeout_seconds": { "type": "integer", "description": "Override timeout (default: 30)." }
            },
            "required": ["tool_name", "parameters"]
        }
        """);

    private static JsonElement BuildInspectProcessSchema() => ParseSchema("""
        {
            "type": "object",
            "properties": {
                "target":        { "type": "string", "description": "Process name, PID, or window title substring." },
                "include":       { "type": "array", "items": { "type": "string" }, "description": "Sections: modules, exports, window_classes, threads." },
                "export_filter": { "type": "string", "description": "Regex filter for export names." }
            },
            "required": ["target"]
        }
        """);

    private static JsonElement BuildDiscoverComSchema() => ParseSchema("""
        {
            "type": "object",
            "properties": {
                "typelib_path":      { "type": "string", "description": "Path to .tlb or .dll with embedded type lib." },
                "interface_filter":  { "type": "string", "description": "Regex to filter interface names." },
                "include_inherited": { "type": "boolean", "description": "Include inherited members (default: false)." }
            },
            "required": ["typelib_path"]
        }
        """);

    private static JsonElement BuildEmptySchema() => ParseSchema("""
        { "type": "object", "properties": {} }
        """);

    // ── Helpers ───────────────────────────────────────────────────

    private static JsonElement ParseSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static readonly JsonSerializerOptions ToolJsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string? Str(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static bool? Bool(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : null;
}
