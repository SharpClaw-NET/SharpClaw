using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Modules.SystemAudio.Services;
using SharpClaw.Modules.Transcription.Contracts;
using SharpClaw.Modules.Transcription.Services;

namespace SharpClaw.Modules.Transcription;

/// <summary>
/// Handles the <c>StartTranscription</c>, <c>StopTranscription</c>, and
/// <c>GetDefaultInputAudio</c> module task steps in the task orchestrator.
/// Also owns the transcription segment event loop.
/// </summary>
internal sealed class TranscriptionTaskStepExecutor(
    TranscriptionTaskBridge taskBridge,
    ITranscriptionSegmentPublisher segmentPublisher,
    IServiceScopeFactory scopeFactory) : ITaskStepExecutorExtension
{
    public string ModuleId => "sharpclaw_transcription";

    public bool CanExecute(string moduleStepKey) =>
        moduleStepKey is "StartTranscription"
            or "StopTranscription"
            or "GetDefaultInputAudio";

    public async Task<bool> ExecuteAsync(
        string moduleStepKey,
        ITaskStepExecutionContext context,
        IReadOnlyList<string>? arguments,
        string? expression,
        string? resultVariable)
    {
        switch (moduleStepKey)
        {
            case "StartTranscription":
                await ExecuteStartTranscriptionAsync(context, arguments, expression, resultVariable);
                return true;

            case "StopTranscription":
                await ExecuteStopTranscriptionAsync(context, expression, resultVariable);
                return true;

            case "GetDefaultInputAudio":
                await ExecuteGetDefaultInputAudioAsync(context, resultVariable);
                return true;

            default:
                return true;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Step implementations
    // ──────────────────────────────────────────────────────────────

    private async Task ExecuteStartTranscriptionAsync(
        ITaskStepExecutionContext context,
        IReadOnlyList<string>? arguments,
        string? expression,
        string? resultVariable)
    {
        var deviceIdStr = arguments is { Count: > 0 }
            ? arguments[0]
            : expression ?? "";

        if (!Guid.TryParse(deviceIdStr, out var deviceId))
            throw new InvalidOperationException($"Invalid audio device ID: {deviceIdStr}");

        var jobRequest = new SubmitAgentJobRequest(
            ActionKey: "transcribe_from_audio_device",
            ResourceId: deviceId);

        var jobResponse = await taskBridge.SubmitJobAsync(
            context.ChannelId, jobRequest, context.CancellationToken);

        await context.AppendLogAsync(
            $"Started transcription job {jobResponse.Id} on device {deviceId}");

        if (resultVariable is not null)
            context.Variables[resultVariable] = jobResponse.Id.ToString();

        StartEventLoop(context, jobResponse.Id);
    }

    private async Task ExecuteStopTranscriptionAsync(
        ITaskStepExecutionContext context,
        string? expression,
        string? resultVariable)
    {
        if (!Guid.TryParse(expression ?? "", out var jobId))
            throw new InvalidOperationException($"Invalid transcription job ID: {expression}");

        await taskBridge.StopTranscriptionJobAsync(jobId, context.CancellationToken);

        await context.AppendLogAsync($"Stopped transcription job {jobId}");
    }

    private async Task ExecuteGetDefaultInputAudioAsync(
        ITaskStepExecutionContext context,
        string? resultVariable)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IInputAudioDeviceResolver>();

        var deviceId = await resolver.GetDefaultDeviceIdAsync(context.CancellationToken);

        if (resultVariable is not null)
            context.Variables[resultVariable] = deviceId.ToString();
    }

    // ──────────────────────────────────────────────────────────────
    // Segment event loop
    // ──────────────────────────────────────────────────────────────

    private void StartEventLoop(ITaskStepExecutionContext context, Guid jobId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var reader = segmentPublisher.Subscribe(jobId);
                if (reader is null) return;

                await foreach (var segment in reader.ReadAllAsync(context.CancellationToken))
                {
                    var handlers = context.EventHandlers
                        .Where(h => h.ModuleTriggerKey == "TranscriptionSegment")
                        .ToList();

                    foreach (var handler in handlers)
                    {
                        if (handler.ParameterName is not null)
                        {
                            context.Variables[handler.ParameterName] =
                                JsonSerializer.Serialize(new
                                {
                                    segment.Text,
                                    segment.StartTime,
                                    segment.EndTime,
                                    segment.Confidence
                                });
                            context.Variables[handler.ParameterName + ".Text"] = segment.Text;
                        }

                        context.CancellationToken.ThrowIfCancellationRequested();
                        await handler.ExecuteBodyAsync(context.CancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await context.AppendLogAsync($"Event loop error: {ex.Message}");
            }
        }, CancellationToken.None);
    }
}
