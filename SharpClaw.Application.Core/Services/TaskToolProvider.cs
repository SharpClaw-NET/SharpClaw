using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.Core.Clients;
using SharpClaw.Application.Infrastructure.Models.Access;
using SharpClaw.Application.Infrastructure.Tasks.Models;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

/// <summary>
/// Converts active <see cref="TaskDefinitionDB"/> records into
/// <see cref="ChatToolDefinition"/> entries that agents can invoke as tools
/// during a chat turn.
/// <para>
/// This is the single place that determines tool schema for platform-level task
/// exposure.  It is intentionally separate from the in-flight task-context
/// tools managed by <see cref="TaskSharedData"/> (those are task-internal
/// hooks only available while a task is executing).
/// </para>
/// </summary>
public sealed class TaskToolProvider(SharpClawDbContext db)
{
    // Prefix all task tool names so callers can identify them as a family.
    internal const string ToolPrefix = "task_invoke__";

    /// <summary>
    /// Build tool definitions for all active task definitions.  The caller
    /// is responsible for checking agent permissions before adding these to
    /// a chat request — use <see cref="AgentActionService.EvaluateGlobalFlagByKeyAsync"/>
    /// with <see cref="TaskPermissionKeys.CanInvokeTasksAsTool"/>.
    /// </summary>
    public async Task<IReadOnlyList<ChatToolDefinition>> GetToolDefinitionsAsync(
        CancellationToken ct = default)
    {
        var definitions = await db.TaskDefinitions
            .Where(d => d.IsActive)
            .OrderBy(d => d.Name)
            .ToListAsync(ct);

        return definitions.Select(BuildToolDefinition).ToList();
    }

    /// <summary>
    /// Parse the task tool name from an agent tool call.  Returns the task
    /// definition name if the call targets a task tool; otherwise null.
    /// </summary>
    public static string? TryParseTaskName(string toolName)
        => toolName.StartsWith(ToolPrefix, StringComparison.Ordinal)
            ? toolName[ToolPrefix.Length..]
            : null;

    private static ChatToolDefinition BuildToolDefinition(
        Infrastructure.Models.Tasks.TaskDefinitionDB definition)
    {
        var parameters = DeserializeParameters(definition.ParametersJson);
        var schema = BuildSchema(parameters);
        return new ChatToolDefinition(
            $"{ToolPrefix}{definition.Name}",
            BuildDescription(definition.Name, definition.Description),
            BuildJsonSchema(JsonSerializer.Serialize(schema)));
    }

    private static string BuildDescription(string name, string? description)
    {
        if (!string.IsNullOrWhiteSpace(description))
            return $"Execute task '{name}': {description}";
        return $"Execute task '{name}'.";
    }

    private static Dictionary<string, object> BuildSchema(
        IReadOnlyList<TaskParameterDefinition> parameters)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var p in parameters)
        {
            var prop = new Dictionary<string, string>
            {
                ["type"] = MapTypeToJsonType(p.TypeName),
            };
            if (!string.IsNullOrEmpty(p.Description))
                prop["description"] = p.Description;
            if (!string.IsNullOrEmpty(p.DefaultValue))
                prop["default"] = p.DefaultValue;

            properties[p.Name] = prop;

            if (p.IsRequired)
                required.Add(p.Name);
        }

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (required.Count > 0)
            schema["required"] = required;

        return schema;
    }

    private static string MapTypeToJsonType(string typeName) => typeName.ToLowerInvariant() switch
    {
        "int" or "long" or "float" or "double" or "decimal" => "number",
        "bool" or "boolean" => "boolean",
        _ => "string",
    };

    private static IReadOnlyList<TaskParameterDefinition> DeserializeParameters(
        string? json)
    {
        if (string.IsNullOrEmpty(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<TaskParameterDefinition>>(json)
                   ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static JsonElement BuildJsonSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
