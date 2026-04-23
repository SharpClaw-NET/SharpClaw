using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using SharpClaw.Application.Core.Clients;
using SharpClaw.Application.Infrastructure.Tasks.Models;

namespace SharpClaw.Application.Services;

/// <summary>
/// Static registry of per-instance shared data stores.  Both the
/// <see cref="TaskOrchestrator"/> and <see cref="ChatService"/> access
/// these stores — the orchestrator creates/removes them, and ChatService
/// reads/writes via task-specific tool calls.
/// </summary>
public static class TaskSharedData
{
    private static readonly ConcurrentDictionary<Guid, TaskSharedDataStore> _stores = new();

    /// <summary>Get or create the store for an instance.</summary>
    public static TaskSharedDataStore GetOrCreate(Guid instanceId) =>
        _stores.GetOrAdd(instanceId, _ => new TaskSharedDataStore());

    /// <summary>Get the store if it exists, or <c>null</c>.</summary>
    public static TaskSharedDataStore? Get(Guid instanceId) =>
        _stores.TryGetValue(instanceId, out var s) ? s : null;

    /// <summary>Remove and discard the store for a completed instance.</summary>
    public static void Remove(Guid instanceId) =>
        _stores.TryRemove(instanceId, out _);
}

/// <summary>
/// Holds task-scoped shared state that agents can read/write through
/// tool calls during task execution.
/// </summary>
public sealed class TaskSharedDataStore
{
    // ═══════════════════════════════════════════════════════════════
    // Change notification
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Callback invoked after every shared data mutation (light or big).
    /// The orchestrator wires this to persist snapshots and log changes.
    /// Parameters: (<c>changeDescription</c>, <c>lightSnapshot</c>,
    /// <c>bigSnapshotJson</c>).
    /// </summary>
    public Func<string, string?, string?, Task>? OnSharedDataChanged { get; set; }

    /// <summary>
    /// Builds a JSON snapshot of all big-data entries for persistence.
    /// </summary>
    public string? BuildBigDataSnapshotJson()
    {
        if (_bigData.IsEmpty) return null;
        return JsonSerializer.Serialize(_bigData.Values.Select(e => new
        {
            e.Id,
            e.Title,
            e.Content,
            e.CreatedAt
        }));
    }

    // ═══════════════════════════════════════════════════════════════
    // Light shared data  — fully visible in the chat header
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Max total words for the light-data text.</summary>
    public const int MaxLightDataWords = 500;

    private readonly Lock _lightLock = new();
    private string? _lightData;

    /// <summary>Current light-data text (<c>null</c> when empty).</summary>
    public string? LightData
    {
        get { lock (_lightLock) return _lightData; }
    }

    /// <summary>
    /// Set the light-data text.  Returns <c>false</c> if
    /// <paramref name="text"/> exceeds the 500-word limit.
    /// </summary>
    public bool TrySetLight(string text)
    {
        if (CountWords(text) > MaxLightDataWords)
            return false;

        lock (_lightLock) _lightData = text;
        return true;
    }

    /// <summary>Clear the light-data text.</summary>
    public void ClearLight()
    {
        lock (_lightLock) _lightData = null;
    }

    // ═══════════════════════════════════════════════════════════════
    // Big shared data  — only IDs shown in header, full via tool call
    // ═══════════════════════════════════════════════════════════════

    private readonly ConcurrentDictionary<string, BigDataEntry> _bigData = new(StringComparer.Ordinal);

    /// <summary>Snapshot of big-data entry metadata (keys only).</summary>
    public IReadOnlyDictionary<string, BigDataEntry> BigData => _bigData;

    /// <summary>
    /// Add or overwrite a big-data entry.  Returns the ID (same as
    /// <paramref name="id"/> when non-null, otherwise auto-generated).
    /// </summary>
    public const int MaxBigDataCharacters = 200_000;

    /// <summary>
    /// Add or overwrite a big-data entry. Returns <c>false</c> when the
    /// content exceeds the supported size limit.
    /// </summary>
    public bool TryWriteBig(string? id, string title, string content, out string resultId)
    {
        resultId = id ?? Guid.NewGuid().ToString("N")[..8];
        if (content.Length > MaxBigDataCharacters)
            return false;

        _bigData[resultId] = new BigDataEntry(resultId, title, content, DateTimeOffset.UtcNow);
        return true;
    }

    /// <summary>Read a big-data entry by ID.</summary>
    public BigDataEntry? GetBig(string id) =>
        _bigData.TryGetValue(id, out var e) ? e : null;

    /// <summary>List all big-data entry IDs and titles.</summary>
    public IReadOnlyList<(string Id, string Title)> ListBig() =>
        _bigData.Values.Select(e => (e.Id, e.Title)).ToList();

