namespace SharpClaw.Application.API.Cli;

/// <summary>
/// Maps short integer IDs (CLI-only) to entity GUIDs for convenient interactive use.
/// Short IDs are assigned as entities are displayed and reset on each app run.
/// </summary>
internal static class CliIdMap
{
    private static readonly Dictionary<Guid, int> GuidToShort = [];
    private static readonly Dictionary<int, Guid> ShortToGuid = [];
    private static int _nextId = 1;

    /// <summary>
    /// Returns the short ID for a GUID, assigning a new one if not yet mapped.
    /// </summary>
    public static int GetOrAssign(Guid guid)
    {
        if (GuidToShort.TryGetValue(guid, out var existing))
            return existing;

        var id = _nextId++;
        GuidToShort[guid] = id;
        ShortToGuid[id] = guid;
        return id;
    }

    /// <summary>
    /// Parses a CLI argument as either a short integer ID (with or without
    /// the <c>#</c> prefix) or a full GUID string.
    /// </summary>
    public static Guid Resolve(string arg)
    {
        var normalized = arg.StartsWith('#') ? arg[1..] : arg;

        if (int.TryParse(normalized, out var shortId))
        {
            if (ShortToGuid.TryGetValue(shortId, out var guid))
                return guid;

            throw new ArgumentException($"Unknown short ID #{shortId}. Use 'list' to see available IDs.");
        }

        return Guid.Parse(arg);
    }
}
