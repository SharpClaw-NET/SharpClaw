using System.Collections.Concurrent;

using SharpClaw.Contracts.Modules;

namespace SharpClaw.Application.Core.Modules;

/// <summary>
/// Lock-free per-tool execution counters. Updated by the pipeline after every
/// module tool call. Thread-safe via <see cref="Interlocked"/> operations.
/// Registered as a singleton in DI.
/// </summary>
public sealed class ModuleMetricsCollector
{
    private readonly ConcurrentDictionary<string, ToolCounters> _counters = new(StringComparer.Ordinal);

    /// <summary>Record a successful tool execution.</summary>
    public void RecordSuccess(string prefixedToolName, TimeSpan duration)
    {
        var c = _counters.GetOrAdd(prefixedToolName, _ => new ToolCounters());
        Interlocked.Increment(ref c.TotalCalls);
        Interlocked.Increment(ref c.SuccessCount);
        Interlocked.Add(ref c.TotalDurationTicks, duration.Ticks);
        Volatile.Write(ref c.LastCallTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    /// <summary>Record a failed tool execution (exception).</summary>
    public void RecordFailure(string prefixedToolName)
    {
        var c = _counters.GetOrAdd(prefixedToolName, _ => new ToolCounters());
        Interlocked.Increment(ref c.TotalCalls);
        Interlocked.Increment(ref c.FailureCount);
        Volatile.Write(ref c.LastFailureTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    /// <summary>Record a timed-out tool execution.</summary>
    public void RecordTimeout(string prefixedToolName)
    {
        var c = _counters.GetOrAdd(prefixedToolName, _ => new ToolCounters());
        Interlocked.Increment(ref c.TotalCalls);
        Interlocked.Increment(ref c.TimeoutCount);
        Volatile.Write(ref c.LastFailureTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    /// <summary>Get a snapshot of metrics for a single tool.</summary>
    public ModuleToolMetrics? GetToolMetrics(string prefixedToolName)
    {
        if (!_counters.TryGetValue(prefixedToolName, out var c)) return null;
        return BuildSnapshot(prefixedToolName, c);
    }

    /// <summary>Get aggregated metrics for a module (all its tools).</summary>
    public ModuleMetricsSnapshot GetModuleMetrics(
        string moduleId, string displayName, string toolPrefix)
    {
        var prefix = $"{toolPrefix}_";
        var toolMetrics = _counters
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(kv => BuildSnapshot(kv.Key, kv.Value))
            .ToList();

        var totalCalls = toolMetrics.Sum(t => t.TotalCalls);
        var successCount = toolMetrics.Sum(t => t.SuccessCount);
        var failureCount = toolMetrics.Sum(t => t.FailureCount);
        var timeoutCount = toolMetrics.Sum(t => t.TimeoutCount);
        var totalDuration = new TimeSpan(toolMetrics.Sum(t => t.TotalDuration.Ticks));
        var avgDuration = successCount > 0
            ? new TimeSpan(totalDuration.Ticks / successCount)
            : TimeSpan.Zero;

        return new ModuleMetricsSnapshot(
            moduleId, displayName,
            totalCalls, successCount, failureCount, timeoutCount,
            totalDuration, avgDuration,
            toolMetrics.Count > 0 ? toolMetrics.Max(t => t.LastCallAt) : null,
            toolMetrics.Count > 0 ? toolMetrics.Max(t => t.LastFailureAt) : null,
            toolMetrics);
    }

    /// <summary>Reset all counters. Used for testing or admin-triggered reset.</summary>
    public void Reset() => _counters.Clear();

    /// <summary>Reset counters for a single module (by prefix).</summary>
    public void ResetModule(string toolPrefix)
    {
        var prefix = $"{toolPrefix}_";
        foreach (var key in _counters.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .ToList())
        {
            _counters.TryRemove(key, out _);
        }
    }

    private static ModuleToolMetrics BuildSnapshot(string name, ToolCounters c)
    {
        var total = Volatile.Read(ref c.TotalCalls);
        var success = Volatile.Read(ref c.SuccessCount);
        var failure = Volatile.Read(ref c.FailureCount);
        var timeout = Volatile.Read(ref c.TimeoutCount);
        var durationTicks = Volatile.Read(ref c.TotalDurationTicks);
        var lastCallTicks = Volatile.Read(ref c.LastCallTicks);
        var lastFailTicks = Volatile.Read(ref c.LastFailureTicks);

        var totalDuration = new TimeSpan(durationTicks);
        var avgDuration = success > 0
            ? new TimeSpan(durationTicks / success)
            : TimeSpan.Zero;

        return new ModuleToolMetrics(
            name, total, success, failure, timeout,
            totalDuration, avgDuration,
            lastCallTicks > 0 ? new DateTimeOffset(lastCallTicks, TimeSpan.Zero) : null,
            lastFailTicks > 0 ? new DateTimeOffset(lastFailTicks, TimeSpan.Zero) : null);
    }

    /// <summary>Mutable counters — fields accessed only via Interlocked/Volatile.</summary>
    private sealed class ToolCounters
    {
        public long TotalCalls;
        public long SuccessCount;
        public long FailureCount;
        public long TimeoutCount;
        public long TotalDurationTicks;
        public long LastCallTicks;
        public long LastFailureTicks;
    }
}
