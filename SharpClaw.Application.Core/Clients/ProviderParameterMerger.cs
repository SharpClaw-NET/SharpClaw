using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// Shared utility for merging user-supplied provider parameters into a
/// typed API payload.  Provider parameters are additive — they never
/// overwrite keys that the client already sets (model, messages, tools, etc.).
/// </summary>
internal static class ProviderParameterMerger
{
    /// <summary>
    /// Serializes <paramref name="payload"/> and merges any user-supplied
    /// <paramref name="providerParameters"/> into the top-level JSON object.
    /// </summary>
    internal static HttpContent Merge<T>(
        T payload,
        Dictionary<string, JsonElement>? providerParameters,
        JsonSerializerOptions? options = null)
    {
        if (providerParameters is not { Count: > 0 })
            return JsonContent.Create(payload, options: options);

        var node = JsonSerializer.SerializeToNode(payload, options);
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in providerParameters)
            {
                if (obj.ContainsKey(key)) continue;
                obj[key] = JsonSerializer.SerializeToNode(value);
            }
        }

        return new StringContent(
            node!.ToJsonString(options),
            System.Text.Encoding.UTF8,
            "application/json");
    }
}
