using System.Text.Json;
using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;

namespace SharpClaw.Modules.Providers.OpenAICompatible.Clients;

/// <summary>
/// DeepSeek via its OpenAI-compatible Chat Completions API.
/// </summary>
public sealed class DeepSeekApiClient(
    string apiKey = "",
    HttpClient? httpClient = null)
    : OpenAiCompatibleApiClient(apiKey, httpClient)
{
    protected override string ApiEndpoint => "https://api.deepseek.com";
    public override string ProviderKey => "deepseek";
    protected override bool SupportsReasoningContentReplay => true;

    protected override Dictionary<string, JsonElement>? PrepareProviderParameters(
        Dictionary<string, JsonElement>? providerParameters,
        CompletionParameters? completionParameters,
        bool hasTools)
    {
        var hasExplicitThinking = providerParameters?.ContainsKey("thinking") == true;
        var thinkingEnabled = IsThinkingEnabled(providerParameters);
        var wantsReasoning = HasReasoningEffort(completionParameters)
            || HasRawReasoningEffort(providerParameters);

        if (hasExplicitThinking && !thinkingEnabled && wantsReasoning)
        {
            throw new InvalidOperationException(
                "DeepSeek reasoning effort requires thinking mode to be enabled.");
        }

        if (hasExplicitThinking)
            return providerParameters;

        var prepared = providerParameters is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(providerParameters, StringComparer.Ordinal);

        prepared["thinking"] = JsonSerializer.SerializeToElement(new
        {
            type = wantsReasoning ? "enabled" : "disabled"
        });

        return prepared;
    }

    private static bool HasReasoningEffort(CompletionParameters? completionParameters)
        => !string.IsNullOrWhiteSpace(completionParameters?.ReasoningEffort);

    private static bool HasRawReasoningEffort(Dictionary<string, JsonElement>? providerParameters)
        => providerParameters?.TryGetValue("reasoning_effort", out var effort) == true
           && effort.ValueKind != JsonValueKind.Null
           && effort.ValueKind != JsonValueKind.Undefined;

    private static bool IsThinkingEnabled(Dictionary<string, JsonElement>? providerParameters)
    {
        if (providerParameters?.TryGetValue("thinking", out var thinking) != true)
            return false;

        if (thinking.ValueKind == JsonValueKind.Null ||
            thinking.ValueKind == JsonValueKind.Undefined)
            return false;

        if (thinking.ValueKind != JsonValueKind.Object ||
            !thinking.TryGetProperty("type", out var type))
        {
            return true;
        }

        return !string.Equals(type.GetString(), "disabled", StringComparison.OrdinalIgnoreCase);
    }
}
