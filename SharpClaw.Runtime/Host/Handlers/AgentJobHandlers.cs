using Microsoft.AspNetCore.Http;
using SharpClaw.Runtime.Host.Routing;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Contracts.DTOs.AgentActions;

namespace SharpClaw.Runtime.Host.Handlers;

[RouteGroup("/channels/{channelId:guid}/jobs")]
public static class AgentJobHandlers
{
    [MapPost]
    public static async Task<IResult> Submit(
        Guid channelId, SubmitAgentJobRequest request, AgentJobService svc, ChatService chatSvc)
    {
        var job = await svc.SubmitAsync(channelId, request);
        var cost = await chatSvc.GetChannelCostAsync(job.ChannelId);
        return Results.Ok(job with { ChannelCost = cost });
    }

    [MapGet]
    public static async Task<IResult> List(Guid channelId, AgentJobService svc)
        => Results.Ok(await svc.ListAsync(channelId));

    [MapGet("/summaries")]
    public static async Task<IResult> ListSummaries(Guid channelId, AgentJobService svc)
        => Results.Ok(await svc.ListSummariesAsync(channelId));

    [MapGet("/{jobId:guid}")]
    public static async Task<IResult> GetById(
        Guid channelId, Guid jobId, AgentJobService svc, ChatService chatSvc)
    {
        var job = await GetScopedJobAsync(channelId, jobId, svc);
        if (job is null) return Results.NotFound();
        var cost = await chatSvc.GetChannelCostAsync(job.ChannelId);
        return Results.Ok(job with { ChannelCost = cost });
    }

    [MapPost("/{jobId:guid}/approve")]
    public static async Task<IResult> Approve(
        Guid channelId, Guid jobId, ApproveAgentJobRequest request, AgentJobService svc, ChatService chatSvc)
    {
        if (await GetScopedJobSummaryAsync(channelId, jobId, svc) is null) return Results.NotFound();
        var job = await svc.ApproveAsync(jobId, request);
        if (job is null) return Results.NotFound();
        var cost = await chatSvc.GetChannelCostAsync(job.ChannelId);
        return Results.Ok(job with { ChannelCost = cost });
    }

    [MapPost("/{jobId:guid}/stop")]
    public static async Task<IResult> Stop(
        Guid channelId, Guid jobId, AgentJobService svc, ChatService chatSvc)
    {
        if (await GetScopedJobSummaryAsync(channelId, jobId, svc) is null) return Results.NotFound();
        var job = await svc.StopAsync(jobId);
        if (job is null) return Results.NotFound();
        var cost = await chatSvc.GetChannelCostAsync(job.ChannelId);
        return Results.Ok(job with { ChannelCost = cost });
    }

    [MapPost("/{jobId:guid}/cancel")]
    public static async Task<IResult> Cancel(
        Guid channelId, Guid jobId, AgentJobService svc, ChatService chatSvc)
    {
        if (await GetScopedJobSummaryAsync(channelId, jobId, svc) is null) return Results.NotFound();
        var job = await svc.CancelAsync(jobId);
        if (job is null) return Results.NotFound();
        var cost = await chatSvc.GetChannelCostAsync(job.ChannelId);
        return Results.Ok(job with { ChannelCost = cost });
    }

    [MapPut("/{jobId:guid}/pause")]
    public static async Task<IResult> Pause(
        Guid channelId, Guid jobId, AgentJobService svc, ChatService chatSvc)
    {
        if (await GetScopedJobSummaryAsync(channelId, jobId, svc) is null) return Results.NotFound();
        var job = await svc.PauseAsync(jobId);
        if (job is null) return Results.NotFound();
        var cost = await chatSvc.GetChannelCostAsync(job.ChannelId);
        return Results.Ok(job with { ChannelCost = cost });
    }

    [MapPut("/{jobId:guid}/resume")]
    public static async Task<IResult> Resume(
        Guid channelId, Guid jobId, AgentJobService svc, ChatService chatSvc)
    {
        if (await GetScopedJobSummaryAsync(channelId, jobId, svc) is null) return Results.NotFound();
        var job = await svc.ResumeAsync(jobId);
        if (job is null) return Results.NotFound();
        var cost = await chatSvc.GetChannelCostAsync(job.ChannelId);
        return Results.Ok(job with { ChannelCost = cost });
    }

    private static async Task<AgentJobResponse?> GetScopedJobAsync(
        Guid channelId, Guid jobId, AgentJobService svc)
    {
        var job = await svc.GetAsync(jobId);
        return job?.ChannelId == channelId ? job : null;
    }

    private static async Task<AgentJobSummaryResponse?> GetScopedJobSummaryAsync(
        Guid channelId, Guid jobId, AgentJobService svc)
    {
        var job = await svc.GetSummaryAsync(jobId);
        return job?.ChannelId == channelId ? job : null;
    }
}
