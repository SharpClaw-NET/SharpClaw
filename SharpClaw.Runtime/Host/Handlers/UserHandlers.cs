using Microsoft.AspNetCore.Http;
using SharpClaw.Runtime.Host.Routing;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Runtime.BLL.Services.Auth;
using SharpClaw.Contracts.DTOs.Users;

namespace SharpClaw.Runtime.Host.Handlers;

[RouteGroup("/users")]
public static class UserHandlers
{
    /// <summary>
    /// Lists all registered users. Requires the caller to be a user admin.
    /// </summary>
    [MapGet]
    public static async Task<IResult> List(SessionService session, AuthService auth)
    {
        if (session.UserId is not { } userId)
            return Results.Unauthorized();

        try
        {
            var users = await auth.ListUsersAsync(userId);
            return Results.Ok(users);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
    }

    /// <summary>
    /// Assigns a role to a user. Requires the caller to be a user admin.
    /// Pass <c>Guid.Empty</c> as <c>roleId</c> to remove the role.
    /// </summary>
    [MapPut("/{id:guid}/role")]
    public static async Task<IResult> AssignRole(
        Guid id, SetUserRoleRequest request, SessionService session, AuthService auth)
    {
        if (session.UserId is not { } callerId)
            return Results.Unauthorized();

        try
        {
            var result = await auth.SetUserRoleAsync(id, request.RoleId, callerId);
            return result is not null ? Results.Ok(result) : Results.NotFound();
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}
