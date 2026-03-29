using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Infrastructure.Models.Access;

/// <summary>
/// Grants a role access to a specific bot integration for sending
/// outbound messages through that platform.
/// </summary>
public class BotIntegrationAccessDB : BaseEntity
{
    /// <summary>
    /// Per-permission clearance override.
    /// <see cref="PermissionClearance.Unset"/> falls back to the group default.
    /// </summary>
    public PermissionClearance Clearance { get; set; } = PermissionClearance.Unset;

    public Guid PermissionSetId { get; set; }
    public PermissionSetDB PermissionSet { get; set; } = null!;

    public Guid BotIntegrationId { get; set; }
    public BotIntegrationDB BotIntegration { get; set; } = null!;
}
