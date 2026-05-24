using System.Text.Json;
using SharpClaw.Application.Core.Modules.Foreign;

namespace SharpClaw.Modules.EditorCommon;

public static class EditorProtocolContracts
{
    public const string BridgeContractName = "editor_bridge";
    public const string SessionContractName = "editor_session";

    public static IReadOnlyList<ForeignModuleProtocolContractExport> Exports =>
    [
        new(
            BridgeContractName,
            EmptyObjectSchema(),
            [
                new(
                    "get_connection",
                    EmptyObjectSchema(),
                    EmptyObjectSchema(),
                    "Return active connection metadata for an editor session."),
                new(
                    "list_connections",
                    EmptyObjectSchema(),
                    EmptyObjectSchema(),
                    "Return active editor bridge connections."),
                new(
                    "send_request",
                    EmptyObjectSchema(),
                    EmptyObjectSchema(),
                    "Send an editor action request and wait for the extension response."),
            ],
            "WebSocket-based IDE bridge for editor extensions."),
        new(
            SessionContractName,
            EmptyObjectSchema(),
            [
                new("create", EmptyObjectSchema(), EmptyObjectSchema(), "Create an editor session."),
                new("get", EmptyObjectSchema(), EmptyObjectSchema(), "Get an editor session by ID."),
                new("list", EmptyObjectSchema(), EmptyObjectSchema(), "List editor sessions."),
                new("update", EmptyObjectSchema(), EmptyObjectSchema(), "Update an editor session."),
                new("delete", EmptyObjectSchema(), EmptyObjectSchema(), "Delete an editor session."),
                new("list_ids", EmptyObjectSchema(), EmptyObjectSchema(), "List editor session IDs."),
                new("lookup_items", EmptyObjectSchema(), EmptyObjectSchema(), "List editor session lookup items."),
            ],
            "Editor session CRUD management."),
    ];

    public static IReadOnlyList<ForeignModuleProtocolContractRequirement> Requirements =>
    [
        new(BridgeContractName, Description: "WebSocket bridge for IDE communication."),
        new(SessionContractName, Description: "Editor session management."),
    ];

    private static JsonElement EmptyObjectSchema()
    {
        using var document = JsonDocument.Parse("""{"type":"object","properties":{}}""");
        return document.RootElement.Clone();
    }
}
