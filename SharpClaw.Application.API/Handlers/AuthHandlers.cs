using Microsoft.AspNetCore.Http;
using SharpClaw.Application.API.Routing;
using SharpClaw.Application.Services;
using SharpClaw.Application.Services.Auth;
using SharpClaw.Contracts.DTOs.Auth;

namespace SharpClaw.Application.API.Handlers;

[RouteGroup("/auth")]
public static class AuthHandlers
{
    [MapGet("/me")]
    public static async Task<IResult> Me(SessionService session, AuthService auth)
    {
        if (session.UserId is not { } userId)
            return Results.Unauthorized();

        var me = await auth.GetMeAsync(userId);
        return me is not null ? Results.Ok(me) : Results.Unauthorized();
    }

    [MapPost("/login")]
    public static async Task<IResult> Login(LoginRequest request, AuthService auth)
    {
        var response = await auth.LoginAsync(request);
        return response is not null ? Results.Ok(response) : Results.Unauthorized();
    }

    [MapPost("/refresh")]
    public static async Task<IResult> Refresh(RefreshRequest request, AuthService auth)
    {
        var response = await auth.RefreshAsync(request);
        return response is not null ? Results.Ok(response) : Results.Unauthorized();
    }

    [MapPost("/register")]
    public static async Task<IResult> Register(RegisterRequest request, AuthService auth)
    {
        var user = await auth.RegisterAsync(request.Username, request.Password);
        return Results.Ok(new { user.Id, user.Username });
    }

    [MapPost("/invalidate-access-tokens")]
    public static async Task<IResult> InvalidateAccessTokens(InvalidateRequest request, AuthService auth)
    {
        await auth.InvalidateAccessTokensAsync(request.UserIds);
        return Results.NoContent();
    }

    [MapPost("/invalidate-refresh-tokens")]
    public static async Task<IResult> InvalidateRefreshTokens(InvalidateRequest request, AuthService auth)
    {
        await auth.InvalidateRefreshTokensAsync(request.UserIds);
        return Results.NoContent();
    }
}
