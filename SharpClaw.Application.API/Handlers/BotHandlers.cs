using Microsoft.AspNetCore.Http;
using SharpClaw.Application.API.Routing;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.Bots;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.API.Handlers;

[RouteGroup("/bots")]
public static class BotHandlers
{
    [MapGet]
    public static async Task<IResult> List(BotIntegrationService svc)
        => Results.Ok(await svc.ListAsync());

    [MapPost]
    public static async Task<IResult> Create(CreateBotIntegrationRequest request, BotIntegrationService svc)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.BadRequest(new { error = "Name is required." });

        var bot = await svc.CreateAsync(request);
        return Results.Created($"/bots/{bot.Id}", bot);
    }

    [MapGet("/{id:guid}")]
    public static async Task<IResult> GetById(Guid id, BotIntegrationService svc)
    {
        var bot = await svc.GetByIdAsync(id);
        return bot is not null ? Results.Ok(bot) : Results.NotFound();
    }

    /// <summary>
    /// Get bot config by type name (telegram, discord, whatsapp).
    /// Used by the gateway to fetch config without knowing the DB id.
    /// </summary>
    [MapGet("/type/{type}")]
    public static async Task<IResult> GetByType(string type, BotIntegrationService svc)
    {
        if (!Enum.TryParse<BotType>(type, ignoreCase: true, out var botType))
            return Results.BadRequest(new { error = $"Unknown bot type: {type}" });

        var bot = await svc.GetByTypeAsync(botType);
        return bot is not null ? Results.Ok(bot) : Results.NotFound();
    }

    [MapPut("/{id:guid}")]
    public static async Task<IResult> Update(
        Guid id, UpdateBotIntegrationRequest request, BotIntegrationService svc)
    {
        try
        {
            var bot = await svc.UpdateAsync(id, request);
            return Results.Ok(bot);
        }
        catch (ArgumentException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
    }

    [MapDelete("/{id:guid}")]
    public static async Task<IResult> Delete(Guid id, BotIntegrationService svc)
    {
        var deleted = await svc.DeleteAsync(id);
        return deleted ? Results.Ok(new { deleted = true }) : Results.NotFound();
    }

    /// <summary>
    /// Returns decrypted bot config for a given type.
    /// Intended for internal gateway consumption only — returns the plain token.
    /// </summary>
    [MapGet("/config/{type}")]
    public static async Task<IResult> GetConfig(string type, BotIntegrationService svc)
    {
        if (!Enum.TryParse<BotType>(type, ignoreCase: true, out var botType))
            return Results.BadRequest(new { error = $"Unknown bot type: {type}" });

        var (enabled, token, defaultChannelId, defaultThreadId) = await svc.GetBotConfigAsync(botType);
        return Results.Ok(new { enabled, botToken = token ?? "", defaultChannelId, defaultThreadId });
    }
}
