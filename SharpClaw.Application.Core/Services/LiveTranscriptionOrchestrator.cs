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

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(effectiveStep), ct);

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
                    continue;
                }

                // Extract the sliding window and convert to WAV for the API
                var windowSamples = ringBuffer.GetLastSeconds(effectiveWindow);
                if (windowSamples.Length == 0)
                    continue;

                var windowStartTime = ringBuffer.GetWindowStartTime(effectiveWindow);
                var wavBytes = FloatSamplesToWav(windowSamples, SampleRate);

                try
                {
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

                    // Current absolute time for commit-delay filtering
                    var currentAbsTime = (double)ringBuffer.TotalWritten / SampleRate;

                    using var scope = scopeFactory.CreateScope();
                    var svc = scope.ServiceProvider.GetRequiredService<AgentJobService>();

                    foreach (var seg in result.Segments)
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

                    // ── Retract stale provisionals ──────────────
                    if (isTwoPass)
                    {
                        var staleThreshold = currentAbsTime - CommitDelaySeconds * 2;
                        for (var i = provisionalSegments.Count - 1; i >= 0; i--)
                        {
                            if (provisionalSegments[i].AbsEnd < staleThreshold)
                            {
                                logger.LogDebug(
                                    "Job {JobId}: retracting stale provisional: {Text}",
                                    jobId, provisionalSegments[i].Text);
                                await svc.RetractSegmentAsync(
                                    jobId, provisionalSegments[i].SegmentId, ct);
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
}
