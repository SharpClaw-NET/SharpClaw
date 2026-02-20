using SharpClaw.Contracts.Entities;
using SharpClaw.Infrastructure.Models;

namespace SharpClaw.Application.Infrastructure.Models.Clearance;

public class RoleDB : BaseEntity
{
    public required string Name { get; set; }

    public Guid? PermissionSetId { get; set; }
    public PermissionSetDB? PermissionSet { get; set; }

    public ICollection<UserDB> Users { get; set; } = [];
}
