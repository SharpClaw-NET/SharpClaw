using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

using SharpClaw.Application.Infrastructure.Models.Jobs;
using SharpClaw.Modules.Transcription.Audio;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Infrastructure.Models;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Utils.Security;

using LanguageValidator = SharpClaw.Modules.Transcription.Clients.LanguageScriptValidator;
using TranscriptionChunkResult = SharpClaw.Modules.Transcription.Clients.TranscriptionChunkResult;
using TranscriptionChunkSegment = SharpClaw.Modules.Transcription.Clients.TranscriptionChunkSegment;

namespace SharpClaw.Modules.Transcription.Services;

/// <summary>
/// Singleton service that manages active audio-capture→STT→push loops.
/// Audio flows: mic → ring buffer → silence gate → sliding window → Whisper → dedup → output.
/// Two-pass mode emits provisional segments immediately, then finalizes
/// them once the commit delay confirms they are stable.
/// </summary>
public sealed class LiveTranscriptionOrchestrator(
    IServiceScopeFactory scopeFactory,
    Clients.SharedAudioCaptureManager sharedCapture,
    Clients.TranscriptionApiClientFactory transcriptionClientFactory,
    IHttpClientFactory httpClientFactory,
    EncryptionOptions encryptionOptions,
    ILogger<LiveTranscriptionOrchestrator> logger) : ILiveTranscriptionOrchestrator
{
    private const int WindowSeconds = 10;
    private const int InferenceIntervalSeconds = 2;
    private const int BufferCapacitySeconds = 15;
    private const double CommitDelaySeconds = 2.0;
    private const int MaxPromptChars = 250;
    private const int SampleRate = 16_000;

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeSessions = new();

    /// <summary>
    /// Returns <see langword="true"/> when the given provider type has a
    /// registered <see cref="ITranscriptionApiClient"/>.
    /// Call before <see cref="Start"/> to fail early with a clear error.
    /// </summary>
    public bool SupportsProvider(ProviderType providerType) =>
        transcriptionClientFactory.Supports(providerType);

    /// <summary>
    /// Starts live audio capture and transcription for a job.
    /// The caller must have already created the <see cref="TranscriptionJobDB"/>
    /// record in the database.
    /// </summary>
    public void Start(
        Guid jobId, Guid modelId, string? deviceIdentifier, string? language,
        TranscriptionMode? mode = null, int? windowSeconds = null, int? stepSeconds = null)
    {
        var cts = new CancellationTokenSource();
        if (!_activeSessions.TryAdd(jobId, cts))
        {
            cts.Dispose();
            throw new InvalidOperationException($"Transcription job {jobId} is already running.");
        }

        _ = Task.Run(() => RunSlidingWindowLoopAsync(
            jobId, modelId, deviceIdentifier, language,
            mode ?? TranscriptionMode.SlidingWindow,
            windowSeconds, stepSeconds,
            cts.Token));
    }

    /// <summary>
    /// Stops the capture loop for a job. The job's DB status is
    /// updated by the caller (<see cref="TranscriptionService"/>).
    /// </summary>
    public void Stop(Guid jobId)
    {
        if (_activeSessions.TryRemove(jobId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    public bool IsRunning(Guid jobId) => _activeSessions.ContainsKey(jobId);

    /// <inheritdoc/>
    public Task StopAllAsync()
    {
        foreach (var (jobId, cts) in _activeSessions)
        {
            if (_activeSessions.TryRemove(jobId, out _))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }

        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════
    // Sliding-window capture → inference → deduplicated push loop
    // ═══════════════════════════════════════════════════════════════

    private async Task RunSlidingWindowLoopAsync(
        Guid jobId, Guid modelId, string? deviceIdentifier,
        string? language, TranscriptionMode mode,
        int? windowSecondsOverride, int? stepSecondsOverride,
        CancellationToken ct)
    {
        logger.LogInformation(
            "Starting sliding-window transcription for job {JobId} on device '{Device}'",
            jobId, deviceIdentifier ?? "default");

        // Resolve model + provider once
        string apiKey = "";
        string modelName;
        ProviderType providerType;

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
            var model = await db.Models
                .Include(m => m.Provider)
                .FirstOrDefaultAsync(m => m.Id == modelId, ct)
                ?? throw new InvalidOperationException($"Model {modelId} not found.");

            providerType = model.Provider.ProviderType;

            if (!transcriptionClientFactory.Supports(providerType))
                throw new InvalidOperationException(
                    $"Provider '{model.Provider.Name}' ({providerType}) does not support transcription.");

            if (providerType == ProviderType.Whisper)
            {
                var localFile = await db.LocalModelFiles
                    .FirstOrDefaultAsync(f => f.ModelId == modelId, ct)
                    ?? throw new InvalidOperationException(
                        $"No local model file found for model {modelId}.");

                if (localFile.Status != LocalModelStatus.Ready)
                    throw new InvalidOperationException(
                        $"Local model file status is {localFile.Status}.");

                modelName = localFile.FilePath;
            }
            else
            {
                if (string.IsNullOrEmpty(model.Provider.EncryptedApiKey))
                    throw new InvalidOperationException("Provider does not have an API key configured.");

                apiKey = ApiKeyEncryptor.DecryptOrPassthrough(model.Provider.EncryptedApiKey, encryptionOptions.Key);
                modelName = model.Name;
            }
        }

        var sttClient = transcriptionClientFactory.GetClient(providerType);
        var ringBuffer = sharedCapture.Acquire(deviceIdentifier, SampleRate, BufferCapacitySeconds);
        var lastSeenEnd = 0.0;
        var provisionals = new List<ProvisionalSegment>();
        var consecutiveErrors = 0;
        const int maxConsecutiveErrors = 5;
        var previousWindowText = "";
        var emittedTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var effectiveLanguage = language;
        var tickCount = 0;

        var effectiveWindow = Clamp(windowSecondsOverride, 5, BufferCapacitySeconds, WindowSeconds);
        var effectiveStep = mode == TranscriptionMode.SlidingWindow
            ? Clamp(stepSecondsOverride, 1, effectiveWindow, InferenceIntervalSeconds)
            : effectiveWindow;
        var isTwoPass = mode == TranscriptionMode.SlidingWindow;
        var promptBuffer = isTwoPass && language is not null
            ? LanguageValidator.GetPromptSeed(language)
            : "";

        // Poll cadence is always short (2 s) so the loop reacts
        // quickly to new audio.  For StrictWindow (step == window),
        // inference only fires once a full window of new samples
        // has accumulated.
        var pollSeconds = InferenceIntervalSeconds;
        var samplesPerWindow = (long)effectiveWindow * SampleRate;
        var lastInferenceSample = ringBuffer.TotalWritten;

        logger.LogInformation(
            "Job {JobId}: mode={Mode}, window={Window}s, step={Step}s, poll={Poll}s",
            jobId, mode, effectiveWindow, effectiveStep, pollSeconds);

        try
        {
            // ── Startup verification: ensure audio is actually flowing ──
            // If the capture task faulted or the device never produces
            // data, detect it now rather than silently looping forever.
            const int startupTimeoutMs = 5000;
            const int startupPollMs = 100;
            var startupPolls = startupTimeoutMs / startupPollMs;

            for (var i = 0; i < startupPolls && ringBuffer.TotalWritten == 0; i++)
            {
                var (isHealthy, captureError) = sharedCapture.GetCaptureStatus(deviceIdentifier);
                if (!isHealthy)
                    throw new InvalidOperationException(
                        $"Audio capture failed to start: {captureError}");

                await Task.Delay(startupPollMs, ct);
            }

            if (ringBuffer.TotalWritten == 0)
            {
                var (_, statusError) = sharedCapture.GetCaptureStatus(deviceIdentifier);
                throw new InvalidOperationException(
                    "No audio data received after waiting for device to start. " +
                    (statusError ?? "Check that the audio device is connected and working."));
            }

            logger.LogInformation(
                "Job {JobId}: audio capture verified, {Samples} samples received.",
                jobId, ringBuffer.TotalWritten);

            // Track data flow for the in-loop watchdog.
            var lastDataWritten = ringBuffer.TotalWritten;
            var noDataTicks = 0;
            const int maxNoDataTicks = 10;

            var pollInterval = TimeSpan.FromSeconds(pollSeconds);
            // Fast-start for SlidingWindow only: fire the first
            // inference after 1 s so users hear feedback sooner.
            // The two-pass provisional→finalized lifecycle corrects
            // any inaccuracies from short initial audio clips.
            // Non-two-pass modes (StrictWindow) MUST wait
            // for a full window — each segment is emitted as final and
            // Whisper hallucinates on clips shorter than ~2 s,
            // poisoning dedup state, prompt buffer, and time cursors.
            var nextPollDelay = isTwoPass
                ? TimeSpan.FromSeconds(1)
                : ComputeAccumulationDelay(ringBuffer, lastInferenceSample, samplesPerWindow);
            var pipelineStart = Stopwatch.GetTimestamp();

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(nextPollDelay, ct);
                nextPollDelay = pollInterval;

                // ── Data-flow watchdog ────────────────────────────
                // Detect when the audio capture has stalled or the
                // device has stopped producing samples.  This must
                // run BEFORE the VAD check because an empty buffer
                // yields RMS=0 which looks like silence.
                var currentWritten = ringBuffer.TotalWritten;
                if (currentWritten == lastDataWritten)
                {
                    noDataTicks++;

                    var (isHealthy, captureError) = sharedCapture.GetCaptureStatus(deviceIdentifier);
                    if (!isHealthy)
                        throw new InvalidOperationException(
                            $"Audio capture failed during transcription: {captureError}");

                    if (noDataTicks >= maxNoDataTicks)
                    {
                        await AddJobLogAsync(jobId,
                            $"No new audio data for {noDataTicks * pollSeconds}s — capture may have stalled.",
                            "Error");
                        throw new InvalidOperationException(
                            $"No new audio data received for {noDataTicks * pollSeconds} seconds. " +
                            "Audio capture may have stalled or the device was disconnected.");
                    }

                    logger.LogDebug(
                        "Job {JobId}: no new audio data (tick {Tick}/{Max}), waiting.",
                        jobId, noDataTicks, maxNoDataTicks);
                    continue;
                }
                noDataTicks = 0;
                lastDataWritten = currentWritten;

                // ── Accumulation gate ─────────────────────────────
                // For modes where step == window, wait until a full
                // window of new audio has been captured before firing
                // inference.  SlidingWindow bypasses this gate (its
                // short step fires every poll tick) and uses a two-
                // pass lifecycle to correct early inaccuracies.
                // StrictWindow MUST accumulate a full window —
                // segments are emitted as final and short clips
                // cause Whisper hallucinations that poison the dedup
                // set, prompt buffer, and time cursors.
                if (!isTwoPass
                    && currentWritten - lastInferenceSample < samplesPerWindow)
                {
                    nextPollDelay = ComputeAccumulationDelay(
                        ringBuffer, lastInferenceSample, samplesPerWindow);
                    continue;
                }

                try
                {
                    tickCount++;

                    // ── Step-region silence check ────────────────────
                    // In sliding-window modes the step is shorter than
                    // the window, so most audio was already processed
                    // last tick.  When the NEW portion (last step
                    // seconds) is silent, re-transcribing the full
                    // window would only produce duplicate segments —
                    // skip the API call entirely.
                    //
                    // Skip this gate on tick 1: there is no "already
                    // processed" audio yet, so ALL captured audio is
                    // new.  Applying the step-region check here would
                    // act as a pure silence gate and delay the first
                    // inference unnecessarily in quiet rooms.
                    if (tickCount > 1 && effectiveStep < effectiveWindow)
                    {
                        var stepSamples = ringBuffer.GetLastSeconds(effectiveStep);
                        if (stepSamples.Length > 0 && ComputeRms(stepSamples) < 0.005)
                        {
                            logger.LogTrace(
                                "Job {JobId}: [tick {Tick}] step-region silence (RMS<0.005), skipping inference",
                                jobId, tickCount);
                            continue;
                        }
                    }

                    var windowSamples = ringBuffer.GetLastSeconds(effectiveWindow);
                    if (windowSamples.Length == 0)
                        continue;

                    var rms = ComputeRms(windowSamples);
                    if (rms < 0.005)
                    {
                        // Advance the accumulation cursor so non-two-pass
                        // modes don't re-check the same silent window
                        // every poll tick.
                        lastInferenceSample = currentWritten;
                        if (!isTwoPass)
                            nextPollDelay = ComputeAccumulationDelay(
                                ringBuffer, lastInferenceSample, samplesPerWindow);
                        logger.LogTrace(
                            "Job {JobId}: [tick {Tick}] silence gate: RMS={Rms}, skipping",
                            jobId, tickCount, rms);
                        continue;
                    }

                    var windowStartTime = ringBuffer.GetWindowStartTime(effectiveWindow);
                    var wavBytes = FloatSamplesToWav(windowSamples, SampleRate);
                    var currentAbsTime = (double)ringBuffer.TotalWritten / SampleRate;

                    logger.LogTrace(
                        "Job {JobId}: [tick {Tick}] window=[{Start}, {End}] RMS={Rms} absTime={AbsTime} lastSeenEnd={LastSeen} provisionals={Provs}",
                        jobId, tickCount, windowStartTime, windowStartTime + effectiveWindow,
                        rms, currentAbsTime, lastSeenEnd, provisionals.Count);

                    using var httpClient = httpClientFactory.CreateClient();

                    var inferenceStart = Stopwatch.GetTimestamp();

                    var result = await sttClient.TranscribeAsync(
                        httpClient, apiKey, modelName, wavBytes,
                        effectiveLanguage, isTwoPass ? promptBuffer : null, ct);

                    consecutiveErrors = 0;
                    lastInferenceSample = currentWritten;

                    // Compensate next poll delay for time spent in
                    // inference.  For non-two-pass modes, compute a
                    // precise delay from the sample deficit so the next
                    // inference fires as soon as a full window
                    // accumulates rather than on a fixed cadence.
                    var inferenceElapsed = Stopwatch.GetElapsedTime(inferenceStart);
                    if (!isTwoPass)
                    {
                        nextPollDelay = ComputeAccumulationDelay(
                            ringBuffer, lastInferenceSample, samplesPerWindow);
                    }
                    else
                    {
                        var compensated = pollInterval - inferenceElapsed;
                        nextPollDelay = compensated > TimeSpan.FromMilliseconds(100)
                            ? compensated
                            : TimeSpan.Zero;
                    }

                    if (effectiveLanguage is null
                        && !string.IsNullOrWhiteSpace(result.Language))
                        effectiveLanguage = result.Language;

                    if (result is null || string.IsNullOrWhiteSpace(result.Text))
                    {
                        logger.LogTrace(
                            "Job {JobId}: [tick {Tick}] API returned empty result, skipping",
                            jobId, tickCount);
                        continue;
                    }

                    IReadOnlyList<TranscriptionChunkSegment> segments = result.Segments;

                    logger.LogTrace(
                        "Job {JobId}: [tick {Tick}] API returned {Count} segment(s), hasTimestamps={HasTs}, duration={Dur}, lang={Lang}",
                        jobId, tickCount, segments.Count, result.HasTimestampedSegments,
                        result.Duration, result.Language ?? "null");

                    for (var si = 0; si < segments.Count; si++)
                    {
                        var s = segments[si];
                        logger.LogTrace(
                            "Job {JobId}: [tick {Tick}]   raw seg[{Idx}]: [{Start},{End}] conf={Conf} \"{Text}\"",
                            jobId, tickCount, si, s.Start, s.End,
                            s.Confidence?.ToString("F3") ?? "null", Truncate(s.Text, 80));
                    }

                    if (!result.HasTimestampedSegments)
                    {
                        var currentText = result.Text.Trim();
                        var newText = RemoveOverlap(previousWindowText, currentText);

                        logger.LogTrace(
                            "Job {JobId}: [tick {Tick}] synthetic diff: prev={PrevLen} chars, curr={CurrLen} chars, new=\"{New}\"",
                            jobId, tickCount, previousWindowText.Length, currentText.Length, Truncate(newText, 80));

                        // When the overlap check found nothing new, only
                        // upgrade previousWindowText to a LONGER response —
                        // downgrading to a shorter subset loses context and
                        // lets already-emitted content slip through as "new"
                        // in a later tick once the shorter prev no longer
                        // contains it.  When there IS new text, always
                        // update so the diff tracks the sliding window.
                        if (string.IsNullOrWhiteSpace(newText))
                        {
                            if (currentText.Length > previousWindowText.Length)
                                previousWindowText = currentText;
                            continue;
                        }
                        previousWindowText = currentText;

                        // Very short residuals starting with a lowercase letter
                        // are almost certainly sentence-completion fragments from
                        // API text truncation at the window boundary (e.g. prev
                        // ended "the fifth", curr completed to "the fifth sentence.").
                        // Merge into the most recent provisional rather than
                        // emitting a standalone fragment segment.
                        var newWords = newText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (newWords.Length <= 2 && newWords.Length > 0
                            && char.IsLower(newWords[0][0])
                            && isTwoPass && provisionals.Count > 0)
                        {
                            var last = provisionals[^1];
                            var mergedText = last.Text + " " + newText;

                            await AddJobLogAsync(jobId,
                                $"[tick {tickCount}] MERGE fragment \"{Truncate(newText, 40)}\" " +
                                $"into provisional: \"{Truncate(mergedText, 60)}\"", "Info");

                            using (var mergeScope = scopeFactory.CreateScope())
                            {
                                var mergeSvc = mergeScope.ServiceProvider.GetRequiredService<AgentJobService>();
                                await mergeSvc.UpdateProvisionalTextAsync(
                                    jobId, last.SegmentId, mergedText, ct);
                            }
                            provisionals[^1] = last with { Text = mergedText };
                            emittedTexts.Add(mergedText.Trim().TrimEnd('.'));
                            continue;
                        }

                        // Split accumulated text into sentence-level segments
                        // so output arrives incrementally rather than in bursts
                        // when multiple sentences become "new" in a single tick.
                        var sentences = SplitSentences(newText);
                        var absSpanStart = Math.Max(lastSeenEnd, windowStartTime);
                        var absSpanEnd = windowStartTime + result.Duration;
                        var spanDuration = absSpanEnd - absSpanStart;

                        if (sentences.Count > 1 && spanDuration > 0)
                        {
                            var totalChars = 0;
                            foreach (var s in sentences) totalChars += s.Length;

                            var segList = new List<TranscriptionChunkSegment>(sentences.Count);
                            var runningAbs = absSpanStart;
                            foreach (var sentence in sentences)
                            {
                                var frac = (double)sentence.Length / totalChars;
                                var segDur = spanDuration * frac;
                                var relStart = runningAbs - windowStartTime;
                                var relEnd = relStart + segDur;
                                segList.Add(new TranscriptionChunkSegment(
                                    sentence, relStart, relEnd, null));
                                runningAbs += segDur;
                            }
                            segments = segList;
                        }
                        else
                        {
                            var newStart = Math.Max(0, result.Duration - effectiveStep);
                            segments = [new TranscriptionChunkSegment(
                                newText, newStart, result.Duration, null)];
                        }
                    }

                    using var scope = scopeFactory.CreateScope();
                    var svc = scope.ServiceProvider.GetRequiredService<AgentJobService>();

                    foreach (var seg in segments)
                    {
                        var absStart = windowStartTime + seg.Start;
                        var absEnd = windowStartTime + seg.End;

                        if (absEnd <= lastSeenEnd)
                        {
                            logger.LogTrace(
                                "Job {JobId}: [tick {Tick}] SKIP (absEnd {AbsEnd} <= lastSeenEnd {LastSeen}): \"{Text}\"",
                                jobId, tickCount, absEnd, lastSeenEnd, Truncate(seg.Text, 60));
                            continue;
                        }

                        var normalizedText = seg.Text.Trim().TrimEnd('.');
                        if (!emittedTexts.Add(normalizedText))
                        {
                            logger.LogTrace(
                                "Job {JobId}: [tick {Tick}] SKIP duplicate text: \"{Text}\"",
                                jobId, tickCount, Truncate(seg.Text, 60));
                            continue;
                        }

                        if (!isTwoPass)
                        {
                            await AddJobLogAsync(jobId,
                                $"[tick {tickCount}] EMIT final [{absStart:F2},{absEnd:F2}]: " +
                                $"\"{Truncate(seg.Text, 60)}\"", "Info");
                            await svc.PushSegmentAsync(
                                jobId, seg.Text, absStart, absEnd, seg.Confidence, ct: ct);
                        }
                        else if (absEnd <= currentAbsTime - CommitDelaySeconds)
                        {
                            await AddJobLogAsync(jobId,
                                $"[tick {tickCount}] EMIT confirmed [{absStart:F2},{absEnd:F2}] " +
                                $"(absEnd <= {currentAbsTime - CommitDelaySeconds:F2}): " +
                                $"\"{Truncate(seg.Text, 60)}\"", "Info");
                            await svc.PushSegmentAsync(
                                jobId, seg.Text, absStart, absEnd, seg.Confidence, ct: ct);
                        }
                        else
                        {
                            await AddJobLogAsync(jobId,
                                $"[tick {tickCount}] EMIT provisional [{absStart:F2},{absEnd:F2}] " +
                                $"(absEnd > {currentAbsTime - CommitDelaySeconds:F2}): " +
                                $"\"{Truncate(seg.Text, 60)}\"", "Info");
                            var prov = await svc.PushSegmentAsync(
                                jobId, seg.Text, absStart, absEnd, seg.Confidence,
                                isProvisional: true, ct: ct);

                            if (prov is not null)
                                provisionals.Add(new ProvisionalSegment(
                                    prov.Id, seg.Text, absEnd));
                        }

                        if (lastSeenEnd == 0.0)
                        {
                            var firstSegElapsed = Stopwatch.GetElapsedTime(pipelineStart);
                            logger.LogInformation(
                                "Job {JobId}: first segment emitted {Elapsed}ms after pipeline start",
                                jobId, (long)firstSegElapsed.TotalMilliseconds);
                        }
                        logger.LogTrace(
                            "Job {JobId}: [tick {Tick}] cursor: lastSeenEnd {From} -> {To}",
                            jobId, tickCount, lastSeenEnd, absEnd);
                        lastSeenEnd = absEnd;
                        if (isTwoPass)
                            AppendPrompt(ref promptBuffer, seg.Text);
                    }

                    if (isTwoPass)
                    {
                        var staleThreshold = currentAbsTime - CommitDelaySeconds * 2;
                        for (var i = provisionals.Count - 1; i >= 0; i--)
                        {
                            if (provisionals[i].AbsEnd < staleThreshold)
                            {
                                var stale = provisionals[i];
                                await AddJobLogAsync(jobId,
                                    $"[tick {tickCount}] FINALIZE stale provisional " +
                                    $"(absEnd {stale.AbsEnd:F2} < threshold {staleThreshold:F2}): " +
                                    $"\"{Truncate(stale.Text, 60)}\"", "Info");
                                await svc.FinalizeSegmentAsync(
                                    jobId, stale.SegmentId, stale.Text, ct: ct);
                                provisionals.RemoveAt(i);
                            }
                        }

                        // Refine the poll delay to account for total tick
                        // processing (dedup + emission + finalization) that
                        // the post-inference estimate could not predict.
                        var totalElapsed = Stopwatch.GetElapsedTime(inferenceStart);
                        var refined = pollInterval - totalElapsed;
                        nextPollDelay = refined > TimeSpan.FromMilliseconds(50)
                            ? refined
                            : TimeSpan.Zero;
                    }
                }
                catch (HttpRequestException ex) when (
                    !ct.IsCancellationRequested &&
                    ex.StatusCode.HasValue &&
                    (int)ex.StatusCode.Value >= 400 &&
                    (int)ex.StatusCode.Value < 500)
                {
                    logger.LogError(ex,
                        "Transcription inference got non-retryable HTTP {StatusCode} for job {JobId}",
                        (int)ex.StatusCode.Value, jobId);

                    await AddJobLogAsync(jobId,
                        $"Fatal: {ex.Message}", "Error");

                    throw new InvalidOperationException(
                        $"Non-retryable API error: {ex.Message}", ex);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    consecutiveErrors++;
                    logger.LogWarning(ex,
                        "Transcription inference failed for job {JobId} ({Consecutive}/{Max})",
                        jobId, consecutiveErrors, maxConsecutiveErrors);

                    await AddJobLogAsync(jobId,
                        $"Inference failed: {ex.Message}" +
                        $" ({consecutiveErrors}/{maxConsecutiveErrors} consecutive)",
                        "Warning");

                    if (consecutiveErrors >= maxConsecutiveErrors)
                        throw new InvalidOperationException(
                            $"Aborting: {maxConsecutiveErrors} consecutive inference failures. Last error: {ex.Message}",
                            ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Transcription job {JobId} cancelled.", jobId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Transcription job {JobId} failed.", jobId);

            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
                var job = await db.AgentJobs
                    .Include(j => j.LogEntries)
                    .FirstOrDefaultAsync(j => j.Id == jobId);
                if (job is not null && job.Status == AgentJobStatus.Executing)
                {
                    job.Status = AgentJobStatus.Failed;
                    job.ErrorLog = ex.ToString();
                    job.CompletedAt = DateTimeOffset.UtcNow;
                    job.LogEntries.Add(new AgentJobLogEntryDB
                    {
                        AgentJobId = job.Id,
                        Message = $"Transcription failed: {ex.Message}",
                        Level = "Error",
                    });
                    await db.SaveChangesAsync();
                }
            }
            catch (Exception dbEx)
            {
                logger.LogError(dbEx, "Failed to update job {JobId} status to Failed.", jobId);
            }
        }
        finally
        {
            await sharedCapture.ReleaseAsync(deviceIdentifier);

            if (_activeSessions.TryRemove(jobId, out var cts))
            {
                await cts.CancelAsync();
                cts.Dispose();
            }
        }
    }

    private readonly record struct ProvisionalSegment(
        Guid SegmentId, string Text, double AbsEnd);

    private static string RemoveOverlap(string previous, string current)
    {
        if (string.IsNullOrWhiteSpace(previous))
            return current;

        var prevWords = previous.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var currWords = current.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Check if current is entirely contained within previous
        // (API returned a subset of previously seen text).
        // Allow up to 10% word mismatches (floor) for Whisper
        // hallucination tolerance; short sequences (< 10 words) require
        // exact match so structurally similar but distinct sentences
        // (e.g. "seventh" → "eighth") are not suppressed.
        if (currWords.Length <= prevWords.Length)
        {
            var maxMismatches = (int)Math.Floor(currWords.Length * 0.1);
            for (var start = 0; start <= prevWords.Length - currWords.Length; start++)
            {
                var mismatches = 0;
                var contained = true;
                for (var j = 0; j < currWords.Length; j++)
                {
                    if (!WordEquals(prevWords[start + j], currWords[j]))
                    {
                        mismatches++;
                        if (mismatches > maxMismatches)
                        {
                            contained = false;
                            break;
                        }
                    }
                }
                if (contained)
                    return "";
            }
        }

        // Find longest suffix of prev matching prefix of curr.
        var maxOverlap = Math.Min(prevWords.Length, currWords.Length);
        for (var k = maxOverlap; k >= 1; k--)
        {
            var match = true;
            for (var j = 0; j < k; j++)
            {
                if (!WordEquals(prevWords[prevWords.Length - k + j], currWords[j]))
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return k >= currWords.Length
                    ? ""
                    : string.Join(' ', currWords[k..]);
        }
        return current;
    }

    private static List<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length - 2; i++)
        {
            if (text[i] is '.' or '!' or '?'
                && char.IsWhiteSpace(text[i + 1])
                && char.IsUpper(text[i + 2]))
            {
                sentences.Add(text[start..(i + 1)].Trim());
                start = i + 2;
            }
        }
        var remaining = text[start..].Trim();
        if (remaining.Length > 0)
            sentences.Add(remaining);
        return sentences;
    }

    private static bool WordEquals(string a, string b) =>
        string.Equals(
            a.TrimEnd('.', ',', '!', '?', ';', ':'),
            b.TrimEnd('.', ',', '!', '?', ';', ':'),
            StringComparison.OrdinalIgnoreCase);

    private static void AppendPrompt(ref string promptBuffer, string text)
    {
        promptBuffer = (promptBuffer + " " + text).Trim();
        if (promptBuffer.Length > MaxPromptChars)
            promptBuffer = promptBuffer[^MaxPromptChars..];
    }

    private static byte[] FloatSamplesToWav(float[] samples, int sampleRate)
    {
        var format = new WaveFormat(sampleRate, 16, 1);
        using var ms = new MemoryStream();
        using (var writer = new WaveFileWriter(ms, format))
        {
            writer.WriteSamples(samples, 0, samples.Length);
            writer.Flush();
        }
        return ms.ToArray();
    }

    private async Task AddJobLogAsync(Guid jobId, string message, string level = "Info")
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
            db.AgentJobLogEntries.Add(new AgentJobLogEntryDB
            {
                AgentJobId = jobId,
                Message = message,
                Level = level,
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write log entry for job {JobId}: {Message}", jobId, message);
        }
    }

    private static TimeSpan ComputeAccumulationDelay(
        AudioRingBuffer ringBuffer, long lastInferenceSample, long samplesPerWindow)
    {
        var deficit = samplesPerWindow - (ringBuffer.TotalWritten - lastInferenceSample);
        return deficit > 0
            ? TimeSpan.FromSeconds(Math.Max((double)deficit / SampleRate, 0.05))
            : TimeSpan.FromMilliseconds(50);
    }

    private static int Clamp(int? value, int min, int max, int defaultValue) =>
        value.HasValue ? Math.Clamp(value.Value, min, max) : defaultValue;

    private static string Truncate(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..maxLen] + "…";

    private static double ComputeRms(float[] samples)
    {
        if (samples.Length == 0)
            return 0;

        double sum = 0;
        foreach (var s in samples)
            sum += s * (double)s;

        return Math.Sqrt(sum / samples.Length);
    }
}
