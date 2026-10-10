namespace SharpClaw.Shared.DurableStorage;


public readonly record struct DurableStreamKey
{
    private DurableStreamKey(DurableStreamKind kind, string canonicalValue)
    {
        Kind = kind;
        CanonicalValue = canonicalValue;
    }

    public DurableStreamKind Kind { get; }
    public string CanonicalValue { get; }

    public static DurableStreamKey Job(Guid jobId) =>
        new(DurableStreamKind.JobLog, $"job/{jobId:D}");

    public static DurableStreamKey Process(string appName, Guid bootId) =>
        new(
            DurableStreamKind.ProcessLog,
            $"process/{NormalizeLogicalName(appName)}/{bootId:D}");

    public static DurableStreamKey Registration(string sourceId, Guid bootId) =>
        new(
            DurableStreamKind.RegistrationLog,
            $"registration/{NormalizeLogicalName(sourceId)}/{bootId:D}");

    public static bool TryParseOperational(
        string? canonicalValue,
        out DurableStreamKey key,
        out string? appName,
        out string? SourceId,
        out Guid bootId)
    {
        key = default;
        appName = null;
        SourceId = null;
        bootId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(canonicalValue))
            return false;

        var firstSeparator = canonicalValue.IndexOf('/', StringComparison.Ordinal);
        var lastSeparator = canonicalValue.LastIndexOf('/');
        if (firstSeparator <= 0
            || lastSeparator <= firstSeparator + 1
            || lastSeparator == canonicalValue.Length - 1
            || !Guid.TryParseExact(
                canonicalValue[(lastSeparator + 1)..],
                "D",
                out bootId))
        {
            bootId = Guid.Empty;
            return false;
        }

        var kind = canonicalValue[..firstSeparator];
        var logicalName = canonicalValue[(firstSeparator + 1)..lastSeparator];
        try
        {
            if (kind.Equals("process", StringComparison.Ordinal))
            {
                key = Process(logicalName, bootId);
                appName = logicalName;
            }
            else if (kind.Equals("registration", StringComparison.Ordinal))
            {
                key = Registration(logicalName, bootId);
                SourceId = logicalName;
            }
            else
            {
                return false;
            }

            return string.Equals(
                key.CanonicalValue,
                canonicalValue,
                StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            key = default;
            appName = null;
            SourceId = null;
            bootId = Guid.Empty;
            return false;
        }
    }

    private static string NormalizeLogicalName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
#pragma warning disable CA1308 // Canonical logical names are hashed into existing durable paths; retain lowercase identity and serialized values.
        var normalized = value.Trim().ToLowerInvariant();
#pragma warning restore CA1308
        if (normalized.Length > 256 || normalized.Any(char.IsControl))
            throw new ArgumentException("Logical stream name is invalid.", nameof(value));
        return normalized;
    }
}
