using System.Text.Json;
using SharpClaw.Application.Infrastructure.Models;
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

    // ── First-class completion parameters ─────────────────────────

    /// <summary>Sampling temperature (0.0–2.0 for most providers).</summary>
    public float? Temperature { get; set; }

    /// <summary>Nucleus sampling probability mass (0.0–1.0).</summary>
    public float? TopP { get; set; }

    /// <summary>Top-K sampling (Anthropic, Google). Not supported by OpenAI Chat Completions.</summary>
    public int? TopK { get; set; }

    /// <summary>Penalises tokens that already appeared (OpenAI, OpenRouter).</summary>
    public float? FrequencyPenalty { get; set; }

    /// <summary>Penalises tokens based on presence in the text so far (OpenAI, OpenRouter).</summary>
    public float? PresencePenalty { get; set; }

    /// <summary>Up to 4 sequences where the model will stop generating.</summary>
    public string[]? Stop { get; set; }

    /// <summary>Deterministic sampling seed (OpenAI, Mistral).</summary>
    public int? Seed { get; set; }

    /// <summary>
    /// Structured output format.  For OpenAI: <c>{ "type": "json_object" }</c>.
    /// Stored as a <see cref="JsonElement"/> to preserve arbitrary structure.
    /// </summary>
    public JsonElement? ResponseFormat { get; set; }

    /// <summary>
    /// Reasoning effort hint for models that support it (e.g. OpenAI o-series).
    /// Values: <c>"low"</c>, <c>"medium"</c>, <c>"high"</c>.
    /// </summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>
    /// Optional provider-specific parameters merged into the API request
    /// payload.  Keys and values are provider-dependent — for example,
    /// Google Gemini accepts <c>response_mime_type</c> while OpenAI uses
    /// <c>response_format</c>.  Stored as a JSON string in the database.
    /// </summary>
    public Dictionary<string, JsonElement>? ProviderParameters { get; set; }

    /// <summary>
    /// Optional custom chat header template. When set, replaces the default
    /// system-generated header for this agent. Tag placeholders like
    /// <c>{{time}}</c> or <c>{{SafeShellAccesses}}</c> are expanded at runtime.
    /// </summary>
    public string? CustomChatHeader { get; set; }

    /// <summary>
    /// When <see langword="true"/>, no tool schemas or tool instruction
    /// suffix are sent in chat requests for this agent — the model sees
    /// only the system prompt and conversation history.  Overridden by
    /// the channel's <see cref="ChannelDB.DisableToolSchemas"/> when set.
    /// </summary>
    public bool DisableToolSchemas { get; set; }

    /// <summary>
    /// Optional tool-awareness set controlling which tool-call schemas are
    /// sent in API requests.  Only tools whose key is <see langword="true"/>
    /// (or absent) are included.  Reduces prompt-token overhead for agents
    /// that only need a subset of capabilities.
    /// <para>
    /// Override chain: channel's set → this set → <see langword="null"/>
    /// (all tools enabled).
    /// </para>
    /// Ignored when <see cref="DisableToolSchemas"/> is <see langword="true"/>.
    /// </summary>
    public Guid? ToolAwarenessSetId { get; set; }
    public ToolAwarenessSetDB? ToolAwarenessSet { get; set; }

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
