using System.Collections.Concurrent;
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
    public string WriteBig(string? id, string title, string content)
    {
        id ??= Guid.NewGuid().ToString("N")[..8];
        _bigData[id] = new BigDataEntry(id, title, content, DateTimeOffset.UtcNow);
        return id;
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
    // Custom tool-call hooks ([ToolCall("name")])
    // ═══════════════════════════════════════════════════════════════

    private readonly ConcurrentDictionary<string, Func<JsonElement?, CancellationToken, Task<string>>>
        _toolCallbacks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Dynamic tool definitions generated from hooks.</summary>
    public IReadOnlyList<ChatToolDefinition> CustomToolDefinitions { get; set; } = [];

    /// <summary>Register a custom tool callback.</summary>
    public void RegisterToolHook(string name, Func<JsonElement?, CancellationToken, Task<string>> callback) =>
        _toolCallbacks[name] = callback;

    /// <summary>Try to invoke a registered custom tool.</summary>
    public bool TryGetToolHook(string name, out Func<JsonElement?, CancellationToken, Task<string>> callback) =>
        _toolCallbacks.TryGetValue(name, out callback!);

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
}

/// <summary>A single entry in the big shared data store.</summary>
public sealed record BigDataEntry(
    string Id,
    string Title,
    string Content,
    DateTimeOffset CreatedAt);
