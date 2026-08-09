using System.Text.Json;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Enums;

using HostAccess = SharpClaw.Contracts.Entities.Core.Access;
using HostClearance = SharpClaw.Contracts.Entities.Core.Clearance;
using HostContext = SharpClaw.Contracts.Entities.Core.Context;
using HostJobs = SharpClaw.Contracts.Entities.Core.Jobs;
using HostMessages = SharpClaw.Contracts.Entities.Core.Messages;

namespace SharpClaw.Runtime.INF.Persistence
{
    internal static class WellKnownIds
    {
        public static readonly Guid AllResources = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
    }
}

namespace SharpClaw.Contracts.Enums
{
    public enum AgentJobStatus
    {
        Queued = 0,
        Executing = 1,
        AwaitingApproval = 2,
        Completed = 3,
        Failed = 4,
        Denied = 5,
        Cancelled = 6,
        Paused = 7,
    }

    public enum PermissionClearance
    {
        Unset = 0,
        ApprovedBySameLevelUser = 1,
        ApprovedByWhitelistedUser = 2,
        ApprovedByPermittedAgent = 3,
        ApprovedByWhitelistedAgent = 4,
        Independent = 5,
        Restricted = 6,
    }

    public enum ExecutionOwnerKind
    {
        AgentJob = 0,
    }
}

namespace SharpClaw.Contracts.Entities.Core
{
    public sealed class ToolAwarenessSetDB : BaseEntity
    {
        public required string Name { get; set; }
        public Dictionary<string, bool> Tools { get; set; } = new();
    }

    public sealed class AgentDB : BaseEntity
    {
        public required string Name { get; set; }
        public string? SystemPrompt { get; set; }
        public int? MaxCompletionTokens { get; set; }
        public float? Temperature { get; set; }
        public float? TopP { get; set; }
        public int? TopK { get; set; }
        public float? FrequencyPenalty { get; set; }
        public float? PresencePenalty { get; set; }
        public string[]? Stop { get; set; }
        public int? Seed { get; set; }
        public JsonElement? ResponseFormat { get; set; }
        public string? ReasoningEffort { get; set; }
        public Dictionary<string, JsonElement>? ProviderParameters { get; set; }
        public string? CustomChatHeader { get; set; }
        public bool DisableToolSchemas { get; set; }
        public Guid? ToolAwarenessSetId { get; set; }
        public ToolAwarenessSetDB? ToolAwarenessSet { get; set; }
        public Guid ModelId { get; set; }
        public ModelDB Model { get; set; } = null!;
        public Guid? RoleId { get; set; }
        public HostClearance.RoleDB? Role { get; set; }
        public ICollection<HostContext.ChannelContextDB> Contexts { get; set; } = [];
        public ICollection<HostContext.ChannelDB> Channels { get; set; } = [];
        public ICollection<HostContext.ChannelDB> AllowedChannels { get; set; } = [];
        public ICollection<HostContext.ChannelContextDB> AllowedContexts { get; set; } = [];
    }
}

namespace SharpClaw.Contracts.Entities.Core.Access
{
    public sealed class GlobalFlagDB : BaseEntity
    {
        public required string FlagKey { get; set; }
        public PermissionClearance Clearance { get; set; } = PermissionClearance.Unset;
        public Guid PermissionSetId { get; set; }
        public HostClearance.PermissionSetDB PermissionSet { get; set; } = null!;
    }

    public sealed class ResourceAccessDB : BaseEntity
    {
        public required string ResourceType { get; set; }
        public Guid ResourceId { get; set; }
        public PermissionClearance Clearance { get; set; } = PermissionClearance.Unset;
        public Guid PermissionSetId { get; set; }
        public HostClearance.PermissionSetDB PermissionSet { get; set; } = null!;
        public string SubType { get; set; } = string.Empty;
        public string? AccessLevel { get; set; }
        public bool IsDefault { get; set; }
    }
}

namespace SharpClaw.Contracts.Entities.Core.Clearance
{
    public sealed class ClearanceAgentWhitelistEntryDB : BaseEntity
    {
        public Guid PermissionSetId { get; set; }
        public PermissionSetDB PermissionSet { get; set; } = null!;
        public Guid AgentId { get; set; }
        public AgentDB Agent { get; set; } = null!;
    }

    public sealed class ClearanceUserWhitelistEntryDB : BaseEntity
    {
        public Guid PermissionSetId { get; set; }
        public PermissionSetDB PermissionSet { get; set; } = null!;
        public Guid UserId { get; set; }
        public UserDB User { get; set; } = null!;
    }

    public sealed class PermissionSetDB : BaseEntity
    {
        public ICollection<HostAccess.GlobalFlagDB> GlobalFlags { get; set; } = [];
        public ICollection<HostAccess.ResourceAccessDB> ResourceAccesses { get; set; } = [];
        public ICollection<ClearanceUserWhitelistEntryDB> ClearanceUserWhitelist { get; set; } = [];
        public ICollection<ClearanceAgentWhitelistEntryDB> ClearanceAgentWhitelist { get; set; } = [];
    }

