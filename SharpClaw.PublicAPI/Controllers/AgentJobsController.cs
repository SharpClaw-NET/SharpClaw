using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Agents;
using SharpClaw.PublicAPI.Infrastructure;

namespace SharpClaw.PublicAPI.Controllers;

[ApiController]
[Route("api/agents/{agentId:guid}/jobs")]
[EnableRateLimiting(Security.RateLimiterConfiguration.GlobalPolicy)]
public class AgentJobsController(InternalApiClient api) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Submit(
        Guid agentId, SubmitAgentJobRequest request, CancellationToken ct)
    {
        try
        {
            var result = await api.PostAsync<SubmitAgentJobRequest, AgentJobResponse>(
                $"/agents/{agentId}/jobs", request, ct);
            return Ok(result);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            return BadRequest(new { error = "Invalid job request." });
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Internal service unavailable." });
        }
    }

    [HttpGet]
    public async Task<IActionResult> List(Guid agentId, CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<IReadOnlyList<AgentJobResponse>>(
                $"/agents/{agentId}/jobs", ct);
            return Ok(result);
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Internal service unavailable." });
        }
    }

    [HttpGet("{jobId:guid}")]
    public async Task<IActionResult> GetById(Guid agentId, Guid jobId, CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<AgentJobResponse>(
                $"/agents/{agentId}/jobs/{jobId}", ct);
            return result is not null ? Ok(result) : NotFound();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return NotFound(new { error = "Job not found." });
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Internal service unavailable." });
        }
    }

    [HttpPost("{jobId:guid}/approve")]
    public async Task<IActionResult> Approve(
        Guid agentId, Guid jobId, ApproveAgentJobRequest request, CancellationToken ct)
    {
        try
        {
            var result = await api.PostAsync<ApproveAgentJobRequest, AgentJobResponse>(
                $"/agents/{agentId}/jobs/{jobId}/approve", request, ct);
            return result is not null ? Ok(result) : NotFound();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return NotFound(new { error = "Job not found." });
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Internal service unavailable." });
        }
    }

    [HttpPost("{jobId:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid agentId, Guid jobId, CancellationToken ct)
    {
        try
        {
            var result = await api.PostAsync<object, AgentJobResponse>(
                $"/agents/{agentId}/jobs/{jobId}/cancel", new { }, ct);
            return result is not null ? Ok(result) : NotFound();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return NotFound(new { error = "Job not found." });
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Internal service unavailable." });
        }
    }
}
