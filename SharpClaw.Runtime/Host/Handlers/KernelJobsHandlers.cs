using Microsoft.AspNetCore.Http;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.Host.Routing;

namespace SharpClaw.Runtime.Host.Handlers;

[RouteGroup("/jobs")]
public static class KernelJobsHandlers
{
    [MapPost]
    public static async Task<IResult> Submit(
        KernelJobSubmissionRequest request,
        KernelJobsCoordinator coordinator,
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ActionKey) || request.Input is null)
            return Results.BadRequest(new { error = "A Jobs action key and input are required." });

        var executionContext = KernelHostEndpoints.CreateExecutionContext(context);
        var job = await coordinator.SubmitAsync(
            new JobSubmission<JobPayloadEnvelope>(
                new SharpClawActionKey(request.ActionKey),
                request.Input,
                executionContext.Caller,
                executionContext.Features,
                request.ConversationId,
                request.Holds),
            executionContext,
            cancellationToken);
        return Results.Ok(job);
    }

    [MapGet]
    public static async Task<IResult> List(
        KernelJobsCoordinator coordinator,
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        var jobs = await coordinator.ListAsync(
            KernelHostEndpoints.CreateExecutionContext(context),
            cancellationToken);
        return Results.Ok(jobs);
    }

    [MapGet("/{jobId:guid}")]
    public static async Task<IResult> Get(
        Guid jobId,
        KernelJobsCoordinator coordinator,
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        var job = await coordinator.GetAsync(
            jobId,
            KernelHostEndpoints.CreateExecutionContext(context),
            cancellationToken);
        return job is null ? Results.NotFound() : Results.Ok(job);
    }

    [MapPost("/{jobId:guid}/dispatch")]
    public static async Task<IResult> Dispatch(
        Guid jobId,
        KernelJobsCoordinator coordinator,
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        var result = await coordinator.DispatchAsync(
            jobId,
            KernelHostEndpoints.CreateExecutionContext(context),
            cancellationToken);
        return Results.Ok(result);
    }

    [MapPost("/{jobId:guid}/cancel")]
    public static Task<IResult> Cancel(
        Guid jobId,
        KernelJobsCoordinator coordinator,
        HttpContext context,
        CancellationToken cancellationToken = default) =>
        RunTransitionAsync(
            jobId,
            coordinator.CancelAsync,
            context,
            cancellationToken);

    [MapPost("/{jobId:guid}/pause")]
    public static Task<IResult> Pause(
        Guid jobId,
        KernelJobsCoordinator coordinator,
        HttpContext context,
        CancellationToken cancellationToken = default) =>
        RunTransitionAsync(
            jobId,
            coordinator.PauseAsync,
            context,
            cancellationToken);

    [MapPost("/{jobId:guid}/stop")]
    public static Task<IResult> Stop(
        Guid jobId,
        KernelJobsCoordinator coordinator,
        HttpContext context,
        CancellationToken cancellationToken = default) =>
        RunTransitionAsync(
            jobId,
            coordinator.StopAsync,
            context,
            cancellationToken);

    [MapPost("/{jobId:guid}/resume")]
    public static Task<IResult> Resume(
        Guid jobId,
        KernelJobsCoordinator coordinator,
        HttpContext context,
        CancellationToken cancellationToken = default) =>
        RunTransitionAsync(
            jobId,
            coordinator.ResumeAsync,
            context,
            cancellationToken);

    [MapPost("/{jobId:guid}/recover")]
    public static Task<IResult> Recover(
        Guid jobId,
        KernelJobsCoordinator coordinator,
        HttpContext context,
        CancellationToken cancellationToken = default) =>
        RunTransitionAsync(
            jobId,
            coordinator.RecoverAsync,
            context,
            cancellationToken);

    [MapPost("/{jobId:guid}/resolve-hold")]
    public static Task<IResult> ResolveHold(
        Guid jobId,
        KernelJobsCoordinator coordinator,
        HttpContext context,
        CancellationToken cancellationToken = default) =>
        RunTransitionAsync(
            jobId,
            coordinator.ResolveHoldAsync,
            context,
            cancellationToken);

    [MapPost("/{jobId:guid}/retry")]
    public static async Task<IResult> Retry(
        Guid jobId,
        KernelJobsCoordinator coordinator,
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        var job = await coordinator.RetryAsync<JobPayloadEnvelope>(
            jobId,
            KernelHostEndpoints.CreateExecutionContext(context),
            cancellationToken);
        return Results.Ok(job);
    }

    [MapDelete("/{jobId:guid}")]
    public static async Task<IResult> Delete(
        Guid jobId,
        KernelJobsCoordinator coordinator,
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        var deleted = await coordinator.DeleteAsync(
            jobId,
            KernelHostEndpoints.CreateExecutionContext(context),
            cancellationToken);
        return deleted ? Results.NoContent() : Results.NotFound();
    }

    [MapGet("/{jobId:guid}/progress")]
    public static async Task<IResult> Progress(
        Guid jobId,
        KernelJobsCoordinator coordinator,
        HttpContext context,
        CancellationToken cancellationToken = default) =>
        Results.Ok(await coordinator.ReadProgressAsync(
            jobId,
            KernelHostEndpoints.CreateExecutionContext(context),
            cancellationToken));

    [MapGet("/{jobId:guid}/attempts")]
    public static async Task<IResult> Attempts(
        Guid jobId,
        KernelJobsCoordinator coordinator,
        HttpContext context,
        CancellationToken cancellationToken = default) =>
        Results.Ok(await coordinator.ReadAttemptsAsync(
            jobId,
            KernelHostEndpoints.CreateExecutionContext(context),
            cancellationToken));

    [MapGet("/{jobId:guid}/artifact")]
    public static async Task<IResult> Artifact(
        Guid jobId,
        KernelJobsCoordinator coordinator,
        HttpContext context,
        CancellationToken cancellationToken = default) =>
        Results.Ok(await coordinator.ReadArtifactAsync(
            jobId,
            KernelHostEndpoints.CreateExecutionContext(context),
            cancellationToken));

    private static async Task<IResult> RunTransitionAsync(
        Guid jobId,
        Func<Guid, KernelActionExecutionContext, CancellationToken, ValueTask<JobDocument>> transition,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var job = await transition(
            jobId,
            KernelHostEndpoints.CreateExecutionContext(context),
            cancellationToken);
        return Results.Ok(job);
    }
}

public sealed record KernelJobSubmissionRequest(
    string ActionKey,
    JobPayloadEnvelope Input,
    Guid? ConversationId = null,
    IReadOnlyList<ToolHoldRequirement>? Holds = null);
