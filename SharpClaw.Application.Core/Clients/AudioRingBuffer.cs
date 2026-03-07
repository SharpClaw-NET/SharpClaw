namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// Thread-safe (single-writer, multi-reader) ring buffer of float PCM
/// samples at a fixed sample rate. Supports writing incoming audio and
/// reading the most recent N seconds as a contiguous array.
/// <para>
/// The buffer is pre-allocated to hold exactly
/// <paramref name="capacitySeconds"/> seconds of audio and wraps around
/// when full — older samples are silently overwritten.
/// </para>
/// <para>
/// <b>Threading model:</b> One thread writes via <see cref="Write"/>
/// (the WASAPI capture callback) while one or more threads read via
/// <see cref="GetLastSeconds"/>, <see cref="GetWindowStartTime"/>,
/// etc. (inference loops from multiple transcription jobs sharing the
/// same device). <see cref="_totalWritten"/> is published with
/// <see cref="Volatile.Write"/> after each sample so every reader
/// sees a consistent sample count and buffer contents. Readers
/// snapshot <see cref="_totalWritten"/> with <see cref="Volatile.Read"/>
/// before copying data. Multiple concurrent readers are safe because
/// they only read the buffer array — no reader-side mutation occurs.
/// The writer advances at real-time speed (16 kHz) while readers copy
/// at memory speed, so the 5 s headroom (30 s capacity − 25 s window)
/// is more than sufficient to prevent the writer from lapping any
/// reader.
/// </para>
/// </summary>
public sealed class AudioRingBuffer
{
    private readonly float[] _buffer;
    private readonly int _sampleRate;
    private long _totalWritten;

    /// <summary>
    /// Creates a ring buffer that can hold <paramref name="capacitySeconds"/>
    /// seconds of audio at the given <paramref name="sampleRate"/>.
    /// </summary>
    public AudioRingBuffer(int sampleRate, int capacitySeconds)
    {
        _sampleRate = sampleRate;
        _buffer = new float[sampleRate * capacitySeconds];
    }

    /// <summary>Total number of samples written since construction.</summary>
    public long TotalWritten => Volatile.Read(ref _totalWritten);

    /// <summary>Number of usable samples currently in the buffer.</summary>
    public int Available => (int)Math.Min(Volatile.Read(ref _totalWritten), _buffer.Length);

    /// <summary>
    /// Appends PCM float samples to the ring buffer. Safe to call from
    /// the audio-capture callback (single writer). Uses
    /// <see cref="Volatile.Write"/> to publish the updated counter so
    /// the reader on another thread sees consistent data.
    /// </summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            var pos = _totalWritten;
            _buffer[(int)(pos % _buffer.Length)] = sample;
            // Publish the incremented counter AFTER the sample is stored
            // so the reader never sees _totalWritten pointing at unwritten data.
            Volatile.Write(ref _totalWritten, pos + 1);
        }
    }

    /// <summary>
    /// Returns the most recent <paramref name="seconds"/> seconds of
    /// audio as a contiguous float array in chronological order.
    /// If fewer samples are available, returns everything available.
    /// </summary>
    public float[] GetLastSeconds(int seconds)
    {
        var requested = _sampleRate * seconds;
        // Snapshot _totalWritten once so all calculations use the same value.
        var written = Volatile.Read(ref _totalWritten);
        var available = (int)Math.Min(written, _buffer.Length);
        var count = Math.Min(requested, available);

        if (count == 0)
            return [];

        var result = new float[count];
        var startPos = written - count;

        for (var i = 0; i < count; i++)
            result[i] = _buffer[(int)((startPos + i) % _buffer.Length)];

        return result;
    }

    /// <summary>
    /// Returns the absolute time (in seconds since recording start) of
    /// the first sample currently in the buffer. This is the time offset
    /// that Whisper segment timestamps are relative to.
    /// </summary>
    public double BufferStartTime
    {
        get
        {
            var written = Volatile.Read(ref _totalWritten);
            var available = (int)Math.Min(written, _buffer.Length);
            var startSample = written - available;
            return (double)startSample / _sampleRate;
        }
    }

    /// <summary>
    /// Returns the absolute time of the start of the last N seconds
    /// window (or the start of available audio if less is buffered).
    /// Use this as the base offset for converting Whisper-relative
    /// timestamps to absolute stream time.
    /// </summary>
    public double GetWindowStartTime(int windowSeconds)
    {
        var requested = _sampleRate * windowSeconds;
        var written = Volatile.Read(ref _totalWritten);
        var available = (int)Math.Min(written, _buffer.Length);
        var count = Math.Min(requested, available);
        var startSample = written - count;
        return (double)startSample / _sampleRate;
    }

    /// <summary>
    /// Checks whether the last <paramref name="seconds"/> seconds of
    /// audio contain any speech based on a simple RMS energy threshold.
    /// </summary>
    public bool ContainsSpeech(int seconds, float silenceThreshold = AudioVad.DefaultSilenceThreshold)
    {
        var samples = GetLastSeconds(seconds);
        return samples.Length > 0 && !AudioVad.IsSilent(samples, silenceThreshold);
    }
}
