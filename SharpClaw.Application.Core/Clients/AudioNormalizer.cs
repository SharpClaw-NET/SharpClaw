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
/// If the input is already in the target format the bytes are returned
/// unchanged (zero-copy fast path). Otherwise NAudio handles the
/// conversion pipeline: stereo→mono, resample→16 kHz, re-encode to
/// 16-bit PCM WAV.
/// </para>
/// </summary>
internal static class AudioNormalizer
{
    internal const int TargetSampleRate = 16_000;
    internal const int TargetBitsPerSample = 16;
    internal const int TargetChannels = 1;

    private static readonly WaveFormat TargetFormat =
        new(TargetSampleRate, TargetBitsPerSample, TargetChannels);

    /// <summary>
    /// Ensures the WAV audio is mono, 16 kHz, 16-bit PCM.
    /// Returns the original bytes if they already match.
    /// </summary>
    internal static byte[] Normalize(byte[] wavData)
    {
        using var inputStream = new MemoryStream(wavData);
        using var reader = new WaveFileReader(inputStream);

        var src = reader.WaveFormat;

        // Fast path — already in the target format.
        if (src.SampleRate == TargetSampleRate
            && src.Channels == TargetChannels
            && src.BitsPerSample == TargetBitsPerSample
            && src.Encoding == WaveFormatEncoding.Pcm)
        {
            return wavData;
        }

        // Build conversion pipeline: source → sample provider → mono → resample → 16-bit WAV.
        ISampleProvider pipeline = reader.ToSampleProvider();

        if (src.Channels > 1)
        {
            pipeline = new StereoToMonoSampleProvider(pipeline)
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f
            };
        }

        if (src.SampleRate != TargetSampleRate)
        {
            pipeline = new WdlResamplingSampleProvider(pipeline, TargetSampleRate);
        }

        using var output = new MemoryStream();
        var target16 = pipeline.ToWaveProvider16();
        using (var writer = new WaveFileWriter(new IgnoreDisposeStream(output), target16.WaveFormat))
        {
            var buf = new byte[4096];
            int read;
            while ((read = target16.Read(buf, 0, buf.Length)) > 0)
                writer.Write(buf, 0, read);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Wraps a stream to prevent <see cref="WaveFileWriter"/> from
    /// closing the underlying <see cref="MemoryStream"/>.
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
