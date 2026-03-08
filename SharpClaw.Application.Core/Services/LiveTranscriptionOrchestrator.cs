using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using SharpClaw.Application.Core.Clients;
using SharpClaw.Application.Infrastructure.Models.Jobs;
using SharpClaw.Contracts.Enums;
using SharpClaw.Infrastructure.Models;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Utils.Security;

namespace SharpClaw.Application.Services;

/// <summary>
/// Singleton service that manages active audio-capture→STT→push loops.
/// <para>
/// Audio flows through a sliding-window pipeline:
/// <c>mic → ring buffer → VAD silence filter → sliding inference window
/// → Whisper → no_speech / compression filter → timestamp dedup → output</c>
/// </para>
/// <para>
/// The ring buffer holds ~30 s of mono 16 kHz float PCM. Every
/// <see cref="InferenceInterval"/> seconds the orchestrator checks for
/// speech via a simple RMS-based VAD; when speech is detected it
/// extracts the last <see cref="WindowSeconds"/> seconds, converts them
/// to a WAV chunk, and sends that to the transcription API.
/// </para>
/// <para>
/// Whisper returns timestamped segments relative to the window start.
/// The orchestrator converts them to absolute stream time and only
/// emits segments whose end time exceeds the last emitted timestamp.
/// Segments flagged as likely silence (<c>no_speech_prob</c> above
/// threshold) or as hallucinated text (<c>compression_ratio</c> above
/// threshold) are discarded.  A commit delay further stabilises
/// recently decoded segments.
/// </para>
/// </summary>
public sealed class LiveTranscriptionOrchestrator(
    IServiceScopeFactory scopeFactory,
    SharedAudioCaptureManager sharedCapture,
    TranscriptionApiClientFactory transcriptionClientFactory,
    IHttpClientFactory httpClientFactory,
    EncryptionOptions encryptionOptions,
    ILogger<LiveTranscriptionOrchestrator> logger)
{
    // ── Sliding-window parameters ─────────────────────────────────

    /// <summary>Seconds of audio sent to Whisper each inference tick.</summary>
    private const int WindowSeconds = 25;

    /// <summary>How often (seconds) the inference loop runs.</summary>
    private const int InferenceIntervalSeconds = 3;

    /// <summary>Ring buffer capacity in seconds.</summary>
    private const int BufferCapacitySeconds = 30;

    /// <summary>
    /// Only commit segments whose absolute end time is at least this many
    /// seconds in the past. Recent segments are unstable across successive
    /// inference runs — delaying commit avoids flickering corrections.
    /// </summary>
    private const double CommitDelaySeconds = 5.0;

    // ── Silence / hallucination thresholds ────────────────────────

    /// <summary>
    /// Whisper's <c>no_speech_prob</c> above this value → segment is
    /// likely silence and is discarded.
    /// </summary>
    private const double NoSpeechProbThreshold = 0.6;

    /// <summary>
    /// Whisper's <c>compression_ratio</c> above this value → segment is
    /// likely hallucinated / repetitive text and is discarded.
    /// </summary>
    private const double CompressionRatioThreshold = 2.4;

    /// <summary>
    /// Whisper's <c>avg_logprob</c> below this value → segment has
    /// very low confidence and is discarded.
    /// </summary>
    private const double LogProbThreshold = -1.0;

    // ── Deduplication parameters ─────────────────────────────────

    /// <summary>
    /// Whisper can shift segment boundaries by 100–300 ms between
    /// overlapping inference windows.  Segments whose absolute end time
    /// falls within this tolerance of the last emitted timestamp are
    /// treated as already-emitted.
    /// <para>
    /// Must be smaller than the shortest expected real segment (~0.5 s)
    /// to avoid swallowing back-to-back speech.
    /// </para>
    /// </summary>
    private const double TimestampToleranceSeconds = 0.3;

    /// <summary>
    /// Maximum number of recently emitted segments kept for
    /// overlap-based deduplication.  Only the most recent entries are
    /// retained; older ones are evicted FIFO.
    /// </summary>
    private const int MaxRecentSegments = 10;

    /// <summary>
    /// Minimum time-overlap ratio (relative to the shorter segment)
    /// required to consider two segments as covering the same audio.
    /// Combined with a text-containment check to confirm the match.
    /// </summary>
    private const double DuplicateOverlapThreshold = 0.5;

    // ── Hallucination guard ──────────────────────────────────────

    /// <summary>
    /// Segments shorter than this (in seconds) with text longer than
    /// <see cref="HallucinationTextFloor"/> characters are almost
    /// certainly hallucinated — Whisper inventing plausible sentences
    /// for noise or micro-pauses.
    /// </summary>
    private const double HallucinationDurationCeiling = 0.5;

    /// <summary>
    /// Text length above which a sub-<see cref="HallucinationDurationCeiling"/>
    /// segment is flagged as hallucinated.  Short text in a short
    /// segment is normal (e.g. "OK", "yes").
    /// </summary>
    private const int HallucinationTextFloor = 15;

    // ── Prompt conditioning ─────────────────────────────────

    /// <summary>
    /// Maximum number of characters from previously finalized text sent
    /// as the Whisper <c>prompt</c> parameter.  Whisper tokenises the
    /// prompt internally and truncates to ~224 tokens; 500 chars
    /// comfortably fits within that limit for English.
    /// </summary>
    private const int MaxPromptChars = 500;

    /// <summary>
    /// Minimum confidence (exp(avg_logprob)) a segment must have to be
    /// emitted as a provisional in two-pass mode.  Segments below this
    /// threshold are held back until the commit delay confirms them,
    /// avoiding noisy provisional churn.
    /// </summary>
    private const double ProvisionalConfidenceFloor = 0.4;

    /// <summary>
    /// Maximum number of times the orchestrator will re-call
    /// <see cref="ITranscriptionApiClient.TranscribeAsync"/> with an
    /// increasingly reinforced prompt when the result comes back in the
    /// wrong language.  Escalation levels:
    /// <list type="number">
    ///   <item><description>Single seed phrase</description></item>
    ///   <item><description>Triple-repeated seed</description></item>
    ///   <item><description>Instruction preamble + double seed</description></item>
    ///   <item><description>Maximum reinforcement block</description></item>
    /// </list>
    /// If all retries are exhausted the result is accepted anyway so
    /// that no audio is ever silently dropped.
    /// </summary>
    private const int MaxLanguageRetries = 4;

    // ── Adaptive VAD ──────────────────────────────────────

    /// <summary>
    /// Multiplier applied to the running noise-floor RMS to compute the
    /// adaptive silence threshold.  A segment is considered speech only
    /// when its RMS exceeds <c>noiseFloor × multiplier</c>.
    /// </summary>
    private const float AdaptiveVadMultiplier = 3.0f;

    /// <summary>
    /// Exponential moving-average decay factor for the noise-floor
    /// estimate.  Lower values make the noise floor follow ambient
    /// changes more slowly (more stable).  0.05 = ~20-tick time
    /// constant.
    /// </summary>
    private const float NoiseFloorAlpha = 0.05f;

    /// <summary>Audio sample rate used throughout the pipeline.</summary>
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

            if (providerType == ProviderType.Local)
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

                apiKey = ApiKeyEncryptor.Decrypt(model.Provider.EncryptedApiKey, encryptionOptions.Key);
                modelName = model.Name;
            }
        }

        var sttClient = transcriptionClientFactory.GetClient(providerType);
        var ringBuffer = sharedCapture.Acquire(deviceIdentifier, SampleRate, BufferCapacitySeconds);
        var lastEmittedTimestamp = 0.0;
        var recentSegments = new List<EmittedSegment>(MaxRecentSegments);
        var provisionalSegments = new List<ProvisionalSegment>();
        var consecutiveErrors = 0;
        const int maxConsecutiveErrors = 5;

        // Text-diff dedup for synthetic segments: tracks the full text
        // returned by the previous API call so we can extract only the
        // genuinely new portion when the model doesn't return per-segment
        // timestamps (e.g. gpt-4o-transcribe in json mode).
        var previousWindowText = "";

        // Prompt conditioning: the last N characters of finalized text
        // are sent to Whisper as a style/vocabulary hint.
        // When a language is explicitly set, seed with a target-language
        // phrase so the very first inference tick is already anchored.
        var promptBuffer = language is not null
            ? LanguageScriptValidator.GetPromptSeed(language)
            : "";

        // Language feedback: when the caller didn't specify a language,
        // the first successful Whisper response tells us what it detected.
        // We lock that in for all subsequent calls so short chunks
        // don't trigger noisy per-tick language detection.
        var effectiveLanguage = language;

        // True when the caller explicitly provided a language code.
        // When explicit, enforcement is strict: result-level language
        // mismatch and segment-level script mismatch both cause
        // discards. Auto-detected language is also enforced once locked.
        var languageIsLocked = language is not null;

        // Adaptive VAD: running noise-floor estimate updated during
        // silence ticks.  Initialised to the fixed default; adapted
        // after the first few ticks.
        var noiseFloor = AudioVad.DefaultSilenceThreshold;

        // Resolve effective window/step from overrides or defaults.
        var effectiveWindow = Clamp(windowSecondsOverride, 5, BufferCapacitySeconds, WindowSeconds);
        var effectiveStep = mode == TranscriptionMode.Simple
            ? effectiveWindow   // no overlap in simple mode
            : Clamp(stepSecondsOverride, 1, effectiveWindow, InferenceIntervalSeconds);
        var isSimple = mode == TranscriptionMode.Simple;
        var isTwoPass = mode == TranscriptionMode.SlidingWindow;

        logger.LogInformation(
            "Job {JobId}: mode={Mode}, window={Window}s, step={Step}s",
            jobId, mode, effectiveWindow, effectiveStep);

        try
        {
            // Give the capture a moment to start filling the buffer
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);

            // ── Startup verification: ensure audio is actually flowing ──
            // If the capture task faulted or the device never produces
            // data, detect it now rather than silently looping forever.
            const int startupTimeoutMs = 5000;
            const int startupPollMs = 250;
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

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(effectiveStep), ct);

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
                            $"No new audio data for {noDataTicks * effectiveStep}s — capture may have stalled.",
                            "Error");
                        throw new InvalidOperationException(
                            $"No new audio data received for {noDataTicks * effectiveStep} seconds. " +
                            "Audio capture may have stalled or the device was disconnected.");
                    }

                    logger.LogDebug(
                        "Job {JobId}: no new audio data (tick {Tick}/{Max}), waiting.",
                        jobId, noDataTicks, maxNoDataTicks);
                    continue;
                }
                noDataTicks = 0;
                lastDataWritten = currentWritten;

                // Adaptive VAD: compute the RMS of the recent step
                // and update the noise floor when it looks like silence.
                var recentSamples = ringBuffer.GetLastSeconds(effectiveStep);
                var recentRms = ComputeRms(recentSamples);
                var adaptiveThreshold = Math.Max(
                    AudioVad.DefaultSilenceThreshold,
                    noiseFloor * AdaptiveVadMultiplier);

                if (recentRms < adaptiveThreshold)
                {
                    // Update noise floor with exponential moving average
                    noiseFloor = noiseFloor * (1 - NoiseFloorAlpha)
                        + (float)recentRms * NoiseFloorAlpha;
                    logger.LogDebug(
                        "Job {JobId}: silence detected (rms={Rms:F5}, floor={Floor:F5}), skipping.",
                        jobId, recentRms, noiseFloor);

                    // ── Finalize stale provisionals during silence ──
                    // The inference loop is skipped during silence, but
                    // provisional segments emitted in earlier speech ticks
                    // must still be promoted once they exceed the staleness
                    // threshold — otherwise they remain in limbo and the
                    // client never sees the final text.
                    if (isTwoPass && provisionalSegments.Count > 0)
                    {
                        var silenceAbsTime = (double)ringBuffer.TotalWritten / SampleRate;
                        var staleThreshold = silenceAbsTime - CommitDelaySeconds * 2;
                        var hasStale = provisionalSegments.Exists(
                            p => p.AbsEnd < staleThreshold);

                        if (hasStale)
                        {
                            using var scope = scopeFactory.CreateScope();
                            var svc = scope.ServiceProvider.GetRequiredService<AgentJobService>();
                            for (var i = provisionalSegments.Count - 1; i >= 0; i--)
                            {
                                if (provisionalSegments[i].AbsEnd < staleThreshold)
                                {
                                    var stale = provisionalSegments[i];
                                    logger.LogDebug(
                                        "Job {JobId}: finalizing stale provisional during silence: {Text}",
                                        jobId, stale.Text);
                                    await svc.FinalizeSegmentAsync(
                                        jobId, stale.SegmentId, stale.Text, ct: ct);
                                    lastEmittedTimestamp = Math.Max(lastEmittedTimestamp, stale.AbsEnd);
                                    TrackEmittedSegment(recentSegments, stale.Text, stale.AbsStart, stale.AbsEnd);

                                    promptBuffer = (promptBuffer + " " + stale.Text).Trim();
                                    if (promptBuffer.Length > MaxPromptChars)
                                        promptBuffer = promptBuffer[^MaxPromptChars..];

                                    provisionalSegments.RemoveAt(i);
                                }
                            }
                        }
                    }

                    continue;
                }

                try
                {
                    // Extract the sliding window and convert to WAV for the API.
                    // Inside the try block so a transient NAudio error is counted
                    // as a consecutive error instead of killing the loop.
                    var windowSamples = ringBuffer.GetLastSeconds(effectiveWindow);
                    if (windowSamples.Length == 0)
                        continue;

                    var windowStartTime = ringBuffer.GetWindowStartTime(effectiveWindow);
                    var wavBytes = FloatSamplesToWav(windowSamples, SampleRate);

                    using var httpClient = httpClientFactory.CreateClient();

                    // ── Language-enforced transcription with retry ──
                    // Whisper's `language` param is a hint, not strict.
                    // When a language is locked we retry with an
                    // increasingly reinforced prompt until Whisper
                    // produces the correct language.
                    TranscriptionChunkResult? result = null;
                    var tickPrompt = promptBuffer;

                    for (var langRetry = 0; langRetry <= MaxLanguageRetries; langRetry++)
                    {
                        result = await sttClient.TranscribeAsync(
                            httpClient, apiKey, modelName, wavBytes,
                            effectiveLanguage, tickPrompt, ct);

                        consecutiveErrors = 0;

                        // Language feedback: lock in the detected language
                        if (effectiveLanguage is null
                            && !string.IsNullOrWhiteSpace(result.Language))
                        {
                            effectiveLanguage = result.Language;
                            languageIsLocked = true;
                            logger.LogInformation(
                                "Job {JobId}: detected language '{Lang}', locking in.",
                                jobId, effectiveLanguage);
                        }

                        // If no enforcement needed, accept the result
                        if (effectiveLanguage is null || !languageIsLocked)
                            break;

                        // Check if Whisper reported the correct language
                        if (LanguageScriptValidator.ResponseLanguageMatches(
                                effectiveLanguage, result.Language))
                            break;

                        // Also accept when the API didn't return a
                        // language field — we can't validate at this
                        // level but the per-segment script check will
                        // catch individual bad segments downstream.
                        if (string.IsNullOrWhiteSpace(result.Language))
                            break;

                        // Language mismatch — retry with reinforced prompt
                        if (langRetry < MaxLanguageRetries)
                        {
                            tickPrompt = LanguageScriptValidator.GetReinforcedPrompt(
                                effectiveLanguage, promptBuffer, langRetry + 1);
                            logger.LogDebug(
                                "Job {JobId}: language mismatch (expected='{Expected}', got='{Got}'), " +
                                "retrying with level-{Level} reinforcement ({Attempt}/{Max})",
                                jobId, effectiveLanguage, result.Language,
                                langRetry + 1, langRetry + 1, MaxLanguageRetries);
                            continue;
                        }

                        // All retries exhausted — accept anyway so no
                        // audio is silently dropped.  The per-segment
                        // script filter downstream will still catch
                        // individual wrong-script segments.
                        logger.LogWarning(
                            "Job {JobId}: accepting wrong-language result after {Max} retries " +
                            "(expected='{Expected}', got='{Got}')",
                            jobId, MaxLanguageRetries, effectiveLanguage, result.Language);
                        break;
                    }

                    if (result is null || string.IsNullOrWhiteSpace(result.Text))
                        continue;

                    // ── Synthetic segment text-diff ───────────────────
                    // Models like gpt-4o-transcribe return only text
                    // without per-segment timestamps.  The synthetic
                    // fallback segment covers the full window, which
                    // breaks overlap-based dedup because consecutive
                    // 25 s windows share ~88 % of audio.  Instead we
                    // diff the text against the previous window and
                    // emit only the genuinely new portion with
                    // timestamps scoped to the last step.
                    IReadOnlyList<TranscriptionChunkSegment> processSegments = result.Segments;

                    if (!result.HasTimestampedSegments)
                    {
                        var currentText = result.Text.Trim();
                        var newContent = ExtractNewContent(previousWindowText, currentText);
                        previousWindowText = currentText;

                        if (string.IsNullOrWhiteSpace(newContent))
                        {
                            logger.LogDebug(
                                "Job {JobId}: no new content in synthetic segment, skipping.",
                                jobId);
                            continue;
                        }

                        // Place the new content in the last step's time
                        // range so absolute timestamps don't overlap with
                        // the previously emitted segment.
                        var newStart = Math.Max(0, result.Duration - effectiveStep);
                        processSegments = [new TranscriptionChunkSegment(
                            newContent, newStart, result.Duration, null)];
                    }

                    // Current absolute time for commit-delay filtering
                    var currentAbsTime = (double)ringBuffer.TotalWritten / SampleRate;

                    using var scope = scopeFactory.CreateScope();
                    var svc = scope.ServiceProvider.GetRequiredService<AgentJobService>();

                    foreach (var seg in processSegments)
                    {
                        // ── API-level quality filters (cheapest) ──
                        if (seg.NoSpeechProbability.HasValue
                            && seg.NoSpeechProbability.Value > NoSpeechProbThreshold)
                        {
                            logger.LogDebug(
                                "Job {JobId}: discarding segment (no_speech_prob={Prob:F3}): {Text}",
                                jobId, seg.NoSpeechProbability.Value, seg.Text);
                            continue;
                        }

                        if (seg.CompressionRatio.HasValue
                            && seg.CompressionRatio.Value > CompressionRatioThreshold)
                        {
                            logger.LogDebug(
                                "Job {JobId}: discarding segment (compression_ratio={Ratio:F2}): {Text}",
                                jobId, seg.CompressionRatio.Value, seg.Text);
                            continue;
                        }

                        if (seg.Confidence.HasValue
                            && Math.Log(seg.Confidence.Value) < LogProbThreshold)
                        {
                            logger.LogDebug(
                                "Job {JobId}: discarding segment (logprob={LogProb:F2}): {Text}",
                                jobId, Math.Log(seg.Confidence.Value), seg.Text);
                            continue;
                        }

                        // ── Short-duration hallucination guard ────
                        var segDuration = seg.End - seg.Start;
                        if (segDuration < HallucinationDurationCeiling
                            && seg.Text.Length > HallucinationTextFloor)
                        {
                            logger.LogDebug(
                                "Job {JobId}: discarding segment (duration={Dur:F2}s, text={Len} chars — likely hallucination): {Text}",
                                jobId, segDuration, seg.Text.Length, seg.Text);
                            continue;
                        }

                        // ── Compute absolute timestamps ───────────
                        var absStart = windowStartTime + seg.Start;
                        var absEnd = windowStartTime + seg.End;

                        // ===== SIMPLE MODE =====
                        if (isSimple)
                        {
                            await svc.PushSegmentAsync(
                                jobId, seg.Text, absStart, absEnd, seg.Confidence, ct: ct);
                            lastEmittedTimestamp = absEnd;
                            continue;
                        }

                        // ── Sliding-window dedup ─────────────────
                        if (absEnd <= lastEmittedTimestamp + TimestampToleranceSeconds)
                            continue;

                        if (IsOverlapDuplicate(absStart, absEnd, seg.Text, recentSegments))
                        {
                            logger.LogDebug(
                                "Job {JobId}: discarding segment (overlap duplicate): {Text}",
                                jobId, seg.Text);
                            continue;
                        }

                        // ── Commit delay gate ────────────────────
                        var passedCommitDelay = absEnd <= currentAbsTime - CommitDelaySeconds;

                        if (!passedCommitDelay)
                        {
                            // ===== TWO-PASS: emit provisional =====
                            // Only emit if confidence is above the floor —
                            // low-confidence segments produce noisy churn.
                            if (isTwoPass
                                && (!seg.Confidence.HasValue
                                    || seg.Confidence.Value >= ProvisionalConfidenceFloor)
                                && !HasProvisionalOverlap(absStart, absEnd, seg.Text, provisionalSegments))
                            {
                                var prov = await svc.PushSegmentAsync(
                                    jobId, seg.Text, absStart, absEnd, seg.Confidence,
                                    isProvisional: true, ct: ct);

                                if (prov is not null)
                                {
                                    provisionalSegments.Add(new ProvisionalSegment(
                                        prov.Id, seg.Text, absStart, absEnd));

                                    // Update prompt conditioning with provisional
                                    // text so subsequent inference ticks benefit
                                    // from Whisper context continuity.  For models
                                    // that don't return per-segment timestamps
                                    // (HasTimestampedSegments=false) the confirmed
                                    // path is never reached because absEnd ≈
                                    // currentAbsTime and the commit delay never
                                    // passes.  Without this, the prompt stays
                                    // empty and Whisper loses vocabulary/style
                                    // anchoring after the first segment.
                                    promptBuffer = (promptBuffer + " " + seg.Text).Trim();
                                    if (promptBuffer.Length > MaxPromptChars)
                                        promptBuffer = promptBuffer[^MaxPromptChars..];
                                }
                            }
                            continue;
                        }

                        // ── Segment confirmed ─ emit / finalize ──
                        if (isTwoPass)
                        {
                            var match = FindProvisionalMatch(absStart, absEnd, seg.Text, provisionalSegments);
                            if (match >= 0)
                            {
                                var prov = provisionalSegments[match];
                                await svc.FinalizeSegmentAsync(
                                    jobId, prov.SegmentId, seg.Text, seg.Confidence, ct);
                                provisionalSegments.RemoveAt(match);
                            }
                            else
                            {
                                await svc.PushSegmentAsync(
                                    jobId, seg.Text, absStart, absEnd, seg.Confidence, ct: ct);
                            }
                        }
                        else
                        {
                            // StrictSlidingWindow: emit only after commit delay
                            await svc.PushSegmentAsync(
                                jobId, seg.Text, absStart, absEnd, seg.Confidence, ct: ct);
                        }

                        lastEmittedTimestamp = absEnd;
                        TrackEmittedSegment(recentSegments, seg.Text, absStart, absEnd);

                        // Update prompt conditioning buffer with
                        // confirmed text for the next inference tick.
                        promptBuffer = (promptBuffer + " " + seg.Text).Trim();
                        if (promptBuffer.Length > MaxPromptChars)
                            promptBuffer = promptBuffer[^MaxPromptChars..];
                    }

                    // ── Finalize stale provisionals ─────────────
                    // Provisionals that survived beyond 2× the commit delay
                    // without being matched by a later confirmed segment are
                    // almost certainly real speech.  Promote them instead of
                    // retracting so accumulated transcription text is never
                    // silently deleted.
                    if (isTwoPass)
                    {
                        var staleThreshold = currentAbsTime - CommitDelaySeconds * 2;
                        for (var i = provisionalSegments.Count - 1; i >= 0; i--)
                        {
                            if (provisionalSegments[i].AbsEnd < staleThreshold)
                            {
                                var stale = provisionalSegments[i];
                                logger.LogDebug(
                                    "Job {JobId}: finalizing stale provisional: {Text}",
                                    jobId, stale.Text);
                                await svc.FinalizeSegmentAsync(
                                    jobId, stale.SegmentId, stale.Text, ct: ct);
                                lastEmittedTimestamp = Math.Max(lastEmittedTimestamp, stale.AbsEnd);
                                TrackEmittedSegment(recentSegments, stale.Text, stale.AbsStart, stale.AbsEnd);

                                // Keep prompt conditioning in sync when
                                // promoting stale provisionals to final.
                                promptBuffer = (promptBuffer + " " + stale.Text).Trim();
                                if (promptBuffer.Length > MaxPromptChars)
                                    promptBuffer = promptBuffer[^MaxPromptChars..];

                                provisionalSegments.RemoveAt(i);
                            }
                        }
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

    // ═══════════════════════════════════════════════════════════════
    // Deduplication helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Checks whether a candidate segment overlaps significantly with
    /// any recently emitted segment AND contains similar text.  This
    /// catches duplicates that the simple monotonic-timestamp check
    /// misses — e.g. when Whisper shifts a segment boundary by more
    /// than <see cref="TimestampToleranceSeconds"/> or re-emits a
    /// segment after a silence gap.
    /// </summary>
    private static bool IsOverlapDuplicate(
        double absStart, double absEnd, string text,
        List<EmittedSegment> recent)
    {
        if (recent.Count == 0)
            return false;

        var candidateDuration = absEnd - absStart;
        if (candidateDuration <= 0)
            return true;

        foreach (var prev in recent)
        {
            var overlapStart = Math.Max(prev.AbsStart, absStart);
            var overlapEnd = Math.Min(prev.AbsEnd, absEnd);
            var overlap = Math.Max(0, overlapEnd - overlapStart);

            var prevDuration = prev.AbsEnd - prev.AbsStart;
            var shorterDuration = Math.Min(candidateDuration, prevDuration);
            if (shorterDuration <= 0)
                continue;

            var overlapRatio = overlap / shorterDuration;
            if (overlapRatio <= DuplicateOverlapThreshold)
                continue;

            if (TextContains(prev.Text, text))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> when one text contains the other
    /// (case-insensitive).  Catches exact duplicates, prefix matches
    /// (Whisper trimming differently), and substring matches (Whisper
    /// merging/splitting at word boundaries).
    /// </summary>
    private static bool TextContains(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
            return false;

        return a.Contains(b, StringComparison.OrdinalIgnoreCase)
            || b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Adds a segment to the recent-emission buffer, evicting the
    /// oldest entry when the buffer is full.
    /// </summary>
    private static void TrackEmittedSegment(
        List<EmittedSegment> recent, string text,
        double absStart, double absEnd)
    {
        if (recent.Count >= MaxRecentSegments)
            recent.RemoveAt(0);

        recent.Add(new EmittedSegment(text, absStart, absEnd));
    }

    /// <summary>
    /// A recently emitted segment, kept in a small FIFO buffer for
    /// overlap-based deduplication.
    /// </summary>
    private readonly record struct EmittedSegment(
        string Text, double AbsStart, double AbsEnd);

    /// <summary>
    /// A provisional segment emitted in two-pass mode that has not yet
    /// been finalized or retracted.
    /// </summary>
    private readonly record struct ProvisionalSegment(
        Guid SegmentId, string Text, double AbsStart, double AbsEnd);

    /// <summary>
    /// Returns <see langword="true"/> when a provisional segment already
    /// covers the same time range and text — prevents re-emitting the
    /// same provisional on every inference tick.
    /// </summary>
    private static bool HasProvisionalOverlap(
        double absStart, double absEnd, string text,
        List<ProvisionalSegment> provisionals)
    {
        var candidateDuration = absEnd - absStart;
        if (candidateDuration <= 0)
            return true;

        foreach (var p in provisionals)
        {
            var overlapStart = Math.Max(p.AbsStart, absStart);
            var overlapEnd = Math.Min(p.AbsEnd, absEnd);
            var overlap = Math.Max(0, overlapEnd - overlapStart);

            var pDuration = p.AbsEnd - p.AbsStart;
            var shorter = Math.Min(candidateDuration, pDuration);
            if (shorter <= 0)
                continue;

            if (overlap / shorter > DuplicateOverlapThreshold && TextContains(p.Text, text))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Finds the index of the provisional segment that best matches the
    /// given confirmed segment, or -1 if none matches.
    /// </summary>
    private static int FindProvisionalMatch(
        double absStart, double absEnd, string text,
        List<ProvisionalSegment> provisionals)
    {
        var candidateDuration = absEnd - absStart;
        if (candidateDuration <= 0)
            return -1;

        var bestIndex = -1;
        var bestOverlap = 0.0;

        for (var i = 0; i < provisionals.Count; i++)
        {
            var p = provisionals[i];
            var overlapStart = Math.Max(p.AbsStart, absStart);
            var overlapEnd = Math.Min(p.AbsEnd, absEnd);
            var overlap = Math.Max(0, overlapEnd - overlapStart);

            var pDuration = p.AbsEnd - p.AbsStart;
            var shorter = Math.Min(candidateDuration, pDuration);
            if (shorter <= 0)
                continue;

            var ratio = overlap / shorter;
            if (ratio > DuplicateOverlapThreshold && TextContains(p.Text, text) && ratio > bestOverlap)
            {
                bestOverlap = ratio;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Converts float PCM samples to a WAV byte array suitable for the
    /// transcription API. Writes a standard RIFF/WAV header for mono
    /// 16 kHz 16-bit PCM.
    /// </summary>
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

    /// <summary>Writes a log entry to a job from a background context.</summary>
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

    /// <summary>
    /// Returns the override value clamped to [min, max], or the default
    /// when the override is <see langword="null"/>.
    /// </summary>
    private static int Clamp(int? value, int min, int max, int defaultValue) =>
        value.HasValue ? Math.Clamp(value.Value, min, max) : defaultValue;

    /// <summary>
    /// Computes the RMS energy of a float PCM sample buffer.
    /// Used by the adaptive VAD to track the ambient noise floor.
    /// </summary>
    private static double ComputeRms(float[] samples)
    {
        if (samples.Length == 0)
            return 0;

        double sum = 0;
        foreach (var s in samples)
            sum += s * (double)s;

        return Math.Sqrt(sum / samples.Length);
    }

    /// <summary>
    /// Compares the previous and current window transcription text at
    /// word level and returns only the genuinely new content.
    /// <para>
    /// <b>Phase 1 — exact suffix-prefix match:</b> finds the longest
    /// suffix of <paramref name="previous"/> that exactly matches a
    /// prefix of <paramref name="current"/>.  This is the fast path
    /// for the common case where Whisper is consistent.
    /// </para>
    /// <para>
    /// <b>Phase 2 — anchor search (fallback):</b> when the exact match
    /// fails (e.g. Whisper inserts or drops a word in the overlapping
    /// audio), searches for the <b>tail</b> of <paramref name="previous"/>
    /// as a contiguous word sequence <em>anywhere</em> in
    /// <paramref name="current"/>.  Everything after that anchor in
    /// <paramref name="current"/> is the genuinely new content.  The
    /// search starts from the rightmost position so that repeated
    /// phrases resolve to the latest occurrence.
    /// </para>
    /// <para>
    /// Both texts are normalised before comparison: contractions are
    /// expanded (<c>I'm → I am</c>) and trailing punctuation is
    /// stripped from each word.  This prevents Whisper's inconsistent
    /// contraction/punctuation handling from breaking the overlap
    /// match.  The returned text comes from the original (unexpanded)
    /// <paramref name="current"/>.
    /// </para>
    /// <para>
    /// Used for models that don't return per-segment timestamps (e.g.
    /// <c>gpt-4o-transcribe</c> in <c>json</c> mode) where the full
    /// window is a single synthetic segment and overlap-based dedup
    /// cannot work.
    /// </para>
    /// </summary>
    private static string ExtractNewContent(string previous, string current)
    {
        if (string.IsNullOrWhiteSpace(previous))
            return current;

        // Normalise: expand contractions → lowercase so "I'm" and
        // "I am" compare as identical across Whisper inference runs.
        var prevNorm = ExpandContractions(previous).ToLowerInvariant();
        var currNorm = ExpandContractions(current).ToLowerInvariant();

        if (prevNorm == currNorm)
            return "";

        var prevWords = prevNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var currWords = currNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // ── Phase 1: exact suffix-of-prev = prefix-of-curr ──────
        // Find the longest suffix of prevWords whose words match the
        // corresponding prefix of currWords.  Start from the longest
        // possible overlap and shrink until a match is found.
        var bestOverlap = 0;
        var maxPossible = Math.Min(prevWords.Length, currWords.Length);

        for (var k = maxPossible; k >= 1; k--)
        {
            var allMatch = true;
            for (var j = 0; j < k; j++)
            {
                if (StripTrailingPunctuation(prevWords[prevWords.Length - k + j])
                    != StripTrailingPunctuation(currWords[j]))
                {
                    allMatch = false;
                    break;
                }
            }

            if (allMatch)
            {
                bestOverlap = k;
                break;
            }
        }

        // ── Phase 2: anchor search (tolerates insertions/deletions) ─
        // When the exact match fails because Whisper inserted or
        // dropped a word in the overlapping audio region, search for
        // the tail of prevWords as a contiguous sequence anywhere in
        // currWords.  Try decreasing anchor lengths (5 → 2) so that
        // the longest reliable match wins.  Search from the right so
        // repeated phrases resolve to the latest (correct) occurrence.
        if (bestOverlap == 0 && prevWords.Length >= 2 && currWords.Length >= 2)
        {
            var anchorMax = Math.Min(5, prevWords.Length);

            for (var a = anchorMax; a >= 2; a--)
            {
                var tailStart = prevWords.Length - a;

                for (var pos = currWords.Length - a; pos >= 0; pos--)
                {
                    var match = true;
                    for (var j = 0; j < a; j++)
                    {
                        if (StripTrailingPunctuation(prevWords[tailStart + j])
                            != StripTrailingPunctuation(currWords[pos + j]))
                        {
                            match = false;
                            break;
                        }
                    }

                    if (match)
                    {
                        bestOverlap = pos + a;
                        break;
                    }
                }

                if (bestOverlap > 0)
                    break;
            }
        }

        if (bestOverlap >= currWords.Length)
            return "";

        // Map the normalised overlap length back to a position in the
        // original (unexpanded) current text so the output preserves
        // Whisper's actual wording.
        return SkipNormalizedWords(current, bestOverlap);
    }

    /// <summary>
    /// Walks through <paramref name="original"/>, expanding each word's
    /// contractions to count normalised words, and returns the substring
    /// after <paramref name="normalizedWordCount"/> normalised words have
    /// been consumed.  A single original word may expand to two
    /// normalised words (e.g. "I'm" → "I am").
    /// </summary>
    private static string SkipNormalizedWords(string original, int normalizedWordCount)
    {
        var words = original.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var consumed = 0;
        var origIndex = 0;

        while (consumed < normalizedWordCount && origIndex < words.Length)
        {
            var expanded = ExpandContractions(words[origIndex]);
            consumed += expanded.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            origIndex++;
        }

        return origIndex >= words.Length
            ? ""
            : string.Join(' ', words[origIndex..]);
    }

    /// <summary>
    /// Expands common English contractions that Whisper frequently
    /// flip-flops between across successive inference windows.
    /// </summary>
    private static string ExpandContractions(string text) => text
        .Replace("I'm", "I am", StringComparison.OrdinalIgnoreCase)
        .Replace("I've", "I have", StringComparison.OrdinalIgnoreCase)
        .Replace("I'll", "I will", StringComparison.OrdinalIgnoreCase)
        .Replace("I'd", "I would", StringComparison.OrdinalIgnoreCase)
        .Replace("it's", "it is", StringComparison.OrdinalIgnoreCase)
        .Replace("that's", "that is", StringComparison.OrdinalIgnoreCase)
        .Replace("there's", "there is", StringComparison.OrdinalIgnoreCase)
        .Replace("here's", "here is", StringComparison.OrdinalIgnoreCase)
        .Replace("what's", "what is", StringComparison.OrdinalIgnoreCase)
        .Replace("who's", "who is", StringComparison.OrdinalIgnoreCase)
        .Replace("don't", "do not", StringComparison.OrdinalIgnoreCase)
        .Replace("doesn't", "does not", StringComparison.OrdinalIgnoreCase)
        .Replace("didn't", "did not", StringComparison.OrdinalIgnoreCase)
        .Replace("can't", "cannot", StringComparison.OrdinalIgnoreCase)
        .Replace("won't", "will not", StringComparison.OrdinalIgnoreCase)
        .Replace("isn't", "is not", StringComparison.OrdinalIgnoreCase)
        .Replace("aren't", "are not", StringComparison.OrdinalIgnoreCase)
        .Replace("wasn't", "was not", StringComparison.OrdinalIgnoreCase)
        .Replace("weren't", "were not", StringComparison.OrdinalIgnoreCase)
        .Replace("hasn't", "has not", StringComparison.OrdinalIgnoreCase)
        .Replace("haven't", "have not", StringComparison.OrdinalIgnoreCase)
        .Replace("couldn't", "could not", StringComparison.OrdinalIgnoreCase)
        .Replace("wouldn't", "would not", StringComparison.OrdinalIgnoreCase)
        .Replace("shouldn't", "should not", StringComparison.OrdinalIgnoreCase)
        .Replace("we're", "we are", StringComparison.OrdinalIgnoreCase)
        .Replace("they're", "they are", StringComparison.OrdinalIgnoreCase)
        .Replace("you're", "you are", StringComparison.OrdinalIgnoreCase)
        .Replace("we've", "we have", StringComparison.OrdinalIgnoreCase)
        .Replace("they've", "they have", StringComparison.OrdinalIgnoreCase)
        .Replace("you've", "you have", StringComparison.OrdinalIgnoreCase)
        .Replace("we'll", "we will", StringComparison.OrdinalIgnoreCase)
        .Replace("they'll", "they will", StringComparison.OrdinalIgnoreCase)
        .Replace("you'll", "you will", StringComparison.OrdinalIgnoreCase)
        .Replace("let's", "let us", StringComparison.OrdinalIgnoreCase);

    private static string StripTrailingPunctuation(string word) =>
        word.TrimEnd('.', ',', '!', '?', ';', ':');
}
