using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SharpClaw.Contracts.DTOs.Auth;
using SharpClaw.PublicAPI.Infrastructure;

namespace SharpClaw.PublicAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting(Security.RateLimiterConfiguration.AuthPolicy)]
public class AuthController(InternalApiClient api) : ControllerBase
{
    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        try
        {
            var result = await api.PostAsync<RegisterRequest, object>("/auth/register", request, ct);
            return Ok(result);
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Internal service unavailable." });
        }
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        try
        {
            var result = await api.PostAsync<LoginRequest, LoginResponse>("/auth/login", request, ct);
            return result is not null ? Ok(result) : Unauthorized();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return Unauthorized(new { error = "Invalid credentials." });
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Internal service unavailable." });
        }
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(RefreshRequest request, CancellationToken ct)
    {
        try
        {
            var result = await api.PostAsync<RefreshRequest, LoginResponse>("/auth/refresh", request, ct);
            return result is not null ? Ok(result) : Unauthorized();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return Unauthorized(new { error = "Invalid or expired refresh token." });
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Internal service unavailable." });
        }
    }
}
