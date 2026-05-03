using SharpClaw.Contracts.Entities;

namespace SharpClaw.Contracts.Entities.Core;

/// <summary>
/// Tracks the enabled/disabled state of a bundled module.
/// </summary>
public class ModuleStateDB : BaseEntity
{
    /// <summary>Unique module identifier (e.g. "sharpclaw_computer_use").</summary>
    public required string ModuleId { get; set; }

    /// <summary>Whether the module is enabled (tools registered, init executed).</summary>
    public bool Enabled { get; set; }

    /// <summary>Last known version string from the module manifest.</summary>
    public string? Version { get; set; }
}
