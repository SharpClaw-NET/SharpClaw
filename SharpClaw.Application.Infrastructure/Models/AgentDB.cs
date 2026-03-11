using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Application.Infrastructure.Models.Context;
using SharpClaw.Contracts.Entities;

namespace SharpClaw.Infrastructure.Models;

public class AgentDB : BaseEntity
{
    public required string Name { get; set; }
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// Optional cap on the number of tokens the model may generate in a
    /// single response.  Sent as <c>max_tokens</c>, <c>max_completion_tokens</c>,
    /// or <c>max_output_tokens</c> depending on the provider and API version.
    /// <see langword="null"/> means no limit (provider default).
    /// </summary>
    public int? MaxCompletionTokens { get; set; }

    public Guid ModelId { get; set; }
    public ModelDB Model { get; set; } = null!;

    public Guid? RoleId { get; set; }
    public RoleDB? Role { get; set; }

    public ICollection<ChannelContextDB> Contexts { get; set; } = [];
    public ICollection<ChannelDB> Channels { get; set; } = [];

    /// <summary>
    /// Channels where this agent is an additional (non-default) allowed
    /// agent.  Inverse of <see cref="ChannelDB.AllowedAgents"/>.
    /// </summary>
    public ICollection<ChannelDB> AllowedChannels { get; set; } = [];

    /// <summary>
    /// Contexts where this agent is an additional allowed agent.
    /// Inverse of <see cref="ChannelContextDB.AllowedAgents"/>.
    /// </summary>
    public ICollection<ChannelContextDB> AllowedContexts { get; set; } = [];
}
