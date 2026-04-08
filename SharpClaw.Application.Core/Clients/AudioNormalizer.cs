using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// Normalises audio to the optimal format for Whisper / ASR models:
/// mono, 16 kHz sample rate, 16-bit PCM, WAV container.
/// <para>
/// Speech contains little energy above ~8 kHz so 16 kHz satisfies
/// Nyquist while minimising model compute. Stereo adds no useful
/// information for speech recognition.
/// </para>
/// <para>
/// Pipeline: input stream → decode → stereo→mono → resample→16 kHz →
/// write 16-bit PCM WAV to output stream. No temp files.
/// </para>
/// <para>
/// If the input is already in the target format the bytes are returned
/// unchanged (fast path).
/// </para>
/// </summary>
public static class AudioNormalizer
{
    public const int TargetSampleRate = 16_000;
    public const int TargetBitsPerSample = 16;
    public const int TargetChannels = 1;

    private static readonly WaveFormat TargetFormat =
        new(TargetSampleRate, TargetBitsPerSample, TargetChannels);

    /// <summary>
    /// Ensures the WAV audio is mono, 16 kHz, 16-bit PCM.
    /// Returns the original bytes when they already match.
    /// </summary>
    public static byte[] Normalize(byte[] wavData)
    {
        using var input = new MemoryStream(wavData);
        using var reader = new WaveFileReader(input);

        var src = reader.WaveFormat;

        // Fast path — already in the target format.
        if (src.SampleRate == TargetSampleRate
            && src.Channels == TargetChannels
            && src.BitsPerSample == TargetBitsPerSample
            && src.Encoding == WaveFormatEncoding.Pcm)
        {
            return wavData;
        }

        using var output = new MemoryStream();
        Normalize(reader, output);
        return output.ToArray();
    }

    /// <summary>
    /// Stream-to-stream normalisation. Reads from <paramref name="input"/>
    /// and writes a Whisper-ready 16 kHz mono 16-bit PCM WAV into
    /// <paramref name="output"/>. No temp files are created.
    /// </summary>
    public static void Normalize(WaveFileReader input, Stream output)
    {
        ISampleProvider pipeline = input.ToSampleProvider();

        // stereo → mono
        if (input.WaveFormat.Channels > 1)
        {
            pipeline = new StereoToMonoSampleProvider(pipeline)
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f
            };
        }

        // resample → 16 kHz
        if (input.WaveFormat.SampleRate != TargetSampleRate)
        {
            pipeline = new WdlResamplingSampleProvider(pipeline, TargetSampleRate);
        }

        // write 16-bit PCM WAV
        using var writer = new WaveFileWriter(new IgnoreDisposeStream(output), TargetFormat);

        var buffer = new float[TargetSampleRate]; // 1 second of samples
        int read;
        while ((read = pipeline.Read(buffer, 0, buffer.Length)) > 0)
        {
            writer.WriteSamples(buffer, 0, read);
        }
    }

    /// <summary>
    /// Wraps a stream to prevent <see cref="WaveFileWriter"/> from
    /// closing the underlying stream on dispose.
    /// </summary>
    private sealed class IgnoreDisposeStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) { /* intentionally left empty */ }
    }
}
