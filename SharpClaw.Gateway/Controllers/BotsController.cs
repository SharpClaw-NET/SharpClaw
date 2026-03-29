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

    /// <summary>
    /// Returns the current bot configuration (tokens included — core decrypts them).
    /// </summary>
    [HttpGet("config")]
    public async Task<IActionResult> GetConfig(CancellationToken ct)
    {
        try
        {
            var result = new Dictionary<string, object>();
            foreach (var type in Enum.GetValues<BotType>())
            {
                var name = type.ToString().ToLowerInvariant();
                var cfg = await dispatcher.GetAsync<BotConfigDto>($"/bots/config/{name}", ct);
                result[name] = new { enabled = cfg?.Enabled ?? false, botToken = cfg?.BotToken ?? "" };
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = $"Core API unreachable: {ex.Message}" });
        }
    }

    /// <summary>
    /// Updates bot configuration via the core API.
    /// Accepts the <see cref="BotConfigRequest"/> shape with a property per bot type.
    /// </summary>
    [HttpPut("config")]
    public async Task<IActionResult> UpdateConfig([FromBody] BotConfigRequest request, CancellationToken ct)
    {
        try
        {
            var entries = new (string Type, BotConfigEntry? Entry)[]
            {
                ("telegram", request.Telegram),
                ("discord", request.Discord),
                ("whatsapp", request.WhatsApp),
                ("slack", request.Slack),
                ("matrix", request.Matrix),
                ("signal", request.Signal),
                ("email", request.Email),
                ("teams", request.Teams),
            };

            foreach (var (type, entry) in entries)
            {
                if (entry is not null)
                    await UpdateBotByTypeAsync(type, entry, ct);
            }

            return Ok(new { saved = true });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = $"Core API error: {ex.Message}" });
        }
    }

    private async Task UpdateBotByTypeAsync(string type, BotConfigEntry entry, CancellationToken ct)
    {
        // Resolve the bot id via a direct GET (reads bypass the queue)
        var bot = await dispatcher.GetAsync<BotIntegrationDto>($"/bots/type/{type}", ct)
            ?? throw new InvalidOperationException($"Bot integration for '{type}' not found in core.");

        var update = new { enabled = entry.Enabled, botToken = entry.BotToken };
        var result = await dispatcher.PutAsync($"/bots/{bot.Id}", update, ct);
        if (!result.IsSuccess)
            throw new HttpRequestException($"Core PUT /bots/{bot.Id} failed: {result.Error}");
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

internal sealed class BotIntegrationDto
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public string? BotType { get; set; }
    public bool Enabled { get; set; }
    public bool HasBotToken { get; set; }
    public Guid? DefaultChannelId { get; set; }
    public Guid? DefaultThreadId { get; set; }
}

// ── Legacy request DTOs (kept for backward compat) ───────────────

public sealed class BotConfigEntry
{
    public bool Enabled { get; set; }
    public string? BotToken { get; set; }
}

public sealed class BotConfigRequest
{
    public BotConfigEntry? Telegram { get; set; }
    public BotConfigEntry? Discord { get; set; }
    public BotConfigEntry? WhatsApp { get; set; }
    public BotConfigEntry? Slack { get; set; }
    public BotConfigEntry? Matrix { get; set; }
    public BotConfigEntry? Signal { get; set; }
    public BotConfigEntry? Email { get; set; }
    public BotConfigEntry? Teams { get; set; }
}
