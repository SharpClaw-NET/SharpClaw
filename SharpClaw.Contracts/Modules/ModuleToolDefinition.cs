using System.Text.Json;

namespace SharpClaw.Contracts.Modules;

/// <summary>
/// A single tool exposed by a module.
/// </summary>
public sealed record ModuleToolDefinition(
    /// <summary>Tool name without prefix (e.g. "enumerate_windows").</summary>
    string Name,

    /// <summary>Description shown to the LLM.</summary>
    string Description,

    /// <summary>JSON Schema for parameters (same format as ChatToolDefinition).</summary>
    JsonElement ParametersSchema,

    /// <summary>Permission requirements for this tool.</summary>
    ModuleToolPermission Permission
);
