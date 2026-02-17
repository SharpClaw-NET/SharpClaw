using System.Collections.Concurrent;

namespace SharpClaw.PublicAPI.Security;

/// <summary>
/// Tracks per-IP violations and bans IPs that exceed the threshold.
/// All state is in-memory and resets on restart.
/// </summary>
public sealed class IpBanService
{
    private readonly ConcurrentDictionary<string, IpRecord> _records = new();

    /// <summary>Number of violations before an IP is banned.</summary>
    public int ViolationThreshold { get; set; } = 10;

    /// <summary>How long a ban lasts once triggered.</summary>
    public TimeSpan BanDuration { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Sliding window for counting violations.</summary>
    public TimeSpan ViolationWindow { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Returns <c>true</c> if the IP is currently banned.</summary>
    public bool IsBanned(string ip)
    {
        if (!_records.TryGetValue(ip, out var record))
            return false;

        if (record.BannedUntil is not null && record.BannedUntil > DateTimeOffset.UtcNow)
            return true;

        // Ban expired — clear it
        if (record.BannedUntil is not null)
        {
            record.BannedUntil = null;
            record.Violations.Clear();
        }

        return false;
    }

    /// <summary>
    /// Records a violation for the given IP. If the violation count within
    /// the window exceeds the threshold, the IP is banned.
    /// </summary>
    public void RecordViolation(string ip)
    {
        var record = _records.GetOrAdd(ip, _ => new IpRecord());
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - ViolationWindow;

        // Prune old violations
        while (record.Violations.TryPeek(out var oldest) && oldest < cutoff)
            record.Violations.TryDequeue(out _);

        record.Violations.Enqueue(now);

        if (record.Violations.Count >= ViolationThreshold)
            record.BannedUntil = now + BanDuration;
    }

    /// <summary>Manually ban an IP for the configured duration.</summary>
    public void Ban(string ip)
    {
        var record = _records.GetOrAdd(ip, _ => new IpRecord());
        record.BannedUntil = DateTimeOffset.UtcNow + BanDuration;
    }

    /// <summary>Manually unban an IP.</summary>
    public void Unban(string ip)
    {
        if (_records.TryGetValue(ip, out var record))
        {
            record.BannedUntil = null;
            record.Violations.Clear();
        }
    }

    private sealed class IpRecord
    {
        public ConcurrentQueue<DateTimeOffset> Violations { get; } = new();
        public DateTimeOffset? BannedUntil { get; set; }
    }
}
