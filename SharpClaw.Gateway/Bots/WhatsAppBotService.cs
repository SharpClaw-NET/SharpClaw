using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Bots;

/// <summary>
/// Hosted service that manages the WhatsApp bot lifecycle.
/// Fetches configuration from the core API (access token, channel, thread),
/// merges WhatsApp-specific settings from the gateway <c>.env</c>
/// (phone number ID, verify token), validates the Meta access token,
/// and publishes the combined result to <see cref="WhatsAppBotState"/>
/// so the webhook proxy can process incoming messages.
/// <para>
/// Automatically reloads configuration when <see cref="BotReloadSignal"/>
/// fires (e.g. after bot settings are changed via the gateway).
/// </para>
/// </summary>
public sealed class WhatsAppBotService : BackgroundService
{
    private readonly InternalApiClient _coreApi;
    private readonly ILogger<WhatsAppBotService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BotReloadSignal _reloadSignal;
    private readonly WhatsAppBotState _state;
    private readonly IOptionsMonitor<WhatsAppBotOptions> _options;

    public WhatsAppBotService(
        InternalApiClient coreApi,
        ILogger<WhatsAppBotService> logger,
        IHttpClientFactory httpClientFactory,
        BotReloadSignal reloadSignal,
        WhatsAppBotState state,
        IOptionsMonitor<WhatsAppBotOptions> options)
    {
        _coreApi = coreApi;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _reloadSignal = reloadSignal;
        _state = state;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait briefly for the core API to be ready after startup
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            BotConfigResponse? config;
            try
            {
                config = await _coreApi.GetAsync<BotConfigResponse>(
                    "/bots/config/whatsapp", stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to fetch WhatsApp bot config from core API. Retrying on reload...");
                config = null;
            }

            var opts = _options.CurrentValue;

            if (config is null || !config.Enabled
                || string.IsNullOrWhiteSpace(config.BotToken))
            {
                _state.Update(null);

                if (config is { Enabled: true } && string.IsNullOrWhiteSpace(config.BotToken))
                    _logger.LogWarning(
                        "WhatsApp bot is enabled but no access token is configured.");
                else
                    _logger.LogInformation(
                        "WhatsApp bot is disabled or not configured. Waiting for reload signal...");

                try { await _reloadSignal.WaitAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            if (string.IsNullOrWhiteSpace(opts.PhoneNumberId))
            {
                _state.Update(null);
                _logger.LogWarning(
                    "WhatsApp bot is enabled but no PhoneNumberId is configured in gateway .env.");
                try { await _reloadSignal.WaitAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            // Validate the access token via a lightweight Graph API call
            if (!await ValidateAccessTokenAsync(config.BotToken, stoppingToken))
            {
                _state.Update(null);
                _logger.LogError(
                    "WhatsApp access token validation failed. Waiting for reload signal...");
                try { await _reloadSignal.WaitAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            _state.Update(new WhatsAppBotState.WhatsAppConfig(
                config.Enabled,
                config.BotToken,
                opts.PhoneNumberId,
                opts.VerifyToken,
                config.DefaultChannelId,
                config.DefaultThreadId));

            _logger.LogInformation(
                "WhatsApp bot configuration loaded. Webhook ready at /api/bots/whatsapp/webhook.");

            // Wait for reload signal to re-fetch config
            try { await _reloadSignal.WaitAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }

            _logger.LogInformation("WhatsApp bot reloading configuration...");
        }

        _state.Update(null);
        _logger.LogInformation("WhatsApp bot stopped.");
    }

    private async Task<bool> ValidateAccessTokenAsync(string token, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("WhatsAppBot");
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            var response = await client.GetAsync(
                "https://graph.facebook.com/v21.0/me", ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("WhatsApp token validation returned {Status}.", response.StatusCode);
                return false;
            }

            _logger.LogInformation("WhatsApp access token validated.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate WhatsApp access token.");
            return false;
        }
    }

    private sealed record BotConfigResponse
    {
        public bool Enabled { get; init; }
        public string? BotToken { get; init; }
        public Guid? DefaultChannelId { get; init; }
        public Guid? DefaultThreadId { get; init; }
    }
}
