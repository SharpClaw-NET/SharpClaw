using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Tasks;
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
        services.AddScoped<ModuleWorkspaceService>();
        services.AddScoped<ModuleBuildService>();
        services.AddSingleton<IModuleRuntimeCommandRunner, ModuleRuntimeCommandRunner>();
        services.AddScoped<ModuleRuntimeVerificationService>();
        services.AddScoped<ModuleScaffoldService>();
        services.AddSingleton<SharpClawSdkReferenceService>();
        services.AddScoped<DevEnvironmentService>();
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
        new("window_management", Optional: true,
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
        new("CanManageTasks",      "Manage Agent Tasks",  "Detect, list, view, create, update, and delete SharpClaw task definitions.", "ManageTaskAsync"),
        new("CanUseSdkReference",  "Use SDK Reference",   "Read SharpClaw SDK and sidecar development reference material.", "UseSdkReferenceAsync"),
        new("CanRunAgentWorkflow", "Run Agent Workflow",  "Apply module or task source, verify it, hot-load when requested, and steer the next conversation turn.", "RunAgentWorkflowAsync"),
        new("CanSteerConversation","Steer Conversation",  "Persist host-owned system steering messages into a channel or thread.", "SteerConversationAsync"),
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
                var lifecycle = sp.GetRequiredService<IModuleLifecycleManager>();
                var workspace = sp.GetRequiredService<ModuleWorkspaceService>();
                var moduleDir = workspace.ResolveModuleDir(args[2]);
                var result = await lifecycle.LoadExternalAsync(moduleDir, sp, ct);
                Console.WriteLine(JsonSerializer.Serialize(result, CliJsonPrint));
                break;
            }
            case "load":
                Console.Error.WriteLine("mdk load <module_id>");
                break;

            case "unload" when args.Length >= 3:
            {
                var lifecycle = sp.GetRequiredService<IModuleLifecycleManager>();
                await lifecycle.UnloadExternalAsync(args[2], ct);
                Console.WriteLine($"Module '{args[2]}' unloaded.");
                break;
            }
            case "unload":
                Console.Error.WriteLine("mdk unload <module_id>");
                break;

            case "reload" when args.Length >= 3:
            {
                var lifecycle = sp.GetRequiredService<IModuleLifecycleManager>();
                var result = await lifecycle.ReloadExternalAsync(args[2], sp, ct);
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
        var task     = new ModuleToolPermission(IsPerResource: false, Check: null, DelegateTo: "ManageTaskAsync");
        var sdk      = new ModuleToolPermission(IsPerResource: false, Check: null, DelegateTo: "UseSdkReferenceAsync");
        var workflow = new ModuleToolPermission(IsPerResource: false, Check: null, DelegateTo: "RunAgentWorkflowAsync");
        var steer    = new ModuleToolPermission(IsPerResource: false, Check: null, DelegateTo: "SteerConversationAsync");

        return
        [
            new("scaffold_module",
                "Generate a complete module project from a specification. Creates a dotnet, node, or python module scaffold in external-modules/{module_id}/.",
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

            new("get_sdk_reference",
                "Return SharpClaw SDK reference material for AI agents, including .NET, JavaScript, Python, storage, task, manifest, and conversation steering examples.",
                BuildSdkReferenceSchema(), sdk),

            new("apply_module_files",
                "Write a batch of module files, build or verify the runtime, hot-load the module, optionally invoke test tools, and persist conversation steering for the next turn.",
                BuildApplyModuleFilesSchema(), workflow, TimeoutSeconds: 180),

            new("record_conversation_steering",
                "Persist a host-owned system steering message into a channel or thread so the next chat turn sees the feedback.",
                BuildRecordConversationSteeringSchema(), steer),

            new("list_conversation_steering",
                "List recent conversation steering messages for a channel or thread.",
                BuildListConversationSteeringSchema(), steer),

            // ── Task management ──────────────────────────────────
            new("list_tasks",
                "Detect and list every SharpClaw task definition (id, name, description, active flag, parameters, requirements, triggers).",
                BuildEmptySchema(), task),

            new("get_task",
                "Get a single task definition by id, including its parameters, requirements, and trigger metadata.",
                BuildTaskIdOnlySchema(), task),

            new("validate_task",
                "Parse and validate raw task C# source text without persisting it. Returns diagnostics so the agent can iterate before saving.",
                BuildTaskValidateSchema(), task),

            new("create_task",
                "Create a new task definition from raw C# source. The script is parsed and validated; trigger bindings are synchronised on save.",
                BuildTaskCreateSchema(), task),

            new("update_task",
                "Update an existing task definition's source text and/or active flag. Re-parses, re-validates, and re-syncs triggers when source changes.",
                BuildTaskUpdateSchema(), task),

            new("delete_task",
                "Delete a task definition by id. Removes its trigger bindings as well.",
                BuildTaskIdOnlySchema(), task),

            new("apply_task_source",
                "Validate raw task C# source, create or update the task only when valid, and persist conversation steering with diagnostics or save details.",
                BuildApplyTaskSourceSchema(), workflow),
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
            "get_sdk_reference"          => GetSdkReference(parameters, sp),
            "apply_module_files"         => await ApplyModuleFilesAsync(parameters, job, sp, ct),
            "record_conversation_steering" => await RecordConversationSteeringAsync(parameters, job, sp, ct),
            "list_conversation_steering" => await ListConversationSteeringAsync(parameters, job, sp, ct),
            "list_tasks"                => await ListTasksAsync(sp, ct),
            "get_task"                  => await GetTaskAsync(parameters, sp, ct),
            "validate_task"             => ValidateTask(parameters, sp),
            "create_task"               => await CreateTaskAsync(parameters, sp, ct),
            "update_task"               => await UpdateTaskAsync(parameters, sp, ct),
            "delete_task"               => await DeleteTaskAsync(parameters, sp, ct),
            "apply_task_source"          => await ApplyTaskSourceAsync(parameters, job, sp, ct),
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
            "describe_module_system" => Task.FromResult(DescribeModuleSystem(sp)),
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
            Tools: tools,
            Runtime: Str(p, "runtime"));

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
        var lifecycle = sp.GetRequiredService<IModuleLifecycleManager>();
        var workspace = sp.GetRequiredService<ModuleWorkspaceService>();

        var moduleId = Str(p, "module_id") ?? throw new InvalidOperationException("module_id is required.");

        // Check if already loaded → reload
        if (lifecycle.IsModuleRegistered(moduleId))
        {
            var reloaded = await lifecycle.ReloadExternalAsync(moduleId, sp, ct);
            return JsonSerializer.Serialize(reloaded, ToolJsonOpts);
        }

        var moduleDir = workspace.ResolveModuleDir(moduleId);
        var result = await lifecycle.LoadExternalAsync(moduleDir, sp, ct);
        return JsonSerializer.Serialize(result, ToolJsonOpts);
    }

    private static async Task<string> UnloadModuleAsync(
        JsonElement p, IServiceProvider sp, CancellationToken ct)
    {
        var lifecycle = sp.GetRequiredService<IModuleLifecycleManager>();
        var moduleId = Str(p, "module_id") ?? throw new InvalidOperationException("module_id is required.");

        await lifecycle.UnloadExternalAsync(moduleId, ct);
        return JsonSerializer.Serialize(new { moduleId, unloaded = true }, ToolJsonOpts);
    }

    private static Task<string> TestToolAsync(
        JsonElement p, IServiceProvider sp, CancellationToken ct)
    {
        var toolName = Str(p, "tool_name") ?? throw new InvalidOperationException("tool_name is required.");
        var paramsEl = p.TryGetProperty("parameters", out var pe) ? pe : default;

        // Resolve the tool via the lifecycle manager and execute directly
        var lifecycle = sp.GetRequiredService<IModuleLifecycleManager>();
        var toolEntry = lifecycle.FindToolByName(toolName)
            ?? throw new InvalidOperationException($"Tool '{toolName}' not found in any loaded module.");

        var timeoutSeconds = Int(p, "timeout_seconds") ?? 30;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var dummyContext = new AgentJobContext(
            JobId: Guid.NewGuid(),
            AgentId: Guid.Empty,
            ChannelId: Guid.Empty,
            ResourceId: null,
            ActionKey: toolName);

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

    private static string GetSdkReference(JsonElement p, IServiceProvider sp)
    {
        var topic = Str(p, "topic") ?? "agent_workflow";
        return sp.GetRequiredService<SharpClawSdkReferenceService>().GetReference(topic);
    }

    private static async Task<string> RecordConversationSteeringAsync(
        JsonElement p,
        AgentJobContext job,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var target = ReadSteeringTarget(p, job);
        var summary = Str(p, "summary") ?? throw new InvalidOperationException("summary is required.");
        var category = Str(p, "category") ?? "manual";
        var details = Str(p, "details");
        var response = await AddSteeringAsync(sp, target, category, summary, details, ct);
        return JsonSerializer.Serialize(response, ToolJsonOpts);
    }

    private static async Task<string> ListConversationSteeringAsync(
        JsonElement p,
        AgentJobContext job,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var target = ReadSteeringTarget(p, job);
        var limit = Int(p, "limit") ?? 20;
        var rows = await ResolveConversationSteering(sp)
            .ListAsync(target.ChannelId, target.ThreadId, limit, ct);
        return JsonSerializer.Serialize(rows, ToolJsonOpts);
    }

    private static async Task<string> ApplyModuleFilesAsync(
        JsonElement p,
        AgentJobContext job,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var target = ReadWorkflowSteeringTarget(p, job);
        var workspace = sp.GetRequiredService<ModuleWorkspaceService>();
        var build = sp.GetRequiredService<ModuleBuildService>();
        var verifier = sp.GetRequiredService<ModuleRuntimeVerificationService>();
        var lifecycle = sp.GetRequiredService<IModuleLifecycleManager>();
        var moduleId = Str(p, "module_id") ?? throw new InvalidOperationException("module_id is required.");
        var configuration = Str(p, "configuration") ?? "Debug";
        var written = new List<object>();

        try
        {
            if (!p.TryGetProperty("files", out var filesEl) || filesEl.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("files is required and must be an array.");

            foreach (var file in filesEl.EnumerateArray())
            {
                var relativePath = Str(file, "relative_path")
                    ?? throw new InvalidOperationException("files[].relative_path is required.");
                var content = Str(file, "content")
                    ?? throw new InvalidOperationException($"content is required for '{relativePath}'.");
                var write = await workspace.WriteFileAsync(moduleId, relativePath, content, ct);
                written.Add(new
                {
                    relative_path = relativePath,
                    path = write.Path,
                    bytes_written = write.BytesWritten,
                });
            }

            var moduleDir = workspace.ResolveModuleDir(moduleId);
            var runtime = DetectRuntime(moduleDir, p);
            var buildRequested = Bool(p, "build") ?? runtime == ModuleScaffoldService.DotNetRuntime;
            var verifyRequested = Bool(p, "verify")
                ?? runtime is ModuleScaffoldService.NodeRuntime or ModuleScaffoldService.PythonRuntime;
            ModuleBuildService.BuildResult? buildResult = null;
            ModuleRuntimeVerificationResult? verificationResult = null;

            if (buildRequested)
            {
                buildResult = await build.BuildAsync(moduleId, configuration, ct);
                if (!buildResult.Success)
                {
                    var steering = await AddSteeringAsync(
                        sp,
                        target,
                        "module_build",
                        $"Module '{moduleId}' build failed.",
                        FormatBuildDiagnostics(buildResult),
                        ct);
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        module_id = moduleId,
                        runtime,
                        files = written,
                        build = buildResult,
                        verification = verificationResult,
                        steering,
                    }, ToolJsonOpts);
                }
            }

            if (verifyRequested)
            {
                verificationResult = await verifier.VerifyAsync(
                    moduleId,
                    runtime,
                    new ModuleRuntimeVerificationRequest(
                        InstallDependencies: Bool(p, "install_dependencies") ?? false,
                        RunDeclaredVerify: Bool(p, "run_declared_verify") ?? true,
                        TimeoutSeconds: Int(p, "verification_timeout_seconds")),
                    ct);
                if (!verificationResult.Success)
                {
                    var steering = await AddSteeringAsync(
                        sp,
                        target,
                        "module_verify",
                        $"Module '{moduleId}' runtime verification failed.",
                        FormatVerificationDiagnostics(verificationResult),
                        ct);
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        module_id = moduleId,
                        runtime,
                        files = written,
                        build = buildResult,
                        verification = verificationResult,
                        steering,
                    }, ToolJsonOpts);
                }
            }

            ModuleStateResponse? state = null;
            if (Bool(p, "load") ?? true)
            {
                state = lifecycle.IsModuleRegistered(moduleId)
                    ? await lifecycle.ReloadExternalAsync(moduleId, sp, ct)
                    : await lifecycle.LoadExternalAsync(moduleDir, sp, ct);
            }

            var testResults = await RunWorkflowToolTestsAsync(p, lifecycle, sp, job, ct);
            var workflowSuccess = testResults.All(test => test.Success);
            var steeringResponse = await AddSteeringAsync(
                sp,
                target,
                "module_workflow",
                BuildModuleWorkflowSummary(moduleId, runtime, buildResult, verificationResult, state, testResults),
                BuildModuleWorkflowDetails(moduleId, written, buildResult, verificationResult, state, testResults),
                ct);

            return JsonSerializer.Serialize(new
            {
                success = workflowSuccess,
                module_id = moduleId,
                runtime,
                files = written,
                build = buildResult,
                verification = verificationResult,
                load = state,
                tests = testResults,
                steering = steeringResponse,
            }, ToolJsonOpts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var steering = await AddSteeringAsync(
                sp,
                target,
                "module_workflow_error",
                $"Module '{moduleId}' workflow failed before completion.",
                TruncateForSteering(ex.ToString()),
                ct);
            return JsonSerializer.Serialize(new
            {
                success = false,
                module_id = moduleId,
                files = written,
                error = ex.Message,
                steering,
            }, ToolJsonOpts);
        }
    }

    private static async Task<string> ApplyTaskSourceAsync(
        JsonElement p,
        AgentJobContext job,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var target = ReadWorkflowSteeringTarget(p, job);
        var authoring = ResolveTaskAuthoring(sp);
        var source = Str(p, "source_text") ?? throw new InvalidOperationException("source_text is required.");
        var taskId = TryParseGuid(Str(p, "task_id"));

        try
        {
            var validation = authoring.ValidateDefinition(source);
            if (!validation.IsValid)
            {
                var steering = await AddSteeringAsync(
                    sp,
                    target,
                    "task_validation",
                    "Task source validation failed. Do not save it yet.",
                    FormatTaskDiagnostics(validation),
                    ct);
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    action = "validate",
                    validation,
                    steering,
                }, ToolJsonOpts);
            }

            TaskDefinitionResponse saved;
            var action = taskId is { } ? "update" : "create";
            if (taskId is { } updateId)
            {
                saved = await authoring.UpdateDefinitionAsync(
                    updateId,
                    new UpdateTaskDefinitionRequest(source, Bool(p, "is_active")),
                    ct)
                    ?? throw new InvalidOperationException($"Task definition '{updateId}' not found.");
            }
            else
            {
                saved = await authoring.CreateDefinitionAsync(
                    new CreateTaskDefinitionRequest(source),
                    ct);
            }

            var steeringResponse = await AddSteeringAsync(
                sp,
                target,
                "task_workflow",
                $"Task '{saved.Name}' {action}d successfully.",
                $"Task id: {saved.Id}{Environment.NewLine}Active: {saved.IsActive}",
                ct);

            return JsonSerializer.Serialize(new
            {
                success = true,
                action,
                validation,
                task = saved,
                steering = steeringResponse,
            }, ToolJsonOpts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var steering = await AddSteeringAsync(
                sp,
                target,
                "task_workflow_error",
                "Task source workflow failed before save completed.",
                TruncateForSteering(ex.ToString()),
                ct);
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message,
                steering,
            }, ToolJsonOpts);
        }
    }

    // ── Task management handlers ─────────────────────────────────

    private static ITaskAuthoring ResolveTaskAuthoring(IServiceProvider sp) =>
        sp.GetService<ITaskAuthoring>()
            ?? throw new InvalidOperationException(
                "ITaskAuthoring is not registered. The host must register TaskService as ITaskAuthoring.");

    private static async Task<string> ListTasksAsync(IServiceProvider sp, CancellationToken ct)
    {
        var authoring = ResolveTaskAuthoring(sp);
        var tasks = await authoring.ListDefinitionsAsync(ct);
        return JsonSerializer.Serialize(tasks, ToolJsonOpts);
    }

    private static async Task<string> GetTaskAsync(JsonElement p, IServiceProvider sp, CancellationToken ct)
    {
        var authoring = ResolveTaskAuthoring(sp);
        var id = ParseTaskId(p);
        var task = await authoring.GetDefinitionAsync(id, ct)
            ?? throw new InvalidOperationException($"Task definition '{id}' not found.");
        return JsonSerializer.Serialize(task, ToolJsonOpts);
    }

    private static string ValidateTask(JsonElement p, IServiceProvider sp)
    {
        var authoring = ResolveTaskAuthoring(sp);
        var source = Str(p, "source_text") ?? throw new InvalidOperationException("source_text is required.");
        var result = authoring.ValidateDefinition(source);
        return JsonSerializer.Serialize(result, ToolJsonOpts);
    }

    private static async Task<string> CreateTaskAsync(JsonElement p, IServiceProvider sp, CancellationToken ct)
    {
        var authoring = ResolveTaskAuthoring(sp);
        var source = Str(p, "source_text") ?? throw new InvalidOperationException("source_text is required.");
        var created = await authoring.CreateDefinitionAsync(new CreateTaskDefinitionRequest(source), ct);
        return JsonSerializer.Serialize(created, ToolJsonOpts);
    }

    private static async Task<string> UpdateTaskAsync(JsonElement p, IServiceProvider sp, CancellationToken ct)
    {
        var authoring = ResolveTaskAuthoring(sp);
        var id = ParseTaskId(p);
        var request = new UpdateTaskDefinitionRequest(
            SourceText: Str(p, "source_text"),
            IsActive: Bool(p, "is_active"));
        var updated = await authoring.UpdateDefinitionAsync(id, request, ct)
            ?? throw new InvalidOperationException($"Task definition '{id}' not found.");
        return JsonSerializer.Serialize(updated, ToolJsonOpts);
    }

    private static async Task<string> DeleteTaskAsync(JsonElement p, IServiceProvider sp, CancellationToken ct)
    {
        var authoring = ResolveTaskAuthoring(sp);
        var id = ParseTaskId(p);
        var removed = await authoring.DeleteDefinitionAsync(id, ct);
        return JsonSerializer.Serialize(new { task_id = id, deleted = removed }, ToolJsonOpts);
    }

    private static Guid ParseTaskId(JsonElement p)
    {
        var raw = Str(p, "task_id") ?? throw new InvalidOperationException("task_id is required.");
        return Guid.TryParse(raw, out var id)
            ? id
            : throw new InvalidOperationException($"task_id '{raw}' is not a valid GUID.");
    }

    // ── Inline tool handlers ─────────────────────────────────────

    private static string DescribeModuleSystem(IServiceProvider sp)
    {
        return sp.GetRequiredService<SharpClawSdkReferenceService>().GetReference("all") + Environment.NewLine + """

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
        var provider = sp.GetRequiredService<IModuleInfoProvider>();
        var modules = provider.GetAllModules();
        var result = modules.Select(m => new
        {
            m.Id,
            m.ToolPrefix,
            ExportedContracts = m.ExportedContractNames,
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
                "runtime": { "type": "string", "enum": ["dotnet", "node", "python"], "description": "Module runtime. Defaults to dotnet." },
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

    private static JsonElement BuildSdkReferenceSchema() => ParseSchema("""
        {
            "type": "object",
            "properties": {
                "topic": {
                    "type": "string",
                    "enum": ["agent_workflow", "dotnet", "javascript", "python", "storage", "conversation_steering", "tasks", "manifest", "all"],
                    "description": "Reference topic. Defaults to agent_workflow."
                }
            }
        }
        """);

    private static JsonElement BuildRecordConversationSteeringSchema() => ParseSchema("""
        {
            "type": "object",
            "properties": {
                "channel_id":  { "type": "string", "description": "Target channel GUID. Optional only when the current job supplies a channel." },
                "thread_id":   { "type": "string", "description": "Optional target thread GUID." },
                "summary":     { "type": "string", "description": "Short steering summary for the next model turn." },
                "details":     { "type": "string", "description": "Optional detailed diagnostics or next action context." },
                "source":      { "type": "string", "description": "Source label. Defaults to module_dev." },
                "category":    { "type": "string", "description": "Steering category. Defaults to manual." },
                "client_type": { "type": "string", "description": "Client type stored on the chat row. Defaults to module-dev." }
            },
            "required": ["summary"]
        }
        """);

    private static JsonElement BuildListConversationSteeringSchema() => ParseSchema("""
        {
            "type": "object",
            "properties": {
                "channel_id":  { "type": "string", "description": "Target channel GUID. Optional only when the current job supplies a channel." },
                "thread_id":   { "type": "string", "description": "Optional target thread GUID." },
                "limit":       { "type": "integer", "description": "Maximum rows to return. Defaults to 20." },
                "source":      { "type": "string", "description": "Source label. Defaults to module_dev." },
                "client_type": { "type": "string", "description": "Client type. Defaults to module-dev." }
            }
        }
        """);

    private static JsonElement BuildApplyModuleFilesSchema() => ParseSchema("""
        {
            "type": "object",
            "properties": {
                "module_id":     { "type": "string", "description": "Target module ID." },
                "runtime":       { "type": "string", "enum": ["dotnet", "node", "python"], "description": "Optional runtime override. Otherwise read from module.json or inferred from a csproj." },
                "configuration": { "type": "string", "description": "Debug or Release for dotnet builds. Defaults to Debug." },
                "build":         { "type": "boolean", "description": "Override whether to run dotnet build. Defaults to true for dotnet and false for node/python." },
                "verify":        { "type": "boolean", "description": "Override runtime verification. Defaults to true for node/python and false for dotnet." },
                "install_dependencies": { "type": "boolean", "description": "Install runtime dependencies before verification. Defaults to false. Node uses npm ci/install. Python uses a module-local .sharpclaw-venv." },
                "run_declared_verify": { "type": "boolean", "description": "Run declared verify hooks after syntax checks. Defaults to true. Node uses npm run sharpclawVerify. Python uses [tool.sharpclaw] verify-command = \"python -m module\"." },
                "verification_timeout_seconds": { "type": "integer", "description": "Per verification command timeout, clamped between 1 and 120 seconds. Defaults to 30." },
                "load":          { "type": "boolean", "description": "Hot-load or reload the module after build/write. Defaults to true." },
                "files": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "relative_path": { "type": "string", "description": "File path relative to module root." },
                            "content":       { "type": "string", "description": "Full file content." }
                        },
                        "required": ["relative_path", "content"]
                    }
                },
                "test_tools": {
                    "type": "array",
                    "description": "Optional loaded tools to invoke after load.",
                    "items": {
                        "type": "object",
                        "properties": {
                            "tool_name":       { "type": "string" },
                            "parameters":      { "type": "object" },
                            "timeout_seconds": { "type": "integer" }
                        },
                        "required": ["tool_name"]
                    }
                },
                "conversation": { "$ref": "#/$defs/conversation" }
            },
            "required": ["module_id", "files", "conversation"],
            "$defs": {
                "conversation": {
                    "type": "object",
                    "properties": {
                        "channel_id":  { "type": "string", "description": "Target channel GUID. Optional only when the current job supplies a channel." },
                        "thread_id":   { "type": "string", "description": "Optional target thread GUID." },
                        "source":      { "type": "string", "description": "Source label. Defaults to module_dev." },
                        "client_type": { "type": "string", "description": "Client type stored on steering messages. Defaults to module-dev." }
                    }
                }
            }
        }
        """);

    private static JsonElement BuildApplyTaskSourceSchema() => ParseSchema("""
        {
            "type": "object",
            "properties": {
                "task_id":     { "type": "string", "description": "Existing task GUID. Omit to create a new task." },
                "source_text": { "type": "string", "description": "Raw C# task source. It is validated before save." },
                "is_active":   { "type": "boolean", "description": "Optional active flag when updating an existing task." },
                "conversation": {
                    "type": "object",
                    "properties": {
                        "channel_id":  { "type": "string", "description": "Target channel GUID. Optional only when the current job supplies a channel." },
                        "thread_id":   { "type": "string", "description": "Optional target thread GUID." },
                        "source":      { "type": "string", "description": "Source label. Defaults to module_dev." },
                        "client_type": { "type": "string", "description": "Client type stored on steering messages. Defaults to module-dev." }
                    }
                }
            },
            "required": ["source_text", "conversation"]
        }
        """);

    private static JsonElement BuildTaskIdOnlySchema() => ParseSchema("""
        {
            "type": "object",
            "properties": {
                "task_id": { "type": "string", "description": "Task definition GUID." }
            },
            "required": ["task_id"]
        }
        """);

    private static JsonElement BuildTaskValidateSchema() => ParseSchema("""
        {
            "type": "object",
            "properties": {
                "source_text": { "type": "string", "description": "Raw C# task script source." }
            },
            "required": ["source_text"]
        }
        """);

    private static JsonElement BuildTaskCreateSchema() => ParseSchema("""
        {
            "type": "object",
            "properties": {
                "source_text": { "type": "string", "description": "Raw C# task script source. Name/description/parameters/triggers are extracted at parse time." }
            },
            "required": ["source_text"]
        }
        """);

    private static JsonElement BuildTaskUpdateSchema() => ParseSchema("""
        {
            "type": "object",
            "properties": {
                "task_id":     { "type": "string", "description": "Task definition GUID." },
                "source_text": { "type": "string", "description": "Optional new C# source. Re-parses and re-syncs triggers when supplied." },
                "is_active":   { "type": "boolean", "description": "Optional active flag override." }
            },
            "required": ["task_id"]
        }
        """);

    // ── Helpers ───────────────────────────────────────────────────

    private static IConversationSteering ResolveConversationSteering(IServiceProvider sp) =>
        sp.GetService<IConversationSteering>()
            ?? throw new InvalidOperationException(
                "IConversationSteering is not registered. The host must provide conversation steering for MDK workflows.");

    private static SteeringTarget ReadWorkflowSteeringTarget(JsonElement p, AgentJobContext job)
    {
        if (!p.TryGetProperty("conversation", out var conversation)
            || conversation.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "conversation is required so the workflow can steer the next chat turn.");
        }

        return ReadSteeringTarget(conversation, job);
    }

    private static SteeringTarget ReadSteeringTarget(JsonElement p, AgentJobContext job)
    {
        var channelId = TryParseGuid(Str(p, "channel_id"))
            ?? (job.ChannelId != Guid.Empty
                ? job.ChannelId
                : throw new InvalidOperationException("channel_id is required when the current job has no channel."));
        var threadId = TryParseGuid(Str(p, "thread_id"));
        return new SteeringTarget(
            channelId,
            threadId,
            Str(p, "source") ?? "module_dev",
            Str(p, "client_type") ?? "module-dev");
    }

    private static async Task<ConversationSteeringResponse> AddSteeringAsync(
        IServiceProvider sp,
        SteeringTarget target,
        string category,
        string summary,
        string? details,
        CancellationToken ct)
    {
        return await ResolveConversationSteering(sp).AddAsync(
            new ConversationSteeringRequest(
                target.ChannelId,
                target.ThreadId,
                summary,
                target.Source,
                category,
                details is null ? null : TruncateForSteering(details),
                target.ClientType),
            ct);
    }

    private static async Task<IReadOnlyList<WorkflowToolTestResult>> RunWorkflowToolTestsAsync(
        JsonElement p,
        IModuleLifecycleManager lifecycle,
        IServiceProvider sp,
        AgentJobContext job,
        CancellationToken ct)
    {
        if (!p.TryGetProperty("test_tools", out var testsEl) || testsEl.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<WorkflowToolTestResult>();
        foreach (var test in testsEl.EnumerateArray())
        {
            var toolName = Str(test, "tool_name")
                ?? throw new InvalidOperationException("test_tools[].tool_name is required.");
            var toolEntry = lifecycle.FindToolByName(toolName)
                ?? throw new InvalidOperationException($"Tool '{toolName}' not found in any loaded module.");
            var timeoutSeconds = Int(test, "timeout_seconds") ?? 30;

            using var emptyParams = JsonDocument.Parse("{}");
            var parameters = test.TryGetProperty("parameters", out var parametersEl)
                ? parametersEl
                : emptyParams.RootElement;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                var result = await toolEntry.Module.ExecuteToolAsync(
                    toolEntry.ToolName,
                    parameters,
                    job with { JobId = Guid.NewGuid(), ActionKey = toolName },
                    sp,
                    timeout.Token);
                results.Add(new WorkflowToolTestResult(toolName, true, result, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new WorkflowToolTestResult(toolName, false, null, ex.Message));
            }
        }

        return results;
    }

    private static string DetectRuntime(string moduleDir, JsonElement p)
    {
        if (Str(p, "runtime") is { } runtime)
            return runtime.Trim().ToLowerInvariant();

        var manifestPath = Path.Combine(moduleDir, "module.json");
        if (File.Exists(manifestPath))
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (manifest.RootElement.TryGetProperty("runtime", out var runtimeEl)
                && runtimeEl.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(runtimeEl.GetString()))
            {
                return runtimeEl.GetString()!.Trim().ToLowerInvariant();
            }
        }

        return Directory.GetFiles(moduleDir, "*.csproj").Length > 0
            ? ModuleScaffoldService.DotNetRuntime
            : "unknown";
    }

    private static string BuildModuleWorkflowSummary(
        string moduleId,
        string runtime,
        ModuleBuildService.BuildResult? build,
        ModuleRuntimeVerificationResult? verification,
        ModuleStateResponse? state,
        IReadOnlyList<WorkflowToolTestResult> tests)
    {
        var failedTests = tests.Count(test => !test.Success);
        if (failedTests > 0)
            return $"Module '{moduleId}' loaded but {failedTests} workflow test(s) failed.";

        if (state is not null)
            return verification is { Ran: true }
                ? $"Module '{moduleId}' ({runtime}) was verified and hot-loaded successfully."
                : $"Module '{moduleId}' ({runtime}) was applied and hot-loaded successfully.";

        if (verification is { Ran: true })
            return $"Module '{moduleId}' ({runtime}) was applied and verified successfully.";

        if (build is not null)
            return $"Module '{moduleId}' ({runtime}) was applied and built successfully.";

        return $"Module '{moduleId}' ({runtime}) files were applied successfully.";
    }

    private static string BuildModuleWorkflowDetails(
        string moduleId,
        IReadOnlyList<object> files,
        ModuleBuildService.BuildResult? build,
        ModuleRuntimeVerificationResult? verification,
        ModuleStateResponse? state,
        IReadOnlyList<WorkflowToolTestResult> tests)
    {
        return TruncateForSteering(JsonSerializer.Serialize(new
        {
            module_id = moduleId,
            files,
            build,
            verification,
            load = state,
            tests,
        }, ToolJsonOpts));
    }

    private static string FormatBuildDiagnostics(ModuleBuildService.BuildResult build)
    {
        var diagnostics = build.Errors.Count > 0
            ? build.Errors
            : build.Warnings;
        if (diagnostics.Count == 0)
            return TruncateForSteering(build.RawOutput);

        return string.Join(
            Environment.NewLine,
            diagnostics.Select(diagnostic =>
                $"{diagnostic.File}({diagnostic.Line},{diagnostic.Column}) {diagnostic.Code}: {diagnostic.Message}"));
    }

    private static string FormatVerificationDiagnostics(ModuleRuntimeVerificationResult verification)
    {
        var failed = verification.Steps.FirstOrDefault(step => !step.Success);
        if (failed is null)
            return !string.IsNullOrWhiteSpace(verification.Error)
                ? verification.Error
                : "Runtime verification failed without a failing step.";

        return TruncateForSteering(JsonSerializer.Serialize(new
        {
            step = failed.Name,
            command = failed.Command,
            exit_code = failed.ExitCode,
            timed_out = failed.TimedOut,
            error = verification.Error,
            stdout = failed.Stdout,
            stderr = failed.Stderr,
        }, ToolJsonOpts));
    }

    private static string FormatTaskDiagnostics(TaskValidationResponse validation)
    {
        return validation.Diagnostics.Count == 0
            ? "Validation failed without diagnostics."
            : string.Join(
                Environment.NewLine,
                validation.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Severity} {diagnostic.Code} at {diagnostic.Line}:{diagnostic.Column}: {diagnostic.Message}"));
    }

    private static string TruncateForSteering(string value)
    {
        const int max = 15000;
        return value.Length <= max
            ? value
            : value[..max] + $"{Environment.NewLine}... truncated ...";
    }

    private static Guid? TryParseGuid(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return Guid.TryParse(raw, out var value)
            ? value
            : throw new InvalidOperationException($"'{raw}' is not a valid GUID.");
    }

    private sealed record SteeringTarget(
        Guid ChannelId,
        Guid? ThreadId,
        string Source,
        string ClientType);

    private sealed record WorkflowToolTestResult(
        string ToolName,
        bool Success,
        string? Result,
        string? Error);

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
