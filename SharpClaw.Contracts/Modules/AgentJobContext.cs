using SharpClaw.Contracts.Enums;

namespace SharpClaw.Contracts.Modules;

/// <summary>
/// Thin read-only snapshot of an <c>AgentJobDB</c> passed to module
/// <see cref="ISharpClawModule.ExecuteToolAsync"/>.
/// Avoids a Contracts → Infrastructure dependency while giving modules
/// the job metadata they need (identity, channel, resource, action key).
/// </summary>
public sealed record AgentJobContext(
    Guid JobId,
    Guid AgentId,
    Guid ChannelId,
    Guid? ResourceId,
    AgentActionType ActionType,
    string? ActionKey,
    string? Language
);
