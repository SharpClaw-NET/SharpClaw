namespace Mk8.Shell.Isolation;

/// <summary>
/// Defines allowed network destinations for sandbox processes. By default,
/// ALL network traffic is blocked (iron curtain). Only destinations
/// explicitly listed here are permitted.
/// <para>
/// Configured in base.env (<c>NetworkWhitelist</c>) and/or sandbox env
/// (<c>MK8_NETWORK_WHITELIST</c>). Sandbox rules are additive to global.
/// </para>
/// </summary>
public sealed class Mk8NetworkWhitelist
{
    /// <summary>
    /// Individual whitelisted endpoints. Each rule permits traffic to
    /// a specific host/port/protocol combination.
    /// </summary>
    public IReadOnlyList<Mk8NetworkRule> Rules { get; }

    /// <summary>
    /// When <c>true</c>, all network traffic is allowed (no filtering).
    /// This effectively disables the iron curtain. Intended only for
    /// debugging — never for production sandboxes.
    /// </summary>
    public bool AllowAll { get; }

    /// <summary>
    /// Creates a whitelist that blocks all network traffic.
    /// </summary>
    public Mk8NetworkWhitelist()
    {
        Rules = [];
        AllowAll = false;
    }

    /// <summary>
    /// Creates a whitelist with explicit rules.
    /// </summary>
    public Mk8NetworkWhitelist(IEnumerable<Mk8NetworkRule> rules, bool allowAll = false)
    {
        Rules = [.. rules];
        AllowAll = allowAll;
    }

    /// <summary>
    /// Checks whether a given destination is permitted.
    /// </summary>
    public bool IsAllowed(string host, int port, Mk8NetworkProtocol protocol)
    {
        if (AllowAll)
            return true;

        foreach (var rule in Rules)
        {
            if (rule.Matches(host, port, protocol))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Parses rules from a comma-separated string.
    /// Format: <c>host:port/protocol</c> where protocol is <c>tcp</c>
    /// or <c>udp</c> (default tcp). Port <c>*</c> means any port.
    /// <para>
    /// Examples: <c>nuget.org:443/tcp, api.github.com:443, *.npm.org:443</c>
    /// </para>
    /// </summary>
    public static Mk8NetworkWhitelist Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new Mk8NetworkWhitelist();

        if (raw.Trim().Equals("*", StringComparison.Ordinal))
            return new Mk8NetworkWhitelist([], allowAll: true);

        var entries = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rules = new List<Mk8NetworkRule>();

        foreach (var entry in entries)
        {
            var rule = Mk8NetworkRule.Parse(entry);
            if (rule is not null)
                rules.Add(rule);
        }

        return new Mk8NetworkWhitelist(rules);
    }

    /// <summary>
    /// Merges two whitelists (global + sandbox). Rules are additive —
    /// the sandbox can add destinations but cannot remove global ones.
    /// AllowAll does NOT propagate from the sandbox source — only the
    /// base/global config can enable debug-mode AllowAll.
    /// </summary>
    public Mk8NetworkWhitelist MergeWith(Mk8NetworkWhitelist? other)
    {
        if (other is null)
            return this;

        // AllowAll does NOT propagate from the 'other' (sandbox)
        // source. Sandbox rules are additive (more destinations)
        // but cannot disable the iron curtain entirely. Only the
        // base/global config can set AllowAll (debug mode).
        return new Mk8NetworkWhitelist(
            [.. Rules, .. other.Rules],
            AllowAll);
    }
}

/// <summary>
/// A single network whitelist rule permitting traffic to a specific
/// host/port/protocol combination.
/// </summary>
public sealed class Mk8NetworkRule
{
    /// <summary>
    /// Target hostname or IP. Supports wildcard prefix: <c>*.example.com</c>
    /// matches <c>sub.example.com</c> and <c>deep.sub.example.com</c>.
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// Target port. <c>0</c> means any port.
    /// </summary>
    public required int Port { get; init; }

    /// <summary>
    /// Protocol filter. <see cref="Mk8NetworkProtocol.Any"/> matches both.
    /// </summary>
    public required Mk8NetworkProtocol Protocol { get; init; }

    /// <summary>
    /// HTTP methods permitted for this rule (GET, POST, PUT, PATCH,
    /// DELETE, HEAD, OPTIONS). For documentation and intent capture —
    /// enforcement is at the IP:port:protocol layer, not HTTP layer.
    /// <c>null</c> or empty = all methods permitted.
    /// </summary>
    public IReadOnlyList<string>? AllowedHttpMethods { get; init; }

    /// <summary>
    /// Checks if this rule permits the given destination.
    /// </summary>
    public bool Matches(string host, int port, Mk8NetworkProtocol protocol)
    {
        // Protocol check
        if (Protocol != Mk8NetworkProtocol.Any && Protocol != protocol)
            return false;

        // Port check
        if (Port != 0 && Port != port)
            return false;

        // Host check (case-insensitive)
        if (Host.StartsWith("*.", StringComparison.Ordinal))
        {
            // Wildcard: *.example.com matches sub.example.com
            var suffix = Host[1..]; // .example.com
            return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                || host.Equals(Host[2..], StringComparison.OrdinalIgnoreCase);
        }

        return Host.Equals(host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses a single rule from string.
    /// Format: <c>host:port/protocol</c>.
    /// </summary>
    public static Mk8NetworkRule? Parse(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
            return null;

        var protocol = Mk8NetworkProtocol.Tcp;
        var s = entry.Trim();

        // Extract /protocol suffix
        var slashIdx = s.LastIndexOf('/');
        if (slashIdx >= 0)
        {
            var protoStr = s[(slashIdx + 1)..];
            protocol = protoStr.ToLowerInvariant() switch
            {
                "tcp" => Mk8NetworkProtocol.Tcp,
                "udp" => Mk8NetworkProtocol.Udp,
                "*"   => Mk8NetworkProtocol.Any,
                _     => Mk8NetworkProtocol.Tcp,
            };
            s = s[..slashIdx];
        }

        // Extract :port
        var port = 0;
        var colonIdx = s.LastIndexOf(':');
        if (colonIdx >= 0)
        {
            var portStr = s[(colonIdx + 1)..];
            if (portStr == "*")
                port = 0;
            else if (int.TryParse(portStr, out var p) && p is > 0 and <= 65535)
                port = p;
            else
                return null; // invalid port

            s = s[..colonIdx];
        }

        if (string.IsNullOrWhiteSpace(s))
            return null;

        return new Mk8NetworkRule
        {
            Host = s,
            Port = port,
            Protocol = protocol,
        };
    }

    public override string ToString()
    {
        var portStr = Port == 0 ? "*" : Port.ToString();
        var protoStr = Protocol switch
        {
            Mk8NetworkProtocol.Any => "*",
            Mk8NetworkProtocol.Udp => "udp",
            _ => "tcp",
        };
        return $"{Host}:{portStr}/{protoStr}";
    }
}

/// <summary>
/// Network protocol for whitelist rules.
/// </summary>
public enum Mk8NetworkProtocol
{
    Tcp,
    Udp,
    Any,
}
