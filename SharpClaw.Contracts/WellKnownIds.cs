namespace SharpClaw.Contracts;

/// <summary>
/// Well-known GUIDs used as sentinel values throughout the system.
/// These are immutable at runtime — the DbContext rejects any attempt
/// to modify or delete entities that carry them.
/// </summary>
public static class WellKnownIds
{
    /// <summary>
    /// Wildcard resource ID. When used as the resource FK in a permission
    /// grant entry, it means "access to all resources of this type."
    /// </summary>
    public static readonly Guid AllResources = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
}
