using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Application.Infrastructure.Models.Resources;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Infrastructure.Models.Access;

/// <summary>
/// Grants an agent (via its role's permission set) access to a
/// <see cref="DocumentSessionDB"/>.  All document tool calls
/// (read range, write range, list sheets, etc.) check this grant.
/// </summary>
public class DocumentSessionAccessDB : BaseEntity
{
    public PermissionClearance Clearance { get; set; }

    public Guid PermissionSetId { get; set; }
    public PermissionSetDB PermissionSet { get; set; } = null!;

    public Guid DocumentSessionId { get; set; }
    public DocumentSessionDB DocumentSession { get; set; } = null!;
}
