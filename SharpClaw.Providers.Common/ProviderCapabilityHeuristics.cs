using SharpClaw.Contracts.Models;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Providers.Common;

/// <summary>
/// Centralised model-name heuristics shared by the per-provider
/// capability resolvers. Each provider plugin composes the family-specific
/// chat/vision predicates it cares about with the universal non-chat tag
/// detection (embedding, tts, image-generation, moderation, legacy
/// completions-only base models, audio/realtime variants).
/// </summary>
/// <remarks>
/// All inputs are normalised to <see cref="string.ToLowerInvariant"/> by
/// <see cref="ToKey"/>; predicates assume their argument is already
/// lowercase. The <c>For*</c> helpers return the final tag set for a
/// single provider family; <see cref="ForGeneric"/> aggregates every
/// family and is used by gateway-style providers (OpenRouter, Groq,
/// Cerebras, Vercel AI Gateway, ZAI, GitHub Copilot) plus the open-ended
/// Custom / Ollama / LlamaSharp endpoints.
/// </remarks>
public static class ProviderCapabilityHeuristics
{
    private static string ToKey(string name) => name.ToLowerInvariant();

    /// <summary>
    /// Detects model names whose tag set is fully determined by their
    /// shape regardless of which provider hosts them (embeddings, TTS,
    /// image generation, moderation, legacy completions). Returns
    /// <see langword="null"/> when the name is *not* one of those — the
    /// caller should then run its family-specific chat/vision check.
    /// </summary>
    public static HashSet<string>? TryNonChatTags(string nameLower)
    {
        if (nameLower.Contains("embedding") || nameLower.Contains("embed"))
            return [WellKnownCapabilityKeys.Embedding];

        if (nameLower.StartsWith("tts-"))
            return [WellKnownCapabilityKeys.Tts];

        if (nameLower.StartsWith("dall-e") || nameLower.StartsWith("gpt-image")
            || nameLower.StartsWith("chatgpt-image") || nameLower.StartsWith("sora"))
            return [WellKnownCapabilityKeys.ImageGeneration];

        if (nameLower.Contains("moderation"))
            return [];

        if (nameLower.StartsWith("babbage") || nameLower.StartsWith("davinci")
            || nameLower.EndsWith("-instruct"))
            return [];

        if (nameLower.Contains("-tts"))
            return [WellKnownCapabilityKeys.Chat, WellKnownCapabilityKeys.Tts];

        if (nameLower.Contains("audio") || nameLower.Contains("realtime"))
            return [WellKnownCapabilityKeys.Chat, WellKnownCapabilityKeys.Tts];

        return null;
    }

    public static bool IsOpenAIChat(string n)
        => n.StartsWith("gpt-3.5-turbo") || n.StartsWith("gpt-4") || n.StartsWith("gpt-5")
        || n.StartsWith("chatgpt-")
        || n.StartsWith("o1") || n.StartsWith("o3") || n.StartsWith("o4");

    public static bool IsOpenAIVision(string n)
        => n.StartsWith("gpt-4o") || n.StartsWith("gpt-4-turbo")
        || n.StartsWith("gpt-4-vision") || n.StartsWith("gpt-5")
        || n.StartsWith("o1") || n.StartsWith("o3") || n.StartsWith("o4");

    public static bool IsAnthropicChat(string n) => n.StartsWith("claude-");
    public static bool IsAnthropicVision(string n)
        => n.StartsWith("claude-3") || n.StartsWith("claude-4");

    public static bool IsGoogleChat(string n) => n.StartsWith("gemini-");
    public static bool IsGoogleVision(string n)
        => n.StartsWith("gemini-1.5") || n.StartsWith("gemini-2")
        || n.StartsWith("gemini-pro-vision");

    public static bool IsMistralChat(string n)
        => n.StartsWith("mistral") || n.StartsWith("mixtral")
        || n.StartsWith("pixtral") || n.StartsWith("codestral")
        || n.StartsWith("ministral");
    public static bool IsMistralVision(string n) => n.StartsWith("pixtral");

