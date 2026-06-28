using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using SharpClaw.Application.Core.Modules.Foreign;
using SharpClaw.Contracts.DTOs.Editor;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.VS2026Editor;

/// <summary>
/// Default module: Visual Studio 2026 integration — read/write files,
/// diagnostics, builds, and terminal commands through a connected
/// VS 2026 extension. Windows only.
/// </summary>
public sealed class VS2026EditorModule : ISharpClawCoreModule, IForeignModuleProtocolContractModule
{
    private const string EditorBridgeContractName = "editor_bridge";
    private const string EditorSessionContractName = "editor_session";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public string Id => "sharpclaw_vs2026_editor";
    public string DisplayName => "VS 2026 Editor";
    public string ToolPrefix => "vs26";

    // ═══════════════════════════════════════════════════════════════
    // Protocol Contract Dependencies
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ForeignModuleProtocolContractExport> ExportedProtocolContracts => [];

    public IReadOnlyList<ForeignModuleProtocolContractRequirement> RequiredProtocolContracts =>
    [
        new(EditorBridgeContractName, Description: "WebSocket bridge for IDE communication."),
        new(EditorSessionContractName, Description: "Editor session management."),
    ];

    // ═══════════════════════════════════════════════════════════════
    // DI Registration
    // ═══════════════════════════════════════════════════════════════

    public void ConfigureServices(IServiceCollection services)
    {
    }

    // ═══════════════════════════════════════════════════════════════
    // Tool Definitions
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions()
    {
        var editorSession = new ModuleToolPermission(
            IsPerResource: true, Check: null,
            DelegateTo: "AccessEditorSessionAsync");

        return
        [
            new("vs26_read_file",
                "Read file content from a connected VS 2026 instance. "
                + "Optional startLine/endLine for partial reads.",
                BuildSchema("""
                {
                    "type": "object",
                    "properties": {
                        "targetId": { "type": "string", "description": "EditorSession GUID." },
                        "filePath": { "type": "string", "description": "File path relative to workspace root." },
                        "startLine": { "type": "integer", "description": "Optional start line (1-based)." },
                        "endLine": { "type": "integer", "description": "Optional end line (1-based, inclusive)." }
                    },
                    "required": ["targetId", "filePath"]
                }
                """),
                editorSession),

            new("vs26_write_file",
                "Write (overwrite) full content to a file in the connected VS 2026 workspace.",
                BuildSchema("""
                {
                    "type": "object",
                    "properties": {
                        "targetId": { "type": "string", "description": "EditorSession GUID." },
                        "filePath": { "type": "string", "description": "File path relative to workspace root." },
                        "content": { "type": "string", "description": "Full file content to write." }
                    },
                    "required": ["targetId", "filePath", "content"]
                }
                """),
                editorSession),

            new("vs26_get_open_files",
                "List open files/tabs in the connected VS 2026 instance.",
                BuildResourceOnlySchema(),
                editorSession),

            new("vs26_get_selection",
                "Get the active file, cursor position, and selected text in VS 2026.",
                BuildResourceOnlySchema(),
                editorSession),

            new("vs26_get_diagnostics",
                "Get errors and warnings from VS 2026. Optional filePath to scope.",
                BuildSchema("""
                {
                    "type": "object",
                    "properties": {
                        "targetId": { "type": "string", "description": "EditorSession GUID." },
                        "filePath": { "type": "string", "description": "Optional file path to scope results." }
                    },
                    "required": ["targetId"]
                }
                """),
                editorSession),

            new("vs26_apply_edit",
                "Replace a line range with new text in VS 2026.",
                BuildSchema("""
                {
                    "type": "object",
                    "properties": {
                        "targetId": { "type": "string", "description": "EditorSession GUID." },
                        "filePath": { "type": "string", "description": "File path relative to workspace root." },
                        "startLine": { "type": "integer", "description": "Start line (1-based)." },
                        "endLine": { "type": "integer", "description": "End line (1-based, inclusive)." },
                        "newText": { "type": "string", "description": "Replacement text." }
                    },
                    "required": ["targetId", "filePath", "startLine", "endLine", "newText"]
                }
                """),
                editorSession),

            new("vs26_create_file",
                "Create a new file in the VS 2026 workspace.",
                BuildSchema("""
                {
                    "type": "object",
                    "properties": {
                        "targetId": { "type": "string", "description": "EditorSession GUID." },
                        "filePath": { "type": "string", "description": "File path relative to workspace root." },
                        "content": { "type": "string", "description": "Initial file content." }
                    },
                    "required": ["targetId", "filePath"]
                }
                """),
                editorSession),

            new("vs26_delete_file",
                "Delete a file from the VS 2026 workspace.",
                BuildSchema("""
                {
                    "type": "object",
                    "properties": {
                        "targetId": { "type": "string", "description": "EditorSession GUID." },
                        "filePath": { "type": "string", "description": "File path relative to workspace root." }
                    },
                    "required": ["targetId", "filePath"]
                }
                """),
                editorSession),

            new("vs26_show_diff",
                "Show a diff view in VS 2026 for user review (accept/reject).",
                BuildSchema("""
                {
                    "type": "object",
                    "properties": {
                        "targetId": { "type": "string", "description": "EditorSession GUID." },
                        "filePath": { "type": "string", "description": "File path relative to workspace root." },
                        "proposedContent": { "type": "string", "description": "Proposed file content." },
                        "diffTitle": { "type": "string", "description": "Diff view title." }
                    },
                    "required": ["targetId", "filePath", "proposedContent"]
                }
                """),
                editorSession),

            new("vs26_run_build",
                "Trigger a build task in the connected VS 2026 instance and return output.",
                BuildResourceOnlySchema(),
                editorSession),

            new("vs26_run_terminal",
                "Run a command in the VS 2026 integrated terminal.",
                BuildSchema("""
                {
                    "type": "object",
                    "properties": {
                        "targetId": { "type": "string", "description": "EditorSession GUID." },
                        "command": { "type": "string", "description": "Command to run." },
                        "workingDirectory": { "type": "string", "description": "Working directory." }
                    },
                    "required": ["targetId", "command"]
                }
                """),
                editorSession),
        ];
    }