    /// <summary>Remove a big-data entry.</summary>
    public bool RemoveBig(string id) =>
        _bigData.TryRemove(id, out _);

    // ═══════════════════════════════════════════════════════════════
    // Agent output
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Format annotation from the task definition that restricts what
    /// the agent may output.  <c>null</c> means agent output is not
    /// allowed for this task.
    /// </summary>
    public string? AllowedOutputFormat { get; set; }

    /// <summary>
    /// Callback invoked when an agent writes output via the
    /// <c>task_output</c> tool.  Set by the orchestrator.
    /// </summary>
    public Func<string, Task>? OnAgentOutput { get; set; }

    // ═══════════════════════════════════════════════════════════════
    // Task introspection
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Task name for introspection.</summary>
    public string? TaskName { get; set; }

    /// <summary>Task description for introspection.</summary>
    public string? TaskDescription { get; set; }

    /// <summary>Raw source text of the task definition.</summary>
    public string? TaskSourceText { get; set; }

    /// <summary>Resolved parameter values as a JSON string.</summary>
    public string? TaskParametersJson { get; set; }

    // ═══════════════════════════════════════════════════════════════
    // Unified task tool descriptors
    // ═══════════════════════════════════════════════════════════════

    private readonly ConcurrentDictionary<string, TaskToolDescriptor> _tools = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>All registered task-scoped tools for this instance.</summary>
    public IReadOnlyList<ChatToolDefinition> GetToolDefinitions() =>
        _tools.Values
            .Select(t => t.Definition)
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Register the built-in task tools for this store.</summary>
    public void RegisterBuiltInTools()
    {
        RegisterTool(new TaskToolDescriptor(
            new ChatToolDefinition(
                "task_write_light_data",
                "Write to light shared data (visible in header, max 500 words). Replaces previous.",
                BuildJsonSchema("""
                    {
                        "type": "object",
                        "properties": {
                            "text": { "type": "string", "description": "Text (max 500 words)." }
                        },
                        "required": ["text"]
                    }
                    """)),
            async (args, _) =>
            {
                var text = args?.GetProperty("text").GetString() ?? string.Empty;
                var ok = TrySetLight(text);
                if (ok && OnSharedDataChanged is not null)
                    await OnSharedDataChanged(
                        $"Light data written ({CountWords(text)} words)",
                        LightData,
                        BuildBigDataSnapshotJson()).ConfigureAwait(false);
                return ok
                    ? "OK: light shared data written."
                    : "Error: text exceeds the 500-word limit for light shared data.";
            }));

        RegisterTool(new TaskToolDescriptor(
            new ChatToolDefinition(
                "task_read_light_data",
                "Read the current light shared data text.",
                BuildJsonSchema("""
                    {
                        "type": "object",
                        "properties": {}
                    }
                    """)),
            (args, ct) => Task.FromResult(LightData ?? "(empty)")));

        RegisterTool(new TaskToolDescriptor(
            new ChatToolDefinition(
                "task_write_big_data",
                "Write a large entry to big shared data. Only ID+title in header; use task_read_big_data for content.",
                BuildJsonSchema("""
                    {
                        "type": "object",
                        "properties": {
                            "id": { "type": "string", "description": "Entry ID (auto-generated if omitted)." },
                            "title": { "type": "string", "description": "Short title." },
                            "content": { "type": "string", "description": "Full content." }
                        },
                        "required": ["title", "content"]
                    }
                    """)),
            async (args, _) =>
            {
                var title = args?.GetProperty("title").GetString() ?? "Untitled";
                var content = args?.GetProperty("content").GetString() ?? string.Empty;
                var id = args?.TryGetProperty("id", out var idElement) == true ? idElement.GetString() : null;
                var ok = TryWriteBig(id, title, content, out var resultId);
                if (!ok)
                    return $"Error: big-data content exceeds the {MaxBigDataCharacters} character limit.";

                if (OnSharedDataChanged is not null)
                    await OnSharedDataChanged(
                        $"Big data '{resultId}' written (title: {title}, {content.Length} chars)",
                        LightData,
                        BuildBigDataSnapshotJson()).ConfigureAwait(false);

                return $"OK: big-data entry '{resultId}' written (title: {title}, {content.Length} chars).";
            }));

        RegisterTool(new TaskToolDescriptor(
            new ChatToolDefinition(
                "task_read_big_data",
                "Read a big shared data entry by ID.",
                BuildJsonSchema("""
                    {
                        "type": "object",
                        "properties": {
                            "id": { "type": "string", "description": "Entry ID." }
                        },
                        "required": ["id"]
                    }
                    """)),
            (args, ct) =>
            {
                var id = args?.GetProperty("id").GetString() ?? string.Empty;
                var entry = GetBig(id);
                return Task.FromResult(entry is not null
                    ? $"[{entry.Id}] {entry.Title}\n{entry.Content}"
                    : $"Big-data entry '{id}' not found.");
            }));

        RegisterTool(new TaskToolDescriptor(
            new ChatToolDefinition(
                "task_list_big_data",
                "List big shared data entries (IDs and titles).",
                BuildJsonSchema("""
                    {
                        "type": "object",
                        "properties": {}
                    }
                    """)),
            (args, ct) =>
            {
                var entries = ListBig();
                return Task.FromResult(entries.Count == 0
                    ? "(no big-data entries)"
                    : string.Join("\n", entries.Select(e => $"- {e.Id}: {e.Title}")));
            }));

        RegisterTool(new TaskToolDescriptor(
            new ChatToolDefinition(
                "task_view_info",
                "View task metadata (name, description, parameters, output format).",
                BuildJsonSchema("""
                    {
                        "type": "object",
                        "properties": {}
                    }
                    """)),
            (args, ct) =>
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Task: {TaskName}");
                if (TaskDescription is not null)
                    sb.AppendLine($"Description: {TaskDescription}");
                if (TaskParametersJson is not null)
                    sb.AppendLine($"Parameters: {TaskParametersJson}");
                if (AllowedOutputFormat is not null)
                    sb.AppendLine($"Agent output format: {AllowedOutputFormat}");
                return Task.FromResult(sb.ToString());
            }));

