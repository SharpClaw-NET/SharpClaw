using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using SharpClaw.Application.Services;
using SharpClaw.Modules.EditorCommon.Services;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.VSCodeEditor;

/// <summary>
/// Default module: Visual Studio Code integration — read/write files,
/// diagnostics, builds, and terminal commands through a connected
/// VS Code extension. All platforms.
/// </summary>
public sealed class VSCodeEditorModule : ISharpClawModule
{
    public string Id => "sharpclaw_vscode_editor";
    public string DisplayName => "VS Code Editor";
    public string ToolPrefix => "vsc";

    // ═══════════════════════════════════════════════════════════════
    // Contract Dependencies
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleContractRequirement> RequiredContracts =>
    [
        new("editor_bridge", typeof(EditorBridgeService),
            Description: "WebSocket bridge for IDE communication"),
        new("editor_session", typeof(EditorSessionService),
            Description: "Editor session management"),
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
            new("vsc_read_file",
                "Read file content from a connected VS Code instance. "
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

            new("vsc_write_file",
                "Write (overwrite) full content to a file in the connected VS Code workspace.",
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

            new("vsc_get_open_files",
                "List open files/tabs in the connected VS Code instance.",
                BuildResourceOnlySchema(),
                editorSession),

            new("vsc_get_selection",
                "Get the active file, cursor position, and selected text in VS Code.",
                BuildResourceOnlySchema(),
                editorSession),

            new("vsc_get_diagnostics",
                "Get errors and warnings from VS Code. Optional filePath to scope.",
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

            new("vsc_apply_edit",
                "Replace a line range with new text in VS Code.",
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

            new("vsc_create_file",
                "Create a new file in the VS Code workspace.",
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

            new("vsc_delete_file",
                "Delete a file from the VS Code workspace.",
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

            new("vsc_show_diff",
                "Show a diff view in VS Code for user review (accept/reject).",
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

            new("vsc_run_build",
                "Trigger a build task in the connected VS Code instance and return output.",
                BuildResourceOnlySchema(),
                editorSession),

            new("vsc_run_terminal",
                "Run a command in the VS Code integrated terminal.",
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
        var bridge = scopedServices.GetRequiredService<EditorBridgeService>();

        // Strip the "vsc_" prefix to get the WebSocket action name.
        var actionName = toolName.StartsWith("vsc_", StringComparison.Ordinal)
            ? toolName["vsc_".Length..]
            : toolName;

        // Extract targetId (session GUID) from parameters.
        if (!parameters.TryGetProperty("targetId", out var targetProp)
            || !Guid.TryParse(targetProp.GetString(), out var sessionId))
            throw new InvalidOperationException(
                "Missing or invalid 'targetId' parameter.");

        // Validate the session is a VS Code session.
        var conn = bridge.GetConnection(sessionId);
        if (conn is not null && conn.EditorType != EditorType.VisualStudioCode)
            throw new InvalidOperationException(
                $"Session {sessionId} is connected to {conn.EditorType}, "
                + "not VisualStudioCode. Use the VS 2026 editor module instead.");

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

        var response = await bridge.SendRequestAsync(
            sessionId, actionName,
            paramDict.Count > 0 ? paramDict : null, ct);

        if (!response.Success)
            throw new InvalidOperationException(
                $"VS Code action '{actionName}' failed: {response.Error}");

        return response.Data ?? $"VS Code action '{actionName}' completed.";
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
}
