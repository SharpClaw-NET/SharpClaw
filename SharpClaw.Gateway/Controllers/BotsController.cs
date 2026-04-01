using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SharpClaw.Contracts.Enums;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Controllers;

/// <summary>
/// Proxies bot integration management to the core API.
/// GETs are forwarded directly; mutations (PUT) are routed through the
/// <see cref="RequestQueueService"/> for sequential processing.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting(Security.RateLimiterConfiguration.GlobalPolicy)]
public class BotsController(GatewayRequestDispatcher dispatcher, BotReloadSignal reloadSignal) : ControllerBase
{
    /// <summary>
    /// Lists all bot integrations from the core database.
    /// </summary>
    [HttpGet("list")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        try
        {
            var bots = await dispatcher.GetAsync<JsonElement>("/bots", ct);
            return Ok(bots);
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = $"Core API unreachable: {ex.Message}" });
        }
    }

    /// <summary>
    /// Creates a new bot integration in the core database.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] JsonElement body, CancellationToken ct)
    {
        var result = await dispatcher.PostAsync("/bots", body, ct);
        if (result.IsSuccess) reloadSignal.Signal();
        return StatusCode((int)result.StatusCode, result.IsSuccess
            ? DeserializeOrRaw(result.JsonBody)
            : new { error = result.Error });
    }

    /// <summary>
    /// Returns a single bot integration by id.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        try
        {
            var bot = await dispatcher.GetAsync<JsonElement>($"/bots/{id}", ct);
            return Ok(bot);
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = $"Core API unreachable: {ex.Message}" });
        }
    }

    /// <summary>
    /// Updates a single bot integration by id.
    /// Routed through the request queue for sequential processing.
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] JsonElement body, CancellationToken ct)
    {
        var result = await dispatcher.PutAsync($"/bots/{id}", body, ct);
        if (result.IsSuccess) reloadSignal.Signal();
        return StatusCode((int)result.StatusCode, result.IsSuccess
            ? DeserializeOrRaw(result.JsonBody)
            : new { error = result.Error });
    }

    /// <summary>
    /// Deletes a bot integration by id.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var result = await dispatcher.DeleteAsync($"/bots/{id}", ct);
        if (result.IsSuccess) reloadSignal.Signal();
        return StatusCode((int)result.StatusCode, result.IsSuccess
            ? DeserializeOrRaw(result.JsonBody)
            : new { error = result.Error });
    }

    /// <summary>
    /// Fires the bot reload signal so running bot services re-fetch their
    /// configuration. Called by clients that mutate bot rows via the core
    /// API directly (bypassing the gateway proxy).
    /// </summary>
    [HttpPost("reload")]
    public IActionResult Reload()
    {
        reloadSignal.Signal();
        return Ok(new { reloaded = true });
    }

    /// <summary>
    /// Returns a combined status/config view for quick reachability checks.
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        try
        {
            var result = new Dictionary<string, object>();
            foreach (var type in Enum.GetValues<BotType>())
            {
                var name = type.ToString().ToLowerInvariant();
                var cfg = await dispatcher.GetAsync<BotConfigDto>($"/bots/config/{name}", ct);
                result[name] = new { enabled = cfg?.Enabled ?? false, configured = !string.IsNullOrWhiteSpace(cfg?.BotToken) };
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = $"Core API unreachable: {ex.Message}" });
        }
    }

    private static object DeserializeOrRaw(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new { };
        try { return JsonSerializer.Deserialize<JsonElement>(json); }
        catch { return json; }
    }
}

// ── DTOs for core API responses ──────────────────────────────────

internal sealed class BotConfigDto
{
    public bool Enabled { get; set; }
    public string? BotToken { get; set; }
    public Guid? DefaultChannelId { get; set; }
    public Guid? DefaultThreadId { get; set; }
}