        RegisterTool(new TaskToolDescriptor(
            new ChatToolDefinition(
                "task_view_source",
                "View the task definition source code.",
                BuildJsonSchema("""
                    {
                        "type": "object",
                        "properties": {}
                    }
                    """)),
            (args, ct) => Task.FromResult(TaskSourceText ?? "(source not available)")));

        if (AllowedOutputFormat is not null)
        {
            RegisterTool(new TaskToolDescriptor(
                new ChatToolDefinition(
                    "task_output",
                    "Write structured output to the task. Format must match [AgentOutput] annotation.",
                    BuildJsonSchema("""
                        {
                            "type": "object",
                            "properties": {
                                "data": { "type": "string", "description": "Output data." }
                            },
                            "required": ["data"]
                        }
                        """)),
                async (args, _) =>
                {
                    if (AllowedOutputFormat is null)
                        return "Error: task_output is not enabled for this task. The task must declare [AgentOutput(\"format\")].";

                    var data = args?.GetProperty("data").GetString() ?? string.Empty;
                    if (OnAgentOutput is not null)
                        await OnAgentOutput(data).ConfigureAwait(false);
                    return "OK: output written to task.";
                }));
        }
    }

    /// <summary>Register a custom task tool hook.</summary>
    public void RegisterCustomToolHook(TaskToolCallHook hook, Func<JsonElement?, CancellationToken, Task<string>> callback)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var param in hook.Parameters)
        {
            var prop = new Dictionary<string, string> { ["type"] = MapTypeToJsonType(param.TypeName) };
            if (param.Description is not null)
                prop["description"] = param.Description;
            properties[param.Name] = prop;
            required.Add(param.Name);
        }

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (required.Count > 0)
            schema["required"] = required;

        RegisterTool(new TaskToolDescriptor(
            new ChatToolDefinition(
                hook.Name,
                hook.Description ?? $"Custom task tool: {hook.Name}",
                BuildJsonSchema(JsonSerializer.Serialize(schema))),
            callback));
    }

    /// <summary>Try to invoke a registered task tool.</summary>
    public async Task<(bool Handled, string? Result)> TryInvokeToolAsync(string name, JsonElement? args, CancellationToken ct)
    {
        if (!_tools.TryGetValue(name, out var descriptor))
            return (false, null);

        return (true, await descriptor.Callback(args, ct).ConfigureAwait(false));
    }

    private void RegisterTool(TaskToolDescriptor descriptor)
        => _tools[descriptor.Definition.Name] = descriptor;

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var count = 0;
        var inWord = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                count++;
            }
        }
        return count;
    }

    private static string MapTypeToJsonType(string csharpType) => csharpType switch
    {
        "int" or "long" or "double" or "float" or "decimal" => "number",
        "bool" => "boolean",
        _ => "string"
    };

    private static JsonElement BuildJsonSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

/// <summary>A single entry in the big shared data store.</summary>
public sealed record BigDataEntry(
    string Id,
    string Title,
    string Content,
    DateTimeOffset CreatedAt);

/// <summary>
/// Unified descriptor for any task-scoped tool, whether built-in or defined
/// through a task hook.
/// </summary>
public sealed record TaskToolDescriptor(
    ChatToolDefinition Definition,
    Func<JsonElement?, CancellationToken, Task<string>> Callback);
