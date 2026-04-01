using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SharpClaw.Contracts.DTOs.Threads;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Controllers;

[ApiController]
[Route("api/channels/{channelId:guid}/threads")]
[EnableRateLimiting(Security.RateLimiterConfiguration.GlobalPolicy)]
public class ThreadsController(InternalApiClient api) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(Guid channelId, CreateThreadRequest request, CancellationToken ct)
    {
        try
        {
            var result = await api.PostAsync<CreateThreadRequest, ThreadResponse>(
                $"/channels/{channelId}/threads", request, ct);
            return Ok(result);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            return BadRequest(new { error = "Invalid thread request." });
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Internal service unavailable." });
        }
    }

    [HttpGet]
    public async Task<IActionResult> List(Guid channelId, CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<IReadOnlyList<ThreadResponse>>(
                $"/channels/{channelId}/threads", ct);
            return Ok(result);
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Internal service unavailable." });
        }
    }

    [HttpGet("{threadId:guid}")]
    public async Task<IActionResult> GetById(Guid channelId, Guid threadId, CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<ThreadResponse>(
                $"/channels/{channelId}/threads/{threadId}", ct);
            return result is not null ? Ok(result) : NotFound();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return NotFound(new { error = "Thread not found." });
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Internal service unavailable." });
        }
    }

    [HttpPut("{threadId:guid}")]
    public async Task<IActionResult> Update(
        Guid channelId, Guid threadId, UpdateThreadRequest request, CancellationToken ct)
    {
        try
        {
            var result = await api.PutAsync<UpdateThreadRequest, ThreadResponse>(
                $"/channels/{channelId}/threads/{threadId}", request, ct);
            return result is not null ? Ok(result) : NotFound();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return NotFound(new { error = "Thread not found." });
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Internal service unavailable." });
        }
    }

    [HttpDelete("{threadId:guid}")]
    public async Task<IActionResult> Delete(Guid channelId, Guid threadId, CancellationToken ct)
    {
        try
        {
            var success = await api.DeleteAsync(
                $"/channels/{channelId}/threads/{threadId}", ct);
            return success ? NoContent() : NotFound();
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Internal service unavailable." });
        }
    }
}
