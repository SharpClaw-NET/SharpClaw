using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Entities.Core;

namespace SharpClaw.Contracts.Entities.Core.Clearance;

public class RoleDB : BaseEntity
{
    public required string Name { get; set; }

    public Guid? PermissionSetId { get; set; }
    public PermissionSetDB? PermissionSet { get; set; }

    public ICollection<UserDB> Users { get; set; } = [];
}
