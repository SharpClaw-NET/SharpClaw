using System.Text.Json;
using SharpClaw.Application.Core.Modules.Foreign;
using SharpClaw.Contracts.DTOs.Editor;

namespace SharpClaw.Modules.EditorCommon.Services;

public sealed class EditorBridgeProtocolContractInvoker(EditorBridgeService bridge)
    : IForeignModuleProtocolContractInvoker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public string ContractName => EditorProtocolContracts.BridgeContractName;
    public IReadOnlyList<ForeignModuleProtocolContractOperation> Operations =>
        EditorProtocolContracts.Exports
            .Single(contract => contract.ContractName == ContractName)
            .Operations;

    public async Task<JsonElement> InvokeAsync(
        string operation,
        JsonElement parameters,
        CancellationToken ct = default)
    {
        return operation switch
        {
            "get_connection" => ToElement(ToConnectionResponse(
                bridge.GetConnection(ReadGuid(parameters, "sessionId")))),
            "list_connections" => ToElement(bridge.GetConnections().Select(ToConnectionResponse).ToList()),
            "send_request" => ToElement(await bridge.SendRequestAsync(
                ReadGuid(parameters, "sessionId"),
                ReadString(parameters, "action"),
                ReadParams(parameters),
                ct)),
            _ => throw new NotSupportedException(
                $"Editor bridge operation '{operation}' is not registered."),
        };
    }

    private static Dictionary<string, object?>? ReadParams(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("params", out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Property 'params' must be a JSON object.");

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number when property.Value.TryGetInt64(out var integer) => integer,
                JsonValueKind.Number => property.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => property.Value.Clone(),
            };
        }

        return result.Count == 0 ? null : result;
    }

    private static EditorConnectionProtocolResponse ToConnectionResponse(EditorConnection? connection) =>
        connection is null
            ? new EditorConnectionProtocolResponse(false)
            : new EditorConnectionProtocolResponse(
                true,
                connection.ConnectionId,
                connection.SessionId,
                connection.EditorType.ToString(),
                connection.EditorVersion,
                connection.WorkspacePath,
                connection.Socket.State.ToString(),
                connection.ConnectedAt);

    private static Guid ReadGuid(JsonElement parameters, string propertyName)
    {
        if (!parameters.TryGetProperty(propertyName, out var property))
            throw new ArgumentException($"Property '{propertyName}' is required.");

        return property.ValueKind == JsonValueKind.String
            ? Guid.Parse(property.GetString() ?? "")
            : property.GetGuid();
    }

    private static string ReadString(JsonElement parameters, string propertyName)
    {
        if (!parameters.TryGetProperty(propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new ArgumentException($"Property '{propertyName}' is required.");
        }

        return property.GetString()
            ?? throw new ArgumentException($"Property '{propertyName}' is required.");
    }

    private static JsonElement ToElement<T>(T value) =>
        JsonSerializer.SerializeToElement(value, JsonOptions);

    private sealed record EditorConnectionProtocolResponse(
        bool Exists,
        string? ConnectionId = null,
        Guid? SessionId = null,
        string? EditorKey = null,
        string? EditorVersion = null,
        string? WorkspacePath = null,
        string? SocketState = null,
        DateTimeOffset? ConnectedAt = null);
}

public sealed class EditorSessionProtocolContractInvoker(EditorSessionService sessions)
    : IForeignModuleProtocolContractInvoker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public string ContractName => EditorProtocolContracts.SessionContractName;
    public IReadOnlyList<ForeignModuleProtocolContractOperation> Operations =>
        EditorProtocolContracts.Exports
            .Single(contract => contract.ContractName == ContractName)
            .Operations;

    public async Task<JsonElement> InvokeAsync(
        string operation,
        JsonElement parameters,
        CancellationToken ct = default)
    {
        return operation switch
        {
            "create" => ToElement(await sessions.CreateAsync(
                Deserialize<CreateEditorSessionRequest>(parameters),
                ct)),
            "get" => ToElement(await sessions.GetByIdAsync(ReadGuid(parameters), ct)),
            "list" => ToElement(await sessions.ListAsync(ct)),
            "update" => ToElement(await sessions.UpdateAsync(
                ReadGuid(parameters),
                Deserialize<UpdateEditorSessionRequest>(
                    parameters.TryGetProperty("request", out var request)
                        ? request
                        : parameters),
                ct)),
            "delete" => ToElement(await sessions.DeleteAsync(ReadGuid(parameters), ct)),
            "list_ids" => ToElement((await sessions.ListAsync(ct)).Select(session => session.Id).ToList()),
            "lookup_items" => ToElement((await sessions.ListAsync(ct))
                .Select(session => new EditorSessionLookupItem(session.Id, session.Name))
                .ToList()),
            _ => throw new NotSupportedException(
                $"Editor session operation '{operation}' is not registered."),
        };
    }

    private static Guid ReadGuid(JsonElement parameters, string propertyName = "id")
    {
        if (!parameters.TryGetProperty(propertyName, out var property))
            throw new ArgumentException($"Property '{propertyName}' is required.");

        return property.ValueKind == JsonValueKind.String
            ? Guid.Parse(property.GetString() ?? "")
            : property.GetGuid();
    }

    private static T Deserialize<T>(JsonElement parameters) =>
        parameters.Deserialize<T>(JsonOptions)
        ?? throw new ArgumentException($"Could not deserialize {typeof(T).Name}.");

    private static JsonElement ToElement<T>(T value) =>
        JsonSerializer.SerializeToElement(value, JsonOptions);

    private sealed record EditorSessionLookupItem(Guid Id, string Name);
}
