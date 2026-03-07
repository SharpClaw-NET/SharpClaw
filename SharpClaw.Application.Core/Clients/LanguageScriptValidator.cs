using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// Provides prompt seeds and reinforcement text for Whisper language
/// enforcement.  Language data is loaded once from the embedded
/// <c>transcription-language-seeds.json</c> resource so new languages
/// can be added by editing the JSON file alone.
/// </summary>
internal static class LanguageScriptValidator
{
    // ═════════════════════════════════════════════════════════════
    // Public API
    // ═════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns <see langword="true"/> when the response-level language
    /// tag matches the expected language.  Both values are normalised
    /// to their base subtag before comparison.
    /// </summary>
    public static bool ResponseLanguageMatches(string expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(actual))
            return true;

        return string.Equals(
            NormaliseBase(expected), NormaliseBase(actual),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns a short natural-language phrase in the target language
    /// suitable for use as a Whisper <c>prompt</c> seed.
    /// Returns <see cref="string.Empty"/> when no seed is available.
    /// </summary>
    public static string GetPromptSeed(string language) =>
        TryGetEntry(language, out var entry) ? entry.Seed : "";

    /// <summary>
    /// Builds a prompt prefix with escalating language reinforcement.
    /// <list type="bullet">
    ///   <item><description>Level 1 — single seed phrase</description></item>
    ///   <item><description>Level 2 — triple-repeated seed</description></item>
    ///   <item><description>Level 3 — instruction preamble + double seed</description></item>
    ///   <item><description>Level 4+ — maximum reinforcement block</description></item>
    /// </list>
    /// </summary>
    public static string GetReinforcedPrompt(
        string language, string existingPrompt, int level)
    {
        if (!TryGetEntry(language, out var entry))
            return existingPrompt;

        var seed = entry.Seed;
        if (string.IsNullOrEmpty(seed))
            return existingPrompt;

        var prefix = level switch
        {
            1 => seed,
            2 => $"{seed} {seed} {seed}",
            3 => !string.IsNullOrEmpty(entry.Preamble)
                    ? $"{entry.Preamble} {seed} {seed}"
                    : $"{seed} {seed} {seed}",
            _ => BuildMaxReinforcement(entry),
        };

        return string.IsNullOrEmpty(existingPrompt)
            ? prefix
            : $"{prefix} {existingPrompt}";
    }

    // ═════════════════════════════════════════════════════════════
    // JSON loading
    // ═════════════════════════════════════════════════════════════

    private sealed class LanguageEntry
    {
        [JsonPropertyName("seed")]
        public string Seed { get; set; } = "";

        [JsonPropertyName("preamble")]
        public string? Preamble { get; set; }

        [JsonPropertyName("filler")]
        public string? Filler { get; set; }
    }

    private static readonly Dictionary<string, LanguageEntry> Entries = LoadEntries();

    private static Dictionary<string, LanguageEntry> LoadEntries()
    {
        const string resourceName = "SharpClaw.Application.Core.transcription-language-seeds.json";

        using var stream = typeof(LanguageScriptValidator).Assembly
            .GetManifestResourceStream(resourceName);

        if (stream is null)
            return new Dictionary<string, LanguageEntry>(StringComparer.Ordinal);

        return JsonSerializer.Deserialize<Dictionary<string, LanguageEntry>>(stream)
            ?? new Dictionary<string, LanguageEntry>(StringComparer.Ordinal);
    }

    // ═════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════

    private static string NormaliseBase(string language)
    {
        var lang = language.ToLowerInvariant();
        var dash = lang.IndexOf('-');
        return dash > 0 ? lang[..dash] : lang;
    }

    private static bool TryGetEntry(string language, out LanguageEntry entry)
    {
        var lang = NormaliseBase(language);
        return Entries.TryGetValue(lang, out entry!);
    }

    private static string BuildMaxReinforcement(LanguageEntry entry)
    {
        var seed = entry.Seed;
        var parts = new List<string>(6);

        if (!string.IsNullOrEmpty(entry.Preamble))
            parts.Add(entry.Preamble);

        parts.Add(seed);
        parts.Add(seed);
        parts.Add(seed);

        if (!string.IsNullOrEmpty(entry.Filler))
            parts.Add(entry.Filler);

        parts.Add(seed);
        return string.Join(" ", parts);
    }
}
