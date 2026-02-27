using Microsoft.AspNetCore.Http;
using SharpClaw.Application.API.Routing;
using SharpClaw.Application.Services;
using SharpClaw.Application.Services.Auth;
using SharpClaw.Contracts.DTOs.Roles;

namespace SharpClaw.Application.API.Handlers;

[RouteGroup("/roles")]
public static class RoleHandlers
{
    [MapGet]
    public static async Task<IResult> List(RoleService svc)
        => Results.Ok(await svc.ListAsync());

    [MapGet("/{id:guid}/permissions")]
    public static async Task<IResult> GetPermissions(Guid id, RoleService svc)
    {
        var result = await svc.GetPermissionsAsync(id);
        return result is not null ? Results.Ok(result) : Results.NotFound();
    }

    [MapPut("/{id:guid}/permissions")]
    public static async Task<IResult> SetPermissions(
        Guid id, SetRolePermissionsRequest request,
        RoleService svc, SessionService session)
    {
        if (session.UserId is not { } userId)
            return Results.Unauthorized();

        try
        {
            var result = await svc.SetPermissionsAsync(id, request, userId);
            return result is not null ? Results.Ok(result) : Results.NotFound();
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden);
        }
    }
}
