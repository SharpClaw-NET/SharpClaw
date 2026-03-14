namespace SharpClaw.Contracts.Entities;

public abstract class BaseEntity
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Optional human-readable identifier.  Used primarily by task scripts
    /// to reference entities without hard-coding GUIDs.  Must be unique per
    /// entity type when set.
    /// </summary>
    public string? CustomId { get; set; }
}
