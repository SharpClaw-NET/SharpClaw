using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Infrastructure.Models.Access;

/// <summary>
/// A single global boolean flag grant. Each row grants a capability
/// identified by <see cref="FlagKey"/> to the owning
/// <see cref="PermissionSetDB"/>.
/// <para>
/// Replaces the 18 individual boolean properties + 18 clearance properties
/// that were previously hardcoded on <see cref="PermissionSetDB"/>.
/// See Module-System-Design §12.4.2.
/// </para>
/// </summary>
public class GlobalFlagDB : BaseEntity
{
    /// <summary>
    /// Canonical flag identifier (e.g. <c>"CanClickDesktop"</c>).
    /// Must match a flag key registered by a module via
    /// <see cref="Contracts.Modules.ModuleGlobalFlagDescriptor.FlagKey"/>.
    /// </summary>
    public required string FlagKey { get; set; }

    /// <summary>
    /// Per-flag clearance level.
    /// <see cref="PermissionClearance.Unset"/> means the grant is inert —
    /// the action is denied until an admin explicitly sets a clearance level.
    /// </summary>
    public PermissionClearance Clearance { get; set; } = PermissionClearance.Unset;

    public Guid PermissionSetId { get; set; }
    public PermissionSetDB PermissionSet { get; set; } = null!;
}
