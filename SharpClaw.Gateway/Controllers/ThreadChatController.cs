using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Controllers;

/// <summary>
/// Public proxy for thread-scoped chat operations.
/// </summary>
[ApiController]
[Route("api/channels/{channelId:guid}/chat/threads/{threadId:guid}")]
[EnableRateLimiting(Security.RateLimiterConfiguration.ChatPolicy)]
public class ThreadChatController(InternalApiClient api) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Send(
        Guid channelId, Guid threadId, ChatRequest request, CancellationToken ct)
    {
        try
        {
            var result = await api.PostAsync<ChatRequest, ChatResponse>(
                $"/channels/{channelId}/chat/threads/{threadId}", request, ct);
            return Ok(result);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return NotFound(new { error = "Channel or thread not found." });
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            return BadRequest(new { error = "Invalid chat request." });
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Internal service unavailable." });
        }
    }

    [HttpGet]
    public async Task<IActionResult> History(Guid channelId, Guid threadId, CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<IReadOnlyList<ChatMessageResponse>>(
                $"/channels/{channelId}/chat/threads/{threadId}", ct);
            return Ok(result);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return NotFound(new { error = "Channel or thread not found." });
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Internal service unavailable." });
        }
    }

    [HttpGet("cost")]
    public async Task<IActionResult> Cost(Guid channelId, Guid threadId, CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<ThreadCostResponse>(
                $"/channels/{channelId}/chat/threads/{threadId}/cost", ct);
            return result is not null ? Ok(result) : NotFound();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return NotFound(new { error = "Channel or thread not found." });
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Internal service unavailable." });
        }
    }
}
