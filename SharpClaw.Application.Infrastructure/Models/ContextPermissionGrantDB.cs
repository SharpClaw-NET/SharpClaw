using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Infrastructure.Models;

/// <summary>
/// A permission clearance level pre-approved at the context level.
/// Applies automatically to every conversation and task within the context
/// unless overridden by a per-conversation or per-task grant.
/// </summary>
public class ContextPermissionGrantDB : BaseEntity
{
    /// <summary>The action type this grant applies to.</summary>
    public AgentActionType ActionType { get; set; }

    /// <summary>
    /// The maximum clearance level the user pre-approves for this action
    /// type across the entire context.
    /// </summary>
    public PermissionClearance GrantedClearance { get; set; }

    public Guid AgentContextId { get; set; }
    public AgentContextDB AgentContext { get; set; } = null!;
}
