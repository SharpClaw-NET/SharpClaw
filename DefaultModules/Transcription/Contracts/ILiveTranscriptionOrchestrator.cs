namespace SharpClaw.Modules.Transcription.Contracts;

internal interface ILiveTranscriptionOrchestrator
{
    bool SupportsProvider(string providerKey);

    void Start(
        Guid jobId,
        Guid modelId,
        Guid deviceId,
        string? language,
        TranscriptionMode? mode = null,
        int? windowSeconds = null,
        int? stepSeconds = null);

    void Stop(Guid jobId);

    bool IsRunning(Guid jobId);

    Task ResumeTranscriptionJobAsync(Guid jobId, CancellationToken ct = default);

    Task StopAllAsync();
}
