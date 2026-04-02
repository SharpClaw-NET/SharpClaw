using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Application.Infrastructure.Models.Resources;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Infrastructure.Models.Access;

/// <summary>
/// Grants an agent (via its role's permission set) permission to
/// launch a <see cref="NativeApplicationDB"/>.
/// </summary>
public class NativeApplicationAccessDB : BaseEntity
{
    public PermissionClearance Clearance { get; set; }

    public Guid PermissionSetId { get; set; }
    public PermissionSetDB PermissionSet { get; set; } = null!;

    public Guid NativeApplicationId { get; set; }
    public NativeApplicationDB NativeApplication { get; set; } = null!;
}
