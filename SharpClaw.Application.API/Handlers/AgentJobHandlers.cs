using Microsoft.AspNetCore.Http;
using SharpClaw.Application.API.Routing;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Transcription;

namespace SharpClaw.Application.API.Handlers;

[RouteGroup("/channels/{channelId:guid}/jobs")]
public static class AgentJobHandlers
{
    [MapPost]
    public static async Task<IResult> Submit(
        Guid channelId, SubmitAgentJobRequest request, AgentJobService svc)
        => Results.Ok(await svc.SubmitAsync(channelId, request));

    [MapGet]
    public static async Task<IResult> List(Guid channelId, AgentJobService svc)
        => Results.Ok(await svc.ListAsync(channelId));

    [MapGet("/{jobId:guid}")]
    public static async Task<IResult> GetById(
        Guid channelId, Guid jobId, AgentJobService svc)
    {
        var job = await svc.GetAsync(jobId);
        return job is not null ? Results.Ok(job) : Results.NotFound();
    }

    [MapPost("/{jobId:guid}/approve")]
    public static async Task<IResult> Approve(
        Guid channelId, Guid jobId, ApproveAgentJobRequest request, AgentJobService svc)
    {
        var job = await svc.ApproveAsync(jobId, request);
        return job is not null ? Results.Ok(job) : Results.NotFound();
    }

    [MapPost("/{jobId:guid}/stop")]
    public static async Task<IResult> Stop(
        Guid channelId, Guid jobId, AgentJobService svc)
    {
        var job = await svc.StopTranscriptionAsync(jobId);
        return job is not null ? Results.Ok(job) : Results.NotFound();
    }

    [MapPost("/{jobId:guid}/cancel")]
    public static async Task<IResult> Cancel(
        Guid channelId, Guid jobId, AgentJobService svc)
    {
        var job = await svc.CancelAsync(jobId);
        return job is not null ? Results.Ok(job) : Results.NotFound();
    }

    [MapPost("/{jobId:guid}/segments")]
    public static async Task<IResult> PushSegment(
        Guid channelId, Guid jobId, PushSegmentRequest request, AgentJobService svc)
    {
        var segment = await svc.PushSegmentAsync(
            jobId, request.Text, request.StartTime, request.EndTime, request.Confidence);
        return segment is not null ? Results.Ok(segment) : Results.NotFound();
    }

    [MapGet("/{jobId:guid}/segments")]
    public static async Task<IResult> GetSegments(
        Guid channelId, Guid jobId, AgentJobService svc, DateTimeOffset? since = null)
    {
        var segments = await svc.GetSegmentsSinceAsync(
            jobId, since ?? DateTimeOffset.MinValue);
        return Results.Ok(segments);
    }
}
