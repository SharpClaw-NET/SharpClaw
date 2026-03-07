using System.Text;
using SharpClaw.Application.Core.LocalInference;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// Transcription client that runs Whisper inference locally via
/// Whisper.net (managed wrapper over whisper.cpp).
/// <para>
/// The <paramref name="model"/> parameter in
/// <see cref="TranscribeAsync"/> receives the absolute file path to
/// the GGML/GGUF Whisper model. The <see cref="WhisperModelManager"/>
/// caches loaded models across calls.
/// </para>
/// <para>
/// Silence suppression is configured through whisper.cpp's native
/// thresholds (<c>no_speech_threshold</c>, <c>logprob_threshold</c>,
/// <c>entropy_threshold</c>). <c>WithNoContext()</c> prevents
/// conditioning on previous text which reduces hallucinated
/// repetitions when processing overlapping sliding windows.
/// </para>
/// </summary>
public sealed class LocalTranscriptionClient(
    WhisperModelManager whisperManager) : ITranscriptionApiClient
{
    public ProviderType ProviderType => ProviderType.Local;

    /// <summary>
    /// Segments with no-speech probability above this threshold are
    /// suppressed by whisper.cpp before being returned.
    /// </summary>
    private const float NoSpeechThreshold = 0.6f;

    /// <summary>
    /// Segments with average log-probability below this threshold are
    /// considered low-confidence and suppressed by whisper.cpp.
    /// </summary>
    private const float LogProbThreshold = -1.0f;

    /// <summary>
    /// Entropy (compression ratio proxy) threshold. Segments above
    /// this value are likely hallucinated/repetitive text.
    /// </summary>
    private const float EntropyThreshold = 2.4f;

    public async Task<TranscriptionChunkResult> TranscribeAsync(
        HttpClient httpClient,
        string apiKey,
        string model,
        byte[] audioData,
        string? language = null,
        string? prompt = null,
        CancellationToken ct = default)
    {
        // 'model' contains the absolute path to the local Whisper GGML file.
        var factory = whisperManager.GetOrLoad(model);

        var builder = factory.CreateBuilder();

        if (!string.IsNullOrEmpty(language))
            builder.WithLanguage(language);

        builder.WithThreads(Math.Max(1, Environment.ProcessorCount / 2));

        // Silence suppression: let whisper.cpp discard segments that
        // are likely noise or hallucinated text.
        builder.WithNoSpeechThreshold(NoSpeechThreshold);
        builder.WithLogProbThreshold(LogProbThreshold);
        builder.WithEntropyThreshold(EntropyThreshold);

        // Prevent auto-conditioning on previous segments within the
        // same chunk — critical for the sliding-window pipeline where
        // the same audio is re-processed across overlapping windows.
        builder.WithNoContext();

        // Explicit prompt conditioning from previously finalized text
        // is safe even with WithNoContext — it provides vocabulary /
        // style hints without the hallucination risk of auto-context.
        if (!string.IsNullOrEmpty(prompt))
            builder.WithPrompt(prompt);

        using var processor = builder.Build();

        // Ensure audio is mono 16 kHz 16-bit PCM — optimal for Whisper.
        // Live-capture audio is already in this format (fast path);
        // future file/stream inputs get resampled here automatically.
        audioData = AudioNormalizer.Normalize(audioData);

        using var ms = new MemoryStream(audioData);

        var segments = new List<TranscriptionChunkSegment>();
        var fullText = new StringBuilder();
        double duration = 0;

        await foreach (var segment in processor.ProcessAsync(ms, ct))
        {
            var text = segment.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(text))
                continue;

            segments.Add(new TranscriptionChunkSegment(
                text,
                segment.Start.TotalSeconds,
                segment.End.TotalSeconds,
                segment.Probability));

            fullText.Append(segment.Text);
            duration = Math.Max(duration, segment.End.TotalSeconds);
        }

        // If no segments but we have text, create a single segment
        if (segments.Count == 0 && fullText.Length > 0)
        {
            segments.Add(new TranscriptionChunkSegment(
                fullText.ToString().Trim(), 0, duration, null));
        }

        return new TranscriptionChunkResult(
            fullText.ToString().Trim(), duration, segments, Language: null);
    }
}
