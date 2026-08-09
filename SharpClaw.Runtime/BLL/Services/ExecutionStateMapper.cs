using SharpClaw.Runtime.INF.Persistence.Entities.Core.Jobs;
using SharpClaw.Core.Jobs;

namespace SharpClaw.Runtime.BLL.Services;

/// <summary>
/// Maps Runtime-owned persistence entities to Core-owned execution state.
/// Persistence navigation properties and provider metadata do not cross this
/// boundary.
/// </summary>
internal static class ExecutionStateMapper
{
    public static AgentJobState ToCoreState(AgentJobDB entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return new AgentJobState
        {
            Id = entity.Id,
            AgentId = entity.AgentId,
            ChannelId = entity.ChannelId,
            CallerUserId = entity.CallerUserId,
            CallerAgentId = entity.CallerAgentId,
            ActionKey = entity.ActionKey,
            ResourceId = entity.ResourceId,
            ScriptJson = entity.ScriptJson,
            WorkingDirectory = entity.WorkingDirectory,
            Status = entity.Status,
            EffectiveClearance = entity.EffectiveClearance,
            PromptTokens = entity.PromptTokens,
            CompletionTokens = entity.CompletionTokens,
            CreatedAt = entity.CreatedAt,
            StartedAt = entity.StartedAt,
            CompletedAt = entity.CompletedAt,
            ApprovedByUserId = entity.ApprovedByUserId,
            ApprovedByAgentId = entity.ApprovedByAgentId,
        };
    }

    public static AgentJobDB ToEntity(AgentJobState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var entity = new AgentJobDB();
        Apply(state, entity);
        return entity;
    }

    public static void Apply(AgentJobState state, AgentJobDB entity)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(entity);
        entity.Id = state.Id;
        entity.AgentId = state.AgentId;
        entity.ChannelId = state.ChannelId;
        entity.CallerUserId = state.CallerUserId;
        entity.CallerAgentId = state.CallerAgentId;
        entity.ActionKey = state.ActionKey;
        entity.ResourceId = state.ResourceId;
        entity.ScriptJson = state.ScriptJson;
        entity.WorkingDirectory = state.WorkingDirectory;
        entity.Status = state.Status;
        entity.EffectiveClearance = state.EffectiveClearance;
        entity.PromptTokens = state.PromptTokens;
        entity.CompletionTokens = state.CompletionTokens;
        entity.CreatedAt = state.CreatedAt;
        entity.StartedAt = state.StartedAt;
        entity.CompletedAt = state.CompletedAt;
        entity.ApprovedByUserId = state.ApprovedByUserId;
        entity.ApprovedByAgentId = state.ApprovedByAgentId;
    }

}
