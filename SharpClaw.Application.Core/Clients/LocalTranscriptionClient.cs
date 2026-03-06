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
/// </summary>
public sealed class LocalTranscriptionClient(
    WhisperModelManager whisperManager) : ITranscriptionApiClient
{
    public ProviderType ProviderType => ProviderType.Local;

    public async Task<TranscriptionChunkResult> TranscribeAsync(
        HttpClient httpClient,
        string apiKey,
        string model,
        byte[] audioData,
        string? language = null,
        CancellationToken ct = default)
    {
        // 'model' contains the absolute path to the local Whisper GGML file.
        var factory = whisperManager.GetOrLoad(model);

        var builder = factory.CreateBuilder();

        if (!string.IsNullOrEmpty(language))
            builder.WithLanguage(language);

        builder.WithThreads(Math.Max(1, Environment.ProcessorCount / 2));

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
            fullText.ToString().Trim(), duration, segments);
    }
}
