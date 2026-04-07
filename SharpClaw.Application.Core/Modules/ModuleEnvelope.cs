using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpClaw.Application.Core.Modules;

/// <summary>
/// Deserialization target for the <c>ScriptJson</c> envelope of a module tool call.
/// Used by <c>DispatchPermissionCheckAsync</c>, <c>DispatchModuleExecutionAsync</c>,
/// and <c>IsPerResourceAction</c> to identify the target module and tool.
/// </summary>
internal sealed record ModuleEnvelope(
    [property: JsonPropertyName("module")] string Module,
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("params")] JsonElement Params
);
