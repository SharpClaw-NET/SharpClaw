namespace SharpClaw.Contracts.Models;

/// <summary>
/// Conventional capability tag keys for model capabilities, stored as
/// comma-separated entries in <c>ModelDB.CapabilityTagsRaw</c>.
///
/// Capability tags are free-form strings — modules and providers may
/// introduce additional keys without changes to this file. The values
/// listed here are the well-known names used by core and shared provider
/// code so spelling stays consistent across the solution. Only
/// <see cref="Chat"/> is observed by core directly (it gates chat-service
/// model assignment); the others are conventions consumed by provider
/// modules and capability-aware UI.
/// </summary>
public static class WellKnownCapabilityKeys
{
    /// <summary>
    /// The model supports chat completions. Observed by core to decide
    /// whether a model is eligible for the chat service.
    /// </summary>
    public const string Chat = "chat";

    /// <summary>The model supports vision (image) inputs. Convention used by provider modules.</summary>
    public const string Vision = "vision";

    /// <summary>The model produces embeddings. Convention used by provider modules.</summary>
    public const string Embedding = "embedding";

    /// <summary>The model produces speech audio (text-to-speech). Convention used by provider modules.</summary>
    public const string Tts = "tts";

    /// <summary>The model generates images. Convention used by provider modules.</summary>
    public const string ImageGeneration = "image-generation";
}
