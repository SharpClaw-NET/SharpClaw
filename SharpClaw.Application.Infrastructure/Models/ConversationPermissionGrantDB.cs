using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Infrastructure.Models;

/// <summary>
/// A permission clearance level pre-approved by the user for a specific
/// action type within a conversation. When an agent action requires
/// <see cref="PermissionClearance.ApprovedBySameLevelUser"/> or
/// <see cref="PermissionClearance.ApprovedByWhitelistedUser"/> and a
/// matching grant exists, the action proceeds without waiting for
/// per-job approval.
/// </summary>
public class ConversationPermissionGrantDB : BaseEntity
{
    /// <summary>The action type this grant applies to.</summary>
    public AgentActionType ActionType { get; set; }

    /// <summary>
    /// The maximum clearance level the user pre-approves for this action
    /// type in this conversation.
    /// </summary>
    public PermissionClearance GrantedClearance { get; set; }

    public Guid ConversationId { get; set; }
    public ConversationDB Conversation { get; set; } = null!;
}
