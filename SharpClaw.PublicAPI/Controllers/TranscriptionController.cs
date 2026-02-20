using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Transcription;
using SharpClaw.PublicAPI.Infrastructure;

namespace SharpClaw.PublicAPI.Controllers;

/// <summary>
/// Public proxy for transcription job operations. Routes to the internal
/// agent jobs API using transcription action types.
/// </summary>
[ApiController]
[Route("api/transcription")]
[EnableRateLimiting(Security.RateLimiterConfiguration.GlobalPolicy)]
public class TranscriptionController(InternalApiClient api) : ControllerBase
{
    [HttpGet("{jobId:guid}")]
    public async Task<IActionResult> GetById(Guid jobId, CancellationToken ct)
    {
        try
        {
            // Job endpoints use agentId=empty for direct job lookup from CLI
            var result = await api.GetAsync<AgentJobResponse>($"/agents/00000000-0000-0000-0000-000000000000/jobs/{jobId}", ct);
            return result is not null ? Ok(result) : NotFound();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return NotFound(new { error = "Transcription job not found." });
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Internal service unavailable." });
        }
    }

    [HttpPost("{jobId:guid}/stop")]
    public async Task<IActionResult> Stop(Guid jobId, CancellationToken ct)
    {
        try
        {
            var result = await api.PostAsync<object, AgentJobResponse>(
                $"/agents/00000000-0000-0000-0000-000000000000/jobs/{jobId}/stop", new { }, ct);
            return result is not null ? Ok(result) : NotFound();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return NotFound(new { error = "Transcription job not found." });
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Internal service unavailable." });
        }
    }

    [HttpPost("{jobId:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid jobId, CancellationToken ct)
    {
        try
        {
            var result = await api.PostAsync<object, AgentJobResponse>(
                $"/agents/00000000-0000-0000-0000-000000000000/jobs/{jobId}/cancel", new { }, ct);
            return result is not null ? Ok(result) : NotFound();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return NotFound(new { error = "Transcription job not found." });
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Internal service unavailable." });
        }
    }

    [HttpGet("{jobId:guid}/segments")]
    public async Task<IActionResult> GetSegments(
        Guid jobId, [FromQuery] DateTimeOffset? since, CancellationToken ct)
    {
        try
        {
            var path = since.HasValue
                ? $"/agents/00000000-0000-0000-0000-000000000000/jobs/{jobId}/segments?since={since:O}"
                : $"/agents/00000000-0000-0000-0000-000000000000/jobs/{jobId}/segments";
            var result = await api.GetAsync<IReadOnlyList<TranscriptionSegmentResponse>>(path, ct);
            return Ok(result);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return NotFound(new { error = "Transcription job not found." });
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Internal service unavailable." });
        }
    }
}
