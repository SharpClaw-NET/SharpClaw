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

    [MapPost]
    public static async Task<IResult> Create(
        CreateRoleRequest request, RoleService svc)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.BadRequest("Role name is required.");

        var result = await svc.CreateAsync(request.Name);
        return Results.Created($"/roles/{result.Id}", result);
    }

    [MapGet("/{id:guid}")]
    public static async Task<IResult> GetById(Guid id, RoleService svc)
    {
        var result = await svc.GetByIdAsync(id);
        return result is not null ? Results.Ok(result) : Results.NotFound();
    }

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
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    [MapPut("/{id:guid}/name")]
    public static async Task<IResult> Rename(Guid id, RenameRoleRequest request, RoleService svc)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.BadRequest("Role name is required.");

        try
        {
            var result = await svc.RenameAsync(id, request.Name);
            return result is not null ? Results.Ok(result) : Results.NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    [MapDelete("/{id:guid}")]
    public static async Task<IResult> Delete(Guid id, RoleService svc)
    {
        var deleted = await svc.DeleteAsync(id);
        return deleted ? Results.NoContent() : Results.NotFound();
    }
}
