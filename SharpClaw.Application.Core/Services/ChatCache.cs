using System.Text;
using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;

namespace SharpClaw.Application.Services;

public sealed class ChatCache(IConfiguration configuration)
{
    private const long DefaultMaxMegabytes = 2048;
    private const long BytesPerMegabyte = 1024 * 1024;

    public const string PrefixHeaderUser = "chat:header:user:";
    public const string PrefixHeaderAgentSuffix = "chat:header:agent-suffix:";
    public const string PrefixThreadHistoryLimits = "chat:thread-history-limits:";
    public const string PrefixEffectiveTools = "chat:effective-tools:";
    public const string PrefixDefaultResourceResolution = "chat:default-resource:";
    public const string PrefixJobLogs = "job:logs:";

    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _fifo = new();
    private long _currentBytes;

    public long CurrentBytes
    {
        get
        {
            lock (_gate)
                return _currentBytes;
        }
    }

    public long MaxBytes => GetMaxBytes();

    public async Task<T?> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T?>> factory,
        Func<T, long>? estimateSize = null,
        CancellationToken ct = default)
        where T : class
    {
        if (TryGet<T>(key, out var cached))
            return cached;

        var value = await factory(ct);
        if (value is null)
            return null;

        Set(key, value, estimateSize);

        if (TryGet<T>(key, out var stored))
            return stored;

        return value;
    }

    public bool TryGet<T>(string key, out T? value)
        where T : class
    {
        if (GetMaxBytes() <= 0)
        {
            value = null;
            return false;
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry)
                && entry.Value is T typed)
            {
                value = typed;
                return true;
            }
        }

        value = null;
        return false;
    }

    public void Set<T>(string key, T value, Func<T, long>? estimateSize = null)
        where T : class
    {
        var maxBytes = GetMaxBytes();
        if (maxBytes <= 0)
            return;

        var sizeBytes = Math.Max(1, estimateSize?.Invoke(value) ?? EstimateObjectSize(value));
        if (sizeBytes > maxBytes)
            return;

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                _currentBytes -= existing.SizeBytes;
                _entries[key] = new CacheEntry(value, sizeBytes);
                _currentBytes += sizeBytes;
            }
            else
            {
                _entries[key] = new CacheEntry(value, sizeBytes);
                _fifo.Enqueue(key);
                _currentBytes += sizeBytes;
            }

            EvictToBudget(maxBytes);
        }
    }

    public void Mutate<T>(string key, Func<T, T> mutate, Func<T, long>? estimateSize = null)
        where T : class
    {
        var maxBytes = GetMaxBytes();
        if (maxBytes <= 0)
            return;

        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry)
                || entry.Value is not T typed)
            {
                return;
            }

            var next = mutate(typed);
            var nextSize = Math.Max(1, estimateSize?.Invoke(next) ?? EstimateObjectSize(next));
            if (nextSize > maxBytes)
            {
                _entries.Remove(key);
                _currentBytes -= entry.SizeBytes;
                return;
            }

            _entries[key] = new CacheEntry(next, nextSize);
            _currentBytes += nextSize - entry.SizeBytes;
            EvictToBudget(maxBytes);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _fifo.Clear();
            _currentBytes = 0;
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            RemoveEntry(key);
            CompactFifoIfNeeded();
        }
    }

    public void RemoveByPrefix(string prefix)
    {
        lock (_gate)
        {
            foreach (var key in _entries.Keys
                         .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                         .ToList())
            {
                RemoveEntry(key);
            }

            CompactFifoIfNeeded();
        }
    }

    public void RemoveHeaderAgentSuffixesForAgent(Guid agentId) =>
        RemoveByPrefix($"{PrefixHeaderAgentSuffix}{agentId:D}:");

    public void RemoveHeaderAgentSuffixesForChannel(Guid channelId) =>
        RemoveWhere(key =>
            key.StartsWith(PrefixHeaderAgentSuffix, StringComparison.Ordinal)
            && KeyHasGuidSegment(key, PrefixHeaderAgentSuffix.Length + 37, channelId));

    public void RemoveEffectiveToolsForAgent(Guid agentId) =>
        RemoveByPrefix($"{PrefixEffectiveTools}{agentId:D}:");

    public void RemoveDefaultResourceResolutionForChannel(Guid channelId) =>
        RemoveByPrefix($"{PrefixDefaultResourceResolution}{channelId:D}:");

    public void RemoveDefaultResourceResolutionForAgent(Guid agentId) =>
        RemoveWhere(key =>
            key.StartsWith(PrefixDefaultResourceResolution, StringComparison.Ordinal)
            && KeyHasGuidSegment(key, PrefixDefaultResourceResolution.Length + 37, agentId));

    public Task<IReadOnlyList<AgentJobLogResponse>?> GetJobLogsAsync(
        Guid jobId,
        Func<CancellationToken, Task<IReadOnlyList<AgentJobLogResponse>>> factory,
        CancellationToken ct)
        => GetOrCreateAsync(
            KeyJobLogs(jobId),
            async innerCt => await factory(innerCt),
            EstimateJobLogs,
            ct);

    public bool TryGetJobLogs(Guid jobId, out IReadOnlyList<AgentJobLogResponse>? logs)
        => TryGet<IReadOnlyList<AgentJobLogResponse>>(KeyJobLogs(jobId), out logs);

    public void SetJobLogs(Guid jobId, IReadOnlyList<AgentJobLogResponse> logs)
        => Set<IReadOnlyList<AgentJobLogResponse>>(KeyJobLogs(jobId), logs.ToArray(), EstimateJobLogs);

    public void AppendJobLogIfCached(Guid jobId, AgentJobLogResponse log)
    {
        Mutate<IReadOnlyList<AgentJobLogResponse>>(
            KeyJobLogs(jobId),
            logs =>
            {
                var next = new List<AgentJobLogResponse>(logs.Count + 1);
                next.AddRange(logs);
                next.Add(log);
                return next
                    .OrderBy(static entry => entry.Timestamp)
                    .ToArray();
            },
            EstimateJobLogs);
    }

    public void RemoveJobLogs(Guid jobId)
        => Remove(KeyJobLogs(jobId));

    public async Task<ChannelCostResponse> GetChannelCostAsync(
        Guid channelId,
        Func<CancellationToken, Task<ChannelCostResponse>> factory,
        CancellationToken ct)
        => await GetOrCreateAsync(
            KeyChannelCost(channelId),
            async innerCt => await factory(innerCt),
            EstimateChannelCost,
            ct)
           ?? new ChannelCostResponse(channelId, 0, 0, 0, []);

    public Task<ThreadCostResponse?> GetThreadCostAsync(
        Guid channelId,
        Guid threadId,
        Func<CancellationToken, Task<ThreadCostResponse?>> factory,
        CancellationToken ct)
        => GetOrCreateAsync(
            KeyThreadCost(channelId, threadId),
            factory,
            EstimateThreadCost,
            ct);

    public Task<AgentCostResponse?> GetAgentCostAsync(
        Guid agentId,
        Func<CancellationToken, Task<AgentCostResponse?>> factory,
        CancellationToken ct)
        => GetOrCreateAsync(
            KeyAgentCost(agentId),
            factory,
            EstimateAgentCost,
            ct);

    public void RecordAssistantTokens(
        Guid channelId,
        Guid? threadId,
        Guid agentId,
        string agentName,
        int promptTokens,
        int completionTokens)
    {
        if (promptTokens <= 0 && completionTokens <= 0)
            return;

        Mutate<ChannelCostResponse>(
            KeyChannelCost(channelId),
            cost => AddAgentTokens(cost, agentId, agentName, promptTokens, completionTokens),
            EstimateChannelCost);

        if (threadId is { } tid)
        {
            Mutate<ThreadCostResponse>(
                KeyThreadCost(channelId, tid),
                cost => AddAgentTokens(cost, agentId, agentName, promptTokens, completionTokens),
                EstimateThreadCost);
        }

        Mutate<AgentCostResponse>(
            KeyAgentCost(agentId),
            cost => AddChannelTokens(cost, channelId, promptTokens, completionTokens),
            EstimateAgentCost);
    }

    public static string KeyHeaderUser(Guid userId)
        => $"{PrefixHeaderUser}{userId:D}";

    public static string KeyHeaderAgentSuffix(
        Guid agentId,
        Guid channelId,
        string providerKey,
        string? reasoningEffort)
        => PrefixHeaderAgentSuffix
           + $"{agentId:D}:{channelId:D}:"
           + $"{providerKey}:{reasoningEffort}";

    public static string KeyThreadHistoryLimits(Guid threadId)
        => $"{PrefixThreadHistoryLimits}{threadId:D}";

    public static string KeyEffectiveTools(Guid agentId, string awarenessFingerprint)
        => $"{PrefixEffectiveTools}{agentId:D}:{awarenessFingerprint}";

    public static string KeyDefaultResourceResolution(
        Guid channelId,
        Guid agentId,
        string? actionKey)
        => PrefixDefaultResourceResolution
           + $"{channelId:D}:{agentId:D}:"
           + (actionKey ?? "").Trim().ToLowerInvariant();

    public static string KeyJobLogs(Guid jobId)
        => $"{PrefixJobLogs}{jobId:D}";

    public static long EstimateString(string? value)
        => value is null ? 0 : Encoding.UTF8.GetByteCount(value);

    public static long EstimateStringCollection(IEnumerable<string> values)
        => values.Sum(EstimateString);

    private void EvictToBudget(long maxBytes)
    {
        while (_currentBytes > maxBytes && _fifo.Count > 0)
        {
            var key = _fifo.Dequeue();
            if (!_entries.Remove(key, out var entry))
                continue;

            _currentBytes -= entry.SizeBytes;
        }
    }

    private void RemoveEntry(string key)
    {
        if (!_entries.Remove(key, out var entry))
            return;

        _currentBytes -= entry.SizeBytes;
        if (_currentBytes < 0)
            _currentBytes = 0;
    }

    private void CompactFifoIfNeeded()
    {
        if (_fifo.Count <= _entries.Count * 2 + 1024)
            return;

        var liveKeys = _entries.Keys.ToHashSet(StringComparer.Ordinal);
        var retained = _fifo.Where(liveKeys.Contains).ToList();
        _fifo.Clear();
        foreach (var key in retained)
            _fifo.Enqueue(key);
    }

    private void RemoveWhere(Func<string, bool> predicate)
    {
        lock (_gate)
        {
            foreach (var key in _entries.Keys.Where(predicate).ToList())
                RemoveEntry(key);

            CompactFifoIfNeeded();
        }
    }

    private static bool KeyHasGuidSegment(string key, int segmentStart, Guid expected)
    {
        const int guidLength = 36;
        if (key.Length < segmentStart + guidLength)
            return false;

        return key.AsSpan(segmentStart, guidLength)
            .Equals(expected.ToString("D").AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    private long GetMaxBytes()
    {
        var explicitBytes = configuration.GetValue<long?>("Chat:CacheMaxBytes");
        if (explicitBytes is not null)
            return Math.Max(0, explicitBytes.Value);

        var megabytes = configuration.GetValue("Chat:CacheMaxMegabytes", DefaultMaxMegabytes);
        if (megabytes <= 0)
            return 0;

        if (megabytes > long.MaxValue / BytesPerMegabyte)
            return long.MaxValue;

        return megabytes * BytesPerMegabyte;
    }

    private static long EstimateObjectSize<T>(T value)
        where T : class
    {
        return value switch
        {
            string text => EstimateString(text),
            IReadOnlyCollection<string> strings => EstimateStringCollection(strings),
            _ => 1024
        };
    }

    private static string KeyChannelCost(Guid channelId) => $"chat:cost:channel:{channelId:D}";

    private static string KeyThreadCost(Guid channelId, Guid threadId)
        => $"chat:cost:thread:{channelId:D}:{threadId:D}";

    private static string KeyAgentCost(Guid agentId) => $"chat:cost:agent:{agentId:D}";

    private static long EstimateChannelCost(ChannelCostResponse cost)
        => 128 + cost.AgentBreakdown.Sum(static item => 96 + EstimateString(item.AgentName));

    private static long EstimateThreadCost(ThreadCostResponse cost)
        => 128 + cost.AgentBreakdown.Sum(static item => 96 + EstimateString(item.AgentName));

    private static long EstimateAgentCost(AgentCostResponse cost)
        => 128 + EstimateString(cost.AgentName) + cost.ChannelBreakdown.Count * 80L;

    private static long EstimateJobLogs(IReadOnlyList<AgentJobLogResponse> logs)
        => 128 + logs.Sum(static log =>
            48 + EstimateString(log.Message) + EstimateString(log.Level));

    private static ChannelCostResponse AddAgentTokens(
        ChannelCostResponse cost,
        Guid agentId,
        string agentName,
        int promptTokens,
        int completionTokens)
    {
        var breakdown = AddAgentBreakdown(
            cost.AgentBreakdown, agentId, agentName, promptTokens, completionTokens);

        return cost with
        {
            TotalPromptTokens = cost.TotalPromptTokens + promptTokens,
            TotalCompletionTokens = cost.TotalCompletionTokens + completionTokens,
            TotalTokens = cost.TotalTokens + promptTokens + completionTokens,
            AgentBreakdown = breakdown
        };
    }

    private static ThreadCostResponse AddAgentTokens(
        ThreadCostResponse cost,
        Guid agentId,
        string agentName,
        int promptTokens,
        int completionTokens)
    {
        var breakdown = AddAgentBreakdown(
            cost.AgentBreakdown, agentId, agentName, promptTokens, completionTokens);

        return cost with
        {
            TotalPromptTokens = cost.TotalPromptTokens + promptTokens,
            TotalCompletionTokens = cost.TotalCompletionTokens + completionTokens,
            TotalTokens = cost.TotalTokens + promptTokens + completionTokens,
            AgentBreakdown = breakdown
        };
    }

    private static IReadOnlyList<AgentTokenBreakdown> AddAgentBreakdown(
        IReadOnlyList<AgentTokenBreakdown> source,
        Guid agentId,
        string agentName,
        int promptTokens,
        int completionTokens)
    {
        var updated = false;
        var items = new List<AgentTokenBreakdown>(source.Count + 1);
        foreach (var item in source)
        {
            if (item.AgentId != agentId)
            {
                items.Add(item);
                continue;
            }

            var prompt = item.PromptTokens + promptTokens;
            var completion = item.CompletionTokens + completionTokens;
            items.Add(item with
            {
                AgentName = string.IsNullOrWhiteSpace(item.AgentName) ? agentName : item.AgentName,
                PromptTokens = prompt,
                CompletionTokens = completion,
                TotalTokens = prompt + completion
            });
            updated = true;
        }

        if (!updated)
        {
            items.Add(new AgentTokenBreakdown(
                agentId,
                agentName,
                promptTokens,
                completionTokens,
                promptTokens + completionTokens));
        }

        return items
            .OrderByDescending(static item => item.TotalTokens)
            .ToList();
    }

    private static AgentCostResponse AddChannelTokens(
        AgentCostResponse cost,
        Guid channelId,
        int promptTokens,
        int completionTokens)
    {
        var updated = false;
        var items = new List<AgentChannelTokenBreakdown>(cost.ChannelBreakdown.Count + 1);
        foreach (var item in cost.ChannelBreakdown)
        {
            if (item.ChannelId != channelId)
            {
                items.Add(item);
                continue;
            }

            var prompt = item.PromptTokens + promptTokens;
            var completion = item.CompletionTokens + completionTokens;
            items.Add(item with
            {
                PromptTokens = prompt,
                CompletionTokens = completion,
                TotalTokens = prompt + completion
            });
            updated = true;
        }

        if (!updated)
        {
            items.Add(new AgentChannelTokenBreakdown(
                channelId,
                promptTokens,
                completionTokens,
                promptTokens + completionTokens));
        }

        var breakdown = items
            .OrderByDescending(static item => item.TotalTokens)
            .ToList();

        return cost with
        {
            TotalPromptTokens = cost.TotalPromptTokens + promptTokens,
            TotalCompletionTokens = cost.TotalCompletionTokens + completionTokens,
            TotalTokens = cost.TotalTokens + promptTokens + completionTokens,
            ChannelBreakdown = breakdown
        };
    }

    private sealed record CacheEntry(object Value, long SizeBytes);
}
