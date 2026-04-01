using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Controllers;

/// <summary>
/// Exposes gateway operational status including the request queue.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting(Security.RateLimiterConfiguration.GlobalPolicy)]
public class GatewayController(RequestQueueService queue) : ControllerBase
{
    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Returns operational status of the gateway including queue metrics.
    /// </summary>
    [HttpGet("status")]
    public IActionResult Status()
    {
        var metrics = queue.Metrics;
        return Ok(new
        {
            queue = new
            {
                enabled = queue.Enabled,
                pending = queue.PendingCount,
                averageProcessingMs = Math.Round(metrics.AverageProcessingMs, 1),
                processedLastHour = metrics.ProcessedLastHour,
                totalEnqueued = metrics.TotalEnqueued,
                totalProcessed = metrics.TotalProcessed,
            },
        });
    }

    /// <summary>
    /// SSE stream of queue metrics, pushed every 2 seconds.
    /// Returns <c>204 No Content</c> when the queue is disabled.
    /// </summary>
    [HttpGet("queue/stream")]
    public async Task QueueStream(CancellationToken ct)
    {
        if (!queue.Enabled)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        HttpContext.Response.ContentType = "text/event-stream";
        HttpContext.Response.Headers.CacheControl = "no-cache";
        HttpContext.Response.Headers.Connection = "keep-alive";

        while (!ct.IsCancellationRequested)
        {
            var metrics = queue.Metrics;
            var payload = JsonSerializer.Serialize(new
            {
                pending = queue.PendingCount,
                avgMs = Math.Round(metrics.AverageProcessingMs, 1),
                processedLastHour = metrics.ProcessedLastHour,
                totalProcessed = metrics.TotalProcessed,
            }, SseJsonOptions);

            await HttpContext.Response.WriteAsync($"data: {payload}\n\n", ct);
            await HttpContext.Response.Body.FlushAsync(ct);

            await Task.Delay(2000, ct);
        }
    }
}
