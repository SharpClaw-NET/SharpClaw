using System.Collections.Frozen;

namespace SharpClaw.Application.Core.LocalInference;

/// <summary>
/// Maps a GGUF <c>general.architecture</c> value to the set of local providers
/// that should be attempted for registration.
/// </summary>
public static class GgufArchitectureClassifier
{
    // Architectures that Whisper.net loads. All others are treated as LlamaSharp-only
    // unless they also appear in the multimodal set.
    private static readonly FrozenSet<string> WhisperOnly =
        FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "whisper");

    // Architectures known to carry audio capability alongside language modelling.
    // Both runtimes are attempted for these.
    private static readonly FrozenSet<string> MultimodalAudio =
        FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
            "qwen2_audio", "qwen2_5_omni", "llama_omni");

    public sealed record ProviderTargets(bool LlamaSharp, bool Whisper);

    /// <summary>
    /// Returns which providers to attempt for the given architecture string.
    /// A <see langword="null"/> architecture (unreadable header) attempts both.
    /// </summary>
    public static ProviderTargets Classify(string? architecture)
    {
        if (architecture is null)
            return new(LlamaSharp: true, Whisper: true);

        if (WhisperOnly.Contains(architecture))
            return new(LlamaSharp: false, Whisper: true);

        if (MultimodalAudio.Contains(architecture))
            return new(LlamaSharp: true, Whisper: true);

        return new(LlamaSharp: true, Whisper: false);
    }
}
