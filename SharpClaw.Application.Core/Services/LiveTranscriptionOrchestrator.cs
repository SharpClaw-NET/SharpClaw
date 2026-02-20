using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Enums;
using SharpClaw.Infrastructure.Models;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Utils.Security;

namespace SharpClaw.Application.Services;

/// <summary>
/// Singleton service that manages active audio-capture→STT→push loops.
/// When a transcription job is started, the orchestrator spawns a
/// background task that captures audio chunks from the device, sends
/// each chunk to the transcription API, and pushes segments into the
/// <see cref="TranscriptionService"/>.
/// </summary>
public sealed class LiveTranscriptionOrchestrator(
    IServiceScopeFactory scopeFactory,
    Core.Clients.IAudioCaptureProvider captureProvider,
    Core.Clients.TranscriptionApiClientFactory transcriptionClientFactory,
    IHttpClientFactory httpClientFactory,
    EncryptionOptions encryptionOptions,
    ILogger<LiveTranscriptionOrchestrator> logger)
{
    private static readonly TimeSpan ChunkDuration = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeSessions = new();

    /// <summary>
    /// Starts live audio capture and transcription for a job.
    /// The caller must have already created the <see cref="TranscriptionJobDB"/>
    /// record in the database.
    /// </summary>
    public void Start(Guid jobId, Guid modelId, string? deviceIdentifier, string? language)
    {
        var cts = new CancellationTokenSource();
        if (!_activeSessions.TryAdd(jobId, cts))
        {
            cts.Dispose();
            throw new InvalidOperationException($"Transcription job {jobId} is already running.");
        }

        // One background task per job — never more. See RunCaptureLoopAsync header.
        _ = Task.Run(() => RunCaptureLoopAsync(jobId, modelId, deviceIdentifier, language, cts.Token));
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
    // Capture → STT → Push loop
    //
    // INVARIANT: Each job runs on exactly ONE task.  The capture
    // provider calls the onChunkReady callback sequentially — never
    // from concurrent tasks.  The mutable state closed over by the
    // callback (consecutiveErrors, streamStartTime) is therefore
    // safe without locks or Interlocked.  DO NOT break this
    // guarantee by parallelising chunk processing.
    // ═══════════════════════════════════════════════════════════════

    private async Task RunCaptureLoopAsync(
        Guid jobId, Guid modelId, string? deviceIdentifier,
        string? language, CancellationToken ct)
    {
        logger.LogInformation(
            "Starting live transcription for job {JobId} on device '{Device}'",
            jobId, deviceIdentifier ?? "default");

        // Resolve model + provider once
        string apiKey;
        string modelName;
        ProviderType providerType;

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
            var model = await db.Models
                .Include(m => m.Provider)
                .FirstOrDefaultAsync(m => m.Id == modelId, ct)
                ?? throw new InvalidOperationException($"Model {modelId} not found.");

            if (string.IsNullOrEmpty(model.Provider.EncryptedApiKey))
                throw new InvalidOperationException("Provider does not have an API key configured.");

            apiKey = ApiKeyEncryptor.Decrypt(model.Provider.EncryptedApiKey, encryptionOptions.Key);
            modelName = model.Name;
            providerType = model.Provider.ProviderType;
        }

        var sttClient = transcriptionClientFactory.GetClient(providerType);
        var streamStartTime = 0.0;
        var consecutiveErrors = 0;
        const int maxConsecutiveErrors = 5;

        try
        {
            await captureProvider.CaptureAsync(
                deviceIdentifier,
                ChunkDuration,
                async (wavBytes, chunkIndex) =>
                {
                    if (ct.IsCancellationRequested) return;

                    try
                    {
                        using var httpClient = httpClientFactory.CreateClient();
                        var result = await sttClient.TranscribeAsync(
                            httpClient, apiKey, modelName, wavBytes, language, ct);

                        consecutiveErrors = 0;

                        if (string.IsNullOrWhiteSpace(result.Text))
                            return;

                        using var scope = scopeFactory.CreateScope();
                        var svc = scope.ServiceProvider.GetRequiredService<AgentJobService>();

                        foreach (var seg in result.Segments)
                        {
                            var absStart = streamStartTime + seg.Start;
                            var absEnd = streamStartTime + seg.End;

                            await svc.PushSegmentAsync(
                                jobId, seg.Text, absStart, absEnd, seg.Confidence, ct);
                        }

                        streamStartTime += result.Duration;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        consecutiveErrors++;
                        logger.LogWarning(ex,
                            "Transcription chunk {Index} failed for job {JobId} ({Consecutive}/{Max})",
                            chunkIndex, jobId, consecutiveErrors, maxConsecutiveErrors);

                        await AddJobLogAsync(jobId,
                            $"Chunk {chunkIndex} failed: {ex.Message}" +
                            $" ({consecutiveErrors}/{maxConsecutiveErrors} consecutive)",
                            "Warning");

                        if (consecutiveErrors >= maxConsecutiveErrors)
                            throw new InvalidOperationException(
                                $"Aborting: {maxConsecutiveErrors} consecutive chunk failures. Last error: {ex.Message}",
                                ex);
                    }
                },
                ct);
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
            if (_activeSessions.TryRemove(jobId, out var cts))
            {
                await cts.CancelAsync();
                cts.Dispose();
            }
        }
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
}
