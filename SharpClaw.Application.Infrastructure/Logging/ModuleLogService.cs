using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace SharpClaw.Application.Infrastructure.Logging;

/// <summary>
/// A single log entry captured from a module's <see cref="ILogger"/> category.
/// Runtime-only — not persisted to disk.
/// </summary>
public sealed record ModuleLogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Message,
    string? ExceptionType,
    string? StackTrace);

/// <summary>
/// Fixed-capacity ring buffer of <see cref="ModuleLogEntry"/> records.
/// Thread-safe via lock-free <see cref="ConcurrentQueue{T}"/> with a trim pass.
/// </summary>
public sealed class ModuleLogBuffer(int capacity = 1000)
{
    private readonly ConcurrentQueue<ModuleLogEntry> _queue = new();
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Append(ModuleLogEntry entry)
    {
        _queue.Enqueue(entry);
        var c = Interlocked.Increment(ref _count);

        // Trim oldest entries when over capacity.
        while (c > capacity && _queue.TryDequeue(out _))
            c = Interlocked.Decrement(ref _count);
    }

    public IReadOnlyList<ModuleLogEntry> GetEntries(
        DateTimeOffset? since, LogLevel? minLevel, int take)
    {
        var result = new List<ModuleLogEntry>(Math.Min(take, Count));
        foreach (var entry in _queue)
        {
            if (since.HasValue && entry.Timestamp <= since.Value) continue;
            if (minLevel.HasValue && entry.Level < minLevel.Value) continue;
            result.Add(entry);
            if (result.Count >= take) break;
        }
        return result;
    }

    public (int Errors, int Warnings) GetDiagnosticCounts()
    {
        int errors = 0, warnings = 0;
        foreach (var entry in _queue)
        {
            if (entry.Level == LogLevel.Error || entry.Level == LogLevel.Critical) errors++;
            else if (entry.Level == LogLevel.Warning) warnings++;
        }
        return (errors, warnings);
    }

    public void Clear()
    {
        while (_queue.TryDequeue(out _))
            Interlocked.Decrement(ref _count);
    }
}

/// <summary>
/// Singleton service that maintains per-module ring buffers of log entries.
/// Fed by <see cref="ModuleLogSinkProvider"/> and queried by API endpoints.
/// </summary>
public sealed class ModuleLogService
{
    /// <summary>Logger category prefix used for module loggers.</summary>
    internal const string CategoryPrefix = "SharpClaw.Modules.";

    private readonly ConcurrentDictionary<string, ModuleLogBuffer> _buffers = new(StringComparer.Ordinal);

    /// <summary>Append a log entry for a module. Creates the buffer on first use.</summary>
    public void Append(string moduleId, LogLevel level, string message, Exception? exception)
    {
        var buffer = _buffers.GetOrAdd(moduleId, _ => new ModuleLogBuffer());
        buffer.Append(new ModuleLogEntry(
            DateTimeOffset.UtcNow,
            level,
            message,
            exception?.GetType().Name,
            exception?.StackTrace));
    }

    /// <summary>Get filtered log entries for a module.</summary>
    public IReadOnlyList<ModuleLogEntry> GetEntries(
        string moduleId, DateTimeOffset? since = null, LogLevel? minLevel = null, int take = 100)
    {
        return _buffers.TryGetValue(moduleId, out var buffer)
            ? buffer.GetEntries(since, minLevel, take)
            : [];
    }

    /// <summary>Get error and warning counts for a module.</summary>
    public (int Errors, int Warnings) GetDiagnosticCounts(string moduleId)
    {
        return _buffers.TryGetValue(moduleId, out var buffer)
            ? buffer.GetDiagnosticCounts()
            : (0, 0);
    }

    /// <summary>Clear the log buffer for a module.</summary>
    public void Clear(string moduleId)
    {
        if (_buffers.TryGetValue(moduleId, out var buffer))
            buffer.Clear();
    }
}