    public sealed class RoleDB : BaseEntity
    {
        public required string Name { get; set; }
        public Guid? PermissionSetId { get; set; }
        public PermissionSetDB? PermissionSet { get; set; }
        public ICollection<UserDB> Users { get; set; } = [];
    }
}

namespace SharpClaw.Contracts.Entities.Core.Context
{
    public sealed class ChannelContextDB : BaseEntity
    {
        public required string Name { get; set; }
        public Guid AgentId { get; set; }
        public AgentDB Agent { get; set; } = null!;
        public Guid? PermissionSetId { get; set; }
        public HostClearance.PermissionSetDB? PermissionSet { get; set; }
        public Guid? DefaultResourceSetId { get; set; }
        public DefaultResourceSetDB? DefaultResourceSet { get; set; }
        public bool DisableChatHeader { get; set; }
        public ICollection<AgentDB> AllowedAgents { get; set; } = [];
        public ICollection<ChannelDB> Channels { get; set; } = [];
    }

    public sealed class ChannelDB : BaseEntity
    {
        public required string Title { get; set; }
        public Guid? AgentId { get; set; }
        public AgentDB? Agent { get; set; }
        public Guid? AgentContextId { get; set; }
        public ChannelContextDB? AgentContext { get; set; }
        public Guid? PermissionSetId { get; set; }
        public HostClearance.PermissionSetDB? PermissionSet { get; set; }
        public Guid? DefaultResourceSetId { get; set; }
        public DefaultResourceSetDB? DefaultResourceSet { get; set; }
        public bool DisableChatHeader { get; set; }
        public string? CustomChatHeader { get; set; }
        public bool DisableToolSchemas { get; set; }
        public Guid? ToolAwarenessSetId { get; set; }
        public ToolAwarenessSetDB? ToolAwarenessSet { get; set; }
        public ICollection<AgentDB> AllowedAgents { get; set; } = [];
        public ICollection<HostMessages.ChatMessageDB> ChatMessages { get; set; } = [];
        public ICollection<ChatThreadDB> Threads { get; set; } = [];
    }

    public sealed class ChatThreadDB : BaseEntity
    {
        public required string Name { get; set; }
        public int? MaxMessages { get; set; }
        public int? MaxCharacters { get; set; }
        public Guid ChannelId { get; set; }
        public ChannelDB Channel { get; set; } = null!;
        public ICollection<HostMessages.ChatMessageDB> ChatMessages { get; set; } = [];
    }

    public sealed class DefaultResourceEntryDB : BaseEntity
    {
        public Guid DefaultResourceSetId { get; set; }
        public string ResourceKey { get; set; } = string.Empty;
        public Guid ResourceId { get; set; }
        public DefaultResourceSetDB? DefaultResourceSet { get; set; }
    }

    public sealed class DefaultResourceSetDB : BaseEntity
    {
        public List<DefaultResourceEntryDB> Entries { get; set; } = [];
    }
}

namespace SharpClaw.Contracts.Entities.Core.Jobs
{
    public sealed class AgentJobDB : BaseEntity
    {
        public Guid AgentId { get; set; }
        public AgentDB Agent { get; set; } = null!;
        public Guid? CallerUserId { get; set; }
        public Guid? CallerAgentId { get; set; }
        public string? ActionKey { get; set; }
        public Guid? ResourceId { get; set; }
        public string? ScriptJson { get; set; }
        public string? WorkingDirectory { get; set; }
        public AgentJobStatus Status { get; set; } = AgentJobStatus.Queued;
        public PermissionClearance EffectiveClearance { get; set; } = PermissionClearance.Unset;
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public Guid? ApprovedByUserId { get; set; }
        public Guid? ApprovedByAgentId { get; set; }
        public Guid ChannelId { get; set; }
        public HostContext.ChannelDB Channel { get; set; } = null!;
    }
}

namespace SharpClaw.Contracts.Entities.Core.Messages
{
    public sealed class ChatMessageDB : BaseEntity
    {
        public required string Role { get; set; }
        public MessageOrigin? Origin { get; set; }
        public required string Content { get; set; }
        public string? ProviderMetadataJson { get; set; }
        public Guid ChannelId { get; set; }
        public HostContext.ChannelDB Channel { get; set; } = null!;
        public Guid? ThreadId { get; set; }
        public HostContext.ChatThreadDB? Thread { get; set; }
        public Guid? SenderUserId { get; set; }
        public string? SenderUsername { get; set; }
        public Guid? SenderAgentId { get; set; }
        public string? SenderAgentName { get; set; }
        public Guid? PermissionRoleId { get; set; }
        public string? PermissionRoleName { get; set; }
        public string? ClientType { get; set; }
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
    }
}