    // ═══════════════════════════════════════════════════════════════
    // Tool Execution
    // ═══════════════════════════════════════════════════════════════

    public async Task<string> ExecuteToolAsync(
        string toolName, JsonElement parameters,
        AgentJobContext job, IServiceProvider scopedServices,
        CancellationToken ct)
    {
        var bridge = ResolveEditorBridge(scopedServices);

        // Strip the "vs26_" prefix to get the WebSocket action name.
        var actionName = toolName.StartsWith("vs26_", StringComparison.Ordinal)
            ? toolName["vs26_".Length..]
            : toolName;

        // Extract targetId (session GUID) from parameters.
        if (!parameters.TryGetProperty("targetId", out var targetProp)
            || !Guid.TryParse(targetProp.GetString(), out var sessionId))
            throw new InvalidOperationException(
                "Missing or invalid 'targetId' parameter.");

        // Validate the session is a VS 2026 session.
        var conn = await GetConnectionAsync(bridge, sessionId, ct);
        if (conn.Exists
            && !string.Equals(conn.EditorKey, "VisualStudio2026", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Session {sessionId} is connected to {conn.EditorKey}, "
                + "not VisualStudio2026. Use the VS Code editor module instead.");
        }

        // Build parameter dict for the WebSocket request (exclude targetId).
        var paramDict = new Dictionary<string, object?>();
        foreach (var prop in parameters.EnumerateObject())
        {
            if (prop.Name.Equals("targetId", StringComparison.OrdinalIgnoreCase))
                continue;
            paramDict[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.GetInt64(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => prop.Value.GetRawText(),
            };
        }

        var response = await SendRequestAsync(
            bridge,
            sessionId,
            actionName,
            paramDict.Count > 0 ? paramDict : null,
            ct);

        if (!response.Success)
            throw new InvalidOperationException(
                $"VS 2026 action '{actionName}' failed: {response.Error}");

        return response.Data ?? $"VS 2026 action '{actionName}' completed.";
    }

    // ── Schema helpers ───────────────────────────────────────

    private static JsonElement BuildSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildResourceOnlySchema() =>
        BuildSchema("""
        {
            "type": "object",
            "properties": {
                "targetId": { "type": "string", "description": "EditorSession GUID." }
            },
            "required": ["targetId"]
        }
        """);

    private static IForeignModuleProtocolContractInvoker ResolveEditorBridge(IServiceProvider services) =>
        services.GetRequiredService<IForeignModuleProtocolContractResolver>()
            .Resolve(EditorBridgeContractName)
        ?? throw new InvalidOperationException("The editor bridge protocol contract is not available.");

    private static async Task<EditorConnectionProtocolResponse> GetConnectionAsync(
        IForeignModuleProtocolContractInvoker bridge,
        Guid sessionId,
        CancellationToken ct)
    {
        var result = await bridge.InvokeAsync(
            "get_connection",
            ToElement(new { sessionId }),
            ct);
        return result.Deserialize<EditorConnectionProtocolResponse>(JsonOptions)
            ?? new EditorConnectionProtocolResponse(false);
    }

    private static async Task<EditorActionResponse> SendRequestAsync(
        IForeignModuleProtocolContractInvoker bridge,
        Guid sessionId,
        string action,
        Dictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        var result = await bridge.InvokeAsync(
            "send_request",
            ToElement(new EditorBridgeSendRequest(sessionId, action, parameters)),
            ct);
        return result.Deserialize<EditorActionResponse>(JsonOptions)
            ?? throw new InvalidOperationException("Editor bridge returned an empty response.");
    }

    private static JsonElement ToElement<T>(T value) =>
        JsonSerializer.SerializeToElement(value, JsonOptions);

    private sealed record EditorBridgeSendRequest(
        Guid SessionId,
        string Action,
        Dictionary<string, object?>? Params);

    private sealed record EditorConnectionProtocolResponse(
        bool Exists,
        string? EditorKey = null);
}