    public static bool IsMetaChat(string n)
        => n.StartsWith("llama-") || n.StartsWith("meta-llama");
    public static bool IsMetaVision(string n)
        => (n.StartsWith("llama-3.2") && n.Contains("vision"))
        || n.StartsWith("llama-4");

    public static bool IsXaiChat(string n) => n.StartsWith("grok-");
    public static bool IsXaiVision(string n)
        => n.StartsWith("grok-2-vision") || n.StartsWith("grok-3");

    public static bool IsMinimaxChat(string n)
        => n.StartsWith("minimax") || n.StartsWith("abab");
    public static bool IsMinimaxVision(string n) => n.StartsWith("minimax-vl");

    /// <summary>
    /// Generic non-vendor chat families surfaced by gateway providers
    /// (OpenRouter, Groq, Cerebras, Vercel, ZAI). Vision is not inferred
    /// for these — operators add the tag manually with
    /// <c>model add --cap Vision</c> when needed.
    /// </summary>
    public static bool IsGenericFamilyChat(string n)
        => n.StartsWith("deepseek") || n.StartsWith("qwen")
        || n.StartsWith("phi-") || n.StartsWith("command")
        || n.StartsWith("yi-") || n.StartsWith("jamba");

    private static HashSet<string> BuildChatTags(bool isVision)
        => isVision
            ? [WellKnownCapabilityKeys.Chat, WellKnownCapabilityKeys.Vision]
            : [WellKnownCapabilityKeys.Chat];

    private static HashSet<string> Resolve(string name, Func<string, bool> chat, Func<string, bool> vision)
    {
        var n = ToKey(name);
        var nonChat = TryNonChatTags(n);
        if (nonChat is not null) return nonChat;
        if (!chat(n)) return [];
        return BuildChatTags(vision(n));
    }

    public static HashSet<string> ForOpenAI(string name)
        => Resolve(name, IsOpenAIChat, IsOpenAIVision);

    public static HashSet<string> ForAnthropic(string name)
        => Resolve(name, IsAnthropicChat, IsAnthropicVision);

    public static HashSet<string> ForGoogle(string name)
        => Resolve(name, IsGoogleChat, IsGoogleVision);

    public static HashSet<string> ForMistral(string name)
        => Resolve(name, IsMistralChat, IsMistralVision);

    public static HashSet<string> ForXai(string name)
        => Resolve(name, IsXaiChat, IsXaiVision);

    public static HashSet<string> ForMinimax(string name)
        => Resolve(name, IsMinimaxChat, IsMinimaxVision);

    /// <summary>
    /// Aggregate resolver matching any known chat or vision family.
    /// Used by gateway providers and open-ended endpoints
    /// (Custom, Ollama, LlamaSharp) that may host arbitrary models.
    /// Equivalent to the legacy <c>InferCapabilitiesAndTags</c>
    /// behaviour that previously lived on <c>ProviderService</c>.
    /// </summary>
    public static HashSet<string> ForGeneric(string name)
    {
        var n = ToKey(name);
        var nonChat = TryNonChatTags(n);
        if (nonChat is not null) return nonChat;

        var chat = IsOpenAIChat(n) || IsAnthropicChat(n) || IsGoogleChat(n)
                || IsMistralChat(n) || IsMetaChat(n) || IsXaiChat(n)
                || IsMinimaxChat(n) || IsGenericFamilyChat(n);
        if (!chat) return [];

        var vision = IsOpenAIVision(n) || IsAnthropicVision(n) || IsGoogleVision(n)
                  || IsMistralVision(n) || IsMetaVision(n) || IsXaiVision(n)
                  || IsMinimaxVision(n);
        return BuildChatTags(vision);
    }
}

/// <summary>
/// Adapter exposing a <see cref="ProviderCapabilityHeuristics"/> family
/// resolver as an <see cref="IModelCapabilityResolver"/>. Plugins use
/// this to plug a static-method resolver into the plugin contract.
/// </summary>
public sealed class HeuristicCapabilityResolver(Func<string, HashSet<string>> resolver)
    : IModelCapabilityResolver
{
    public HashSet<string> Resolve(string modelName) => resolver(modelName);
}
