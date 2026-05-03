using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Services;

namespace SharpClaw.Modules.AgentOrchestration.Handlers;

// ═══════════════════════════════════════════════════════════════════
// Scheduled jobs   /scheduled-jobs
// Owned by the AgentOrchestration module. Delegates to the
// host-supplied IScheduledJobService contract.
// ═══════════════════════════════════════════════════════════════════

public static class ScheduledJobEndpoints
{
    public static IEndpointRouteBuilder MapScheduledJobEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/scheduled-jobs");

        group.MapPost("/", async (
            CreateScheduledJobRequest request, IScheduledJobService svc, CancellationToken ct) =>
        {
            try
            {
                var result = await svc.CreateAsync(request, ct);
                return Results.Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.UnprocessableEntity(new { error = ex.Message });
            }
        });

        group.MapGet("/", async (IScheduledJobService svc, CancellationToken ct)
            => Results.Ok(await svc.ListAsync(ct)));

        group.MapGet("/{jobId:guid}", async (Guid jobId, IScheduledJobService svc, CancellationToken ct) =>
        {
            var job = await svc.GetByIdAsync(jobId, ct);
            return job is not null ? Results.Ok(job) : Results.NotFound();
        });

        group.MapPut("/{jobId:guid}", async (
            Guid jobId, UpdateScheduledJobRequest request,
            IScheduledJobService svc, CancellationToken ct) =>
        {
            try
            {
                var result = await svc.UpdateAsync(jobId, request, ct);
                return result is not null ? Results.Ok(result) : Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.UnprocessableEntity(new { error = ex.Message });
            }
        });

        group.MapDelete("/{jobId:guid}", async (
            Guid jobId, IScheduledJobService svc, CancellationToken ct)
            => await svc.DeleteAsync(jobId, ct) ? Results.NoContent() : Results.NotFound());

        // ── Pause / Resume ─────────────────────────────────────────

        group.MapPost("/{jobId:guid}/pause", async (
            Guid jobId, IScheduledJobService svc, CancellationToken ct) =>
        {
            var result = await svc.PauseAsync(jobId, ct);
            return result is not null ? Results.Ok(result) : Results.NotFound();
        });

        group.MapPost("/{jobId:guid}/resume", async (
            Guid jobId, IScheduledJobService svc, CancellationToken ct) =>
        {
            var result = await svc.ResumeAsync(jobId, ct);
            return result is not null ? Results.Ok(result) : Results.NotFound();
        });

        // ── Preview endpoints ──────────────────────────────────────

        group.MapGet("/{jobId:guid}/preview", async (
            Guid jobId, IScheduledJobService svc, CancellationToken ct, int count = 10) =>
        {
            count = count <= 0 ? 10 : Math.Min(count, 100);
            var result = await svc.PreviewJobAsync(jobId, count, ct);
            return result is not null ? Results.Ok(result) : Results.NotFound();
        });

        group.MapGet("/preview", (
            IScheduledJobService svc, string expression, string? timezone = null, int count = 10) =>
        {
            count = count <= 0 ? 10 : Math.Min(count, 100);
            try
            {
                var result = svc.PreviewExpression(expression, timezone, count);
                return Results.Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.UnprocessableEntity(new { error = ex.Message });
            }
        });

        return routes;
    }
}
