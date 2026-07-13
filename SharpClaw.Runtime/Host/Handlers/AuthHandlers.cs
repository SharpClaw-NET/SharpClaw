using Microsoft.AspNetCore.Http;
using SharpClaw.Runtime.Host.Routing;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Runtime.BLL.Services.Auth;
using SharpClaw.Contracts.DTOs.Agents;
using SharpClaw.Contracts.DTOs.Auth;

namespace SharpClaw.Runtime.Host.Handlers;

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

    [MapPut("/me/role")]
    public static async Task<IResult> AssignSelfRole(
        AssignAgentRoleRequest request, AgentService agentSvc, SessionService session)
    {
        if (session.UserId is null)
            return Results.Unauthorized();

        try
        {
            var result = await agentSvc.AssignUserRoleAsync(request.RoleId);
            return result is not null ? Results.Ok(result) : Results.NotFound();
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
    }
}
