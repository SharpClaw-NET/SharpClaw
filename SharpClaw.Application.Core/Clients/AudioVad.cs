namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// Simple energy-based Voice Activity Detection (VAD). Computes RMS
/// energy over a span of float PCM samples and compares against a
/// configurable silence threshold.
/// <para>
/// This is a lightweight pre-filter to avoid sending purely silent
/// audio to the transcription API. It does NOT replace Whisper's own
/// <c>no_speech_prob</c> detection — both are used in tandem.
/// </para>
/// </summary>
internal static class AudioVad
{
    /// <summary>
    /// Default RMS threshold below which audio is considered silent.
    /// Typical speech RMS is 0.02–0.1; silence/noise floor is &lt;0.003.
    /// </summary>
    internal const float DefaultSilenceThreshold = 0.005f;

    /// <summary>
    /// Returns <see langword="true"/> when the RMS energy of the samples
    /// is below <paramref name="threshold"/>.
    /// </summary>
    public static bool IsSilent(ReadOnlySpan<float> samples, float threshold = DefaultSilenceThreshold)
    {
        if (samples.Length == 0)
            return true;

        double sum = 0;
        foreach (var s in samples)
            sum += s * (double)s;

        var rms = Math.Sqrt(sum / samples.Length);
        return rms < threshold;
    }
}
