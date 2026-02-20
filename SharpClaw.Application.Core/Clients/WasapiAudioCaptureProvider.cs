using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// Captures audio from a WASAPI input device using NAudio.
/// Records 16-bit 16 kHz mono PCM and delivers WAV-encoded chunks.
/// <para>
/// <b>Windows-only.</b> On non-Windows platforms, <see cref="ListDevices"/>
/// returns an empty list and <see cref="CaptureAsync"/> throws
/// <see cref="PlatformNotSupportedException"/>.
/// </para>
/// </summary>
public sealed class WasapiAudioCaptureProvider : IAudioCaptureProvider
{
    private const int TargetSampleRate = 16_000;
    private const int TargetBitsPerSample = 16;
    private const int TargetChannels = 1;

    public IReadOnlyList<(string Id, string Name)> ListDevices()
    {
        if (!OperatingSystem.IsWindows())
            return [];

        var enumerator = new MMDeviceEnumerator();
        var devices = enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => (d.ID, d.FriendlyName))
            .ToList();

        return devices;
    }

    public async Task CaptureAsync(
        string? deviceIdentifier,
        TimeSpan chunkDuration,
        Func<byte[], int, Task> onChunkReady,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "WASAPI audio capture is only supported on Windows.");

        var enumerator = new MMDeviceEnumerator();
        var device = string.IsNullOrEmpty(deviceIdentifier) || deviceIdentifier == "default"
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
            : enumerator.GetDevice(deviceIdentifier);

        // Capture in the device's native format; we'll resample below
        using var capture = new WasapiCapture(device);

        var targetFormat = new WaveFormat(TargetSampleRate, TargetBitsPerSample, TargetChannels);
        var chunkByteTarget = targetFormat.AverageBytesPerSecond * (int)chunkDuration.TotalSeconds;

        using var buffer = new MemoryStream();
        var chunkIndex = 0;

        // ── INVARIANT: one job = one task, sequential chunk processing. ──
        // The onChunkReady callback MUST NEVER execute concurrently with
        // itself.  Callers (LiveTranscriptionOrchestrator) close over
        // mutable state (consecutiveErrors, streamStartTime) that is NOT
        // thread-safe.  All chunks flow through a Channel and are drained
        // by a single consumer task to guarantee this.
        // DO NOT replace this with Task.Run-per-chunk or Parallel.ForEach.
        var channel = Channel.CreateUnbounded<(byte[] Wav, int Index)>(
            new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Exception? callbackError = null;

        var consumerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var (wav, idx) in channel.Reader.ReadAllAsync(linkedCts.Token))
                {
                    await onChunkReady(wav, idx);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref callbackError, ex, null);
                await linkedCts.CancelAsync();
            }
        });

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        linkedCts.Token.Register(() => tcs.TrySetResult());

        capture.DataAvailable += (_, e) =>
        {
            if (linkedCts.IsCancellationRequested) return;

            // Resample captured data to 16 kHz mono 16-bit
            var resampled = Resample(e.Buffer, e.BytesRecorded, capture.WaveFormat, targetFormat);
            buffer.Write(resampled, 0, resampled.Length);

            if (buffer.Length >= chunkByteTarget)
            {
                var pcmBytes = buffer.ToArray();
                buffer.SetLength(0);
                buffer.Position = 0;

                var wavBytes = PcmToWav(pcmBytes, targetFormat);
                var idx = chunkIndex++;

                channel.Writer.TryWrite((wavBytes, idx));
            }
        };

        capture.RecordingStopped += (_, _) => tcs.TrySetResult();

        capture.StartRecording();

        await tcs.Task;

        capture.StopRecording();

        // Signal consumer that no more chunks will arrive, then wait for it.
        channel.Writer.TryComplete();
        await consumerTask;

        // Re-throw the callback error so the caller sees it
        if (callbackError is not null)
            throw callbackError;

        // Flush remaining audio if any
        if (buffer.Length > 0)
        {
            var pcmBytes = buffer.ToArray();
            var wavBytes = PcmToWav(pcmBytes, targetFormat);
            await onChunkReady(wavBytes, chunkIndex);
        }
    }

    public void Dispose() { }

    // ═══════════════════════════════════════════════════════════════
    // PCM helpers
    // ═══════════════════════════════════════════════════════════════

    private static byte[] Resample(
        byte[] source, int bytesRecorded,
        WaveFormat sourceFormat, WaveFormat targetFormat)
    {
        if (sourceFormat.SampleRate == targetFormat.SampleRate
            && sourceFormat.Channels == targetFormat.Channels
            && sourceFormat.BitsPerSample == targetFormat.BitsPerSample)
        {
            return source[..bytesRecorded];
        }

        using var sourceStream = new RawSourceWaveStream(
            new MemoryStream(source, 0, bytesRecorded), sourceFormat);
        using var resampler = new MediaFoundationResampler(sourceStream, targetFormat);
        resampler.ResamplerQuality = 60;

        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
            output.Write(buffer, 0, read);

        return output.ToArray();
    }

    private static byte[] PcmToWav(byte[] pcmData, WaveFormat format)
    {
        using var ms = new MemoryStream();
        using (var writer = new WaveFileWriter(ms, format))
        {
            writer.Write(pcmData, 0, pcmData.Length);
            writer.Flush();
        }
        return ms.ToArray();
    }
}
