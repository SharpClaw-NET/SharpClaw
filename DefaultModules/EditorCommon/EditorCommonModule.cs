using System.Text.Json;

using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using SharpClaw.Application.Core.Modules.Foreign;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.EditorCommon.Handlers;
using SharpClaw.Modules.EditorCommon.Services;

namespace SharpClaw.Modules.EditorCommon;

/// <summary>
/// Infrastructure module: shared editor bridge and session services
/// consumed by the VS 2026 and VS Code editor modules.
/// No LLM-callable tools — this module provides only DI services,
/// protocol contract exports, and REST/WebSocket endpoints.
/// </summary>
public sealed class EditorCommonModule : ISharpClawRuntimeModule, IForeignModuleProtocolContractModule
{
    public string Id => "sharpclaw_editor_common";
    public string DisplayName => "Editor Common";
    public string ToolPrefix => "edc";

    // ═══════════════════════════════════════════════════════════════
    // Protocol Contract Exports
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ForeignModuleProtocolContractExport> ExportedProtocolContracts =>
        EditorProtocolContracts.Exports;

    public IReadOnlyList<ForeignModuleProtocolContractRequirement> RequiredProtocolContracts => [];

    public IReadOnlyList<ModuleStorageContractDescriptor> GetStorageContracts() =>
    [
        new(
            Id,
            "editor_sessions",
            StorageOperations(),
            "Editor session records keyed by connected editor workspace.",
            [
                new("name", ModuleStorageIndexValueKind.String),
                new("editorType", ModuleStorageIndexValueKind.String),
                new("workspacePath", ModuleStorageIndexValueKind.String),
                new("editorWorkspace", ModuleStorageIndexValueKind.String),
            ],
            MaxDocumentBytes: 131_072,
            MaxBatchSize: 100),
    ];

    // ═══════════════════════════════════════════════════════════════
    // DI Registration
    // ═══════════════════════════════════════════════════════════════

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<EditorSessionStore>();
        services.AddSingleton<EditorBridgeService>();
        services.AddScoped<EditorSessionService>();
        services.AddScoped<IForeignModuleProtocolContractInvoker, EditorBridgeProtocolContractInvoker>();
        services.AddScoped<IForeignModuleProtocolContractInvoker, EditorSessionProtocolContractInvoker>();
    }

    // ═══════════════════════════════════════════════════════════════
    // Endpoint Registration
    // ═══════════════════════════════════════════════════════════════

    public void MapEndpoints(object app)
    {
        var endpoints = (IEndpointRouteBuilder)app;
        endpoints.MapEditorEndpoints();
        endpoints.MapEditorSessionResourceEndpoints();
    }

    // ═══════════════════════════════════════════════════════════════
    // CLI Commands
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleCliCommand> GetCliCommands() =>
    [
        new(
            Name: "editorsession",
            Aliases: ["editor", "es"],
            Scope: ModuleCliScope.ResourceType,
            Description: "Editor session CRUD",
            UsageLines:
            [
                "resource editorsession list                      List all editor sessions",
                "resource editorsession get <id>                  Show an editor session",
                "resource editorsession delete <id>               Delete an editor session",
            ],
            Handler: HandleEditorSessionResourceCliAsync),
    ];

    public IReadOnlyList<ModuleResourceTypeDescriptor> GetResourceTypeDescriptors() =>
    [
        new("EditorSession", "EditorSession", "AccessEditorSessionAsync", static async (sp, ct) =>
        {
            var svc = sp.GetRequiredService<EditorSessionService>();
            return [.. (await svc.ListAsync(ct)).Select(session => session.Id)];
        },
        LoadLookupItems: static async (sp, ct) =>
        {
            var svc = sp.GetRequiredService<EditorSessionService>();
            return [.. (await svc.ListAsync(ct))
                .Select(session => new ValueTuple<Guid, string>(session.Id, session.Name))];
        }, DefaultResourceKey: "editor"),
    ];

    private static async Task HandleEditorSessionResourceCliAsync(
        string[] args, IServiceProvider sp, CancellationToken ct)
    {
        var ids = sp.GetRequiredService<ICliIdResolver>();

        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage:");
            Console.Error.WriteLine("  resource editorsession list                      List all editor sessions");
            Console.Error.WriteLine("  resource editorsession get <id>                  Show an editor session");
            Console.Error.WriteLine("  resource editorsession delete <id>               Delete an editor session");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Editor sessions are auto-created when an IDE extension connects.");
            Console.Error.WriteLine("Use 'channel defaults <id> set editor <sessionId>' to assign one.");
            return;
        }

        var sub = args[2].ToLowerInvariant();
        var svc = sp.GetRequiredService<EditorSessionService>();

        switch (sub)
        {
            case "get" when args.Length >= 4:
                var session = await svc.GetByIdAsync(ids.Resolve(args[3]), ct);
                if (session is null) { Console.Error.WriteLine("Not found."); return; }
                ids.PrintJson(session);
                break;
            case "get":
                Console.Error.WriteLine("resource editorsession get <id>");
                break;

            case "list":
                ids.PrintJson(await svc.ListAsync(ct));
                break;

            case "delete" when args.Length >= 4:
                Console.WriteLine(
                    await svc.DeleteAsync(ids.Resolve(args[3]))
                        ? "Done." : "Not found.");
                break;
            case "delete":
                Console.Error.WriteLine("resource editorsession delete <id>");
                break;

            default:
                Console.Error.WriteLine($"Unknown command: resource editorsession {sub}");
                break;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Header Tags
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleHeaderTag>? GetHeaderTags() =>
    [
        new ModuleHeaderTag(
            Name: "editor",
            Resolve: static (sp, ct) =>
            {
                var bridge = sp.GetRequiredService<EditorBridgeService>();
                var connections = bridge.GetConnections();
                if (connections.Count == 0)
                    return Task.FromResult("(none)");

                var sessions = string.Join(", ", connections.Select(c =>
                {
                    var s = c.EditorType.ToString();
                    if (c.EditorVersion is not null) s += $" {c.EditorVersion}";
                    if (c.WorkspacePath is not null) s += $" workspace={c.WorkspacePath}";
                    return s;
                }));
                return Task.FromResult(sessions);
            })
    ];

    // ═══════════════════════════════════════════════════════════════
    // No LLM-callable tools
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];

    public Task<string> ExecuteToolAsync(
        string toolName, JsonElement parameters,
        AgentJobContext job, IServiceProvider scopedServices,
        CancellationToken ct) =>
        throw new NotSupportedException(
            $"EditorCommon does not expose LLM-callable tools (received '{toolName}').");

    private static IReadOnlyList<ModuleStorageOperationDescriptor> StorageOperations() =>
    [
        new(ModuleStorageOperations.Get),
        new(ModuleStorageOperations.Upsert),
        new(ModuleStorageOperations.BatchUpsert),
        new(ModuleStorageOperations.Delete),
        new(ModuleStorageOperations.BatchDelete),
        new(ModuleStorageOperations.List),
        new(ModuleStorageOperations.Query),
    ];
}
