using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SharpClaw.Modules.Transcription.Services;

namespace SharpClaw.Modules.Transcription.Handlers;

/// <summary>
/// Registers minimal-API REST endpoints for transcription job queries
/// and segment polling.  These complement the core job endpoints by
/// exposing a transcription-specific DTO that omits shell fields and
/// adds segment statistics.
/// </summary>
public static class TranscriptionJobEndpoints
{
    /// <summary>
    /// Maps transcription job query and segment polling endpoints under
    /// <c>/transcription/jobs</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapTranscriptionJobEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/transcription/jobs");

        group.MapGet("/", async (TranscriptionService svc, Guid? inputAudioId = null)
            => Results.Ok(await svc.ListTranscriptionJobsAsync(inputAudioId)));

        group.MapGet("/summaries", async (TranscriptionService svc, Guid? inputAudioId = null)
            => Results.Ok(await svc.ListTranscriptionJobSummariesAsync(inputAudioId)));

        group.MapGet("/{jobId:guid}", async (Guid jobId, TranscriptionService svc) =>
        {
            var job = await svc.GetTranscriptionJobAsync(jobId);
            return job is not null ? Results.Ok(job) : Results.NotFound();
        });

        group.MapGet("/{jobId:guid}/segments", async (
            Guid jobId, TranscriptionService svc, DateTimeOffset? since = null) =>
        {
            var segments = await svc.GetSegmentsAsync(jobId, since);
            return segments is not null ? Results.Ok(segments) : Results.NotFound();
        });

        return routes;
    }
}
