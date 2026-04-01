using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Bots;

/// <summary>
/// Hosted service that manages the Slack bot lifecycle.
/// Fetches configuration from the core API (bot token, channel, thread),
/// merges Slack-specific settings from the gateway <c>.env</c>
/// (signing secret), validates the token via <c>auth.test</c>,
/// and publishes the combined result to <see cref="SlackBotState"/>
/// so the webhook proxy can process incoming events.
/// <para>
/// Automatically reloads configuration when <see cref="BotReloadSignal"/>
/// fires (e.g. after bot settings are changed via the gateway).
/// </para>
/// </summary>
public sealed class SlackBotService : BackgroundService
{
    private readonly InternalApiClient _coreApi;
    private readonly ILogger<SlackBotService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BotReloadSignal _reloadSignal;
    private readonly SlackBotState _state;
    private readonly IOptionsMonitor<SlackBotOptions> _options;

    public SlackBotService(
        InternalApiClient coreApi,
        ILogger<SlackBotService> logger,
        IHttpClientFactory httpClientFactory,
        BotReloadSignal reloadSignal,
        SlackBotState state,
        IOptionsMonitor<SlackBotOptions> options)
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
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            BotConfigResponse? config;
            try
            {
                config = await _coreApi.GetAsync<BotConfigResponse>(
                    "/bots/config/slack", stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to fetch Slack bot config from core API. Retrying on reload...");
                config = null;
            }

            var opts = _options.CurrentValue;

            if (config is null || !config.Enabled
                || string.IsNullOrWhiteSpace(config.BotToken))
            {
                _state.Update(null);

                if (config is { Enabled: true } && string.IsNullOrWhiteSpace(config.BotToken))
                    _logger.LogWarning(
                        "Slack bot is enabled but no bot token is configured.");
                else
                    _logger.LogInformation(
                        "Slack bot is disabled or not configured. Waiting for reload signal...");

                try { await _reloadSignal.WaitAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            if (string.IsNullOrWhiteSpace(opts.SigningSecret))
            {
                _state.Update(null);
                _logger.LogWarning(
                    "Slack bot is enabled but no SigningSecret is configured in gateway .env.");
                try { await _reloadSignal.WaitAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            if (!await ValidateBotTokenAsync(config.BotToken, stoppingToken))
            {
                _state.Update(null);
                _logger.LogError(
                    "Slack bot token validation failed. Waiting for reload signal...");
                try { await _reloadSignal.WaitAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            _state.Update(new SlackBotState.SlackConfig(
                config.Enabled,
                config.BotToken,
                opts.SigningSecret,
                config.DefaultChannelId,
                config.DefaultThreadId));

            _logger.LogInformation(
                "Slack bot configuration loaded. Webhook ready at /api/bots/slack/events.");

            try { await _reloadSignal.WaitAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }

            _logger.LogInformation("Slack bot reloading configuration...");
        }

        _state.Update(null);
        _logger.LogInformation("Slack bot stopped.");
    }

    private async Task<bool> ValidateBotTokenAsync(string token, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("SlackBot");
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var response = await client.GetAsync(
                "https://slack.com/api/auth.test", ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Slack auth.test returned {Status}.", response.StatusCode);
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogInformation("Slack bot token validated: {Response}", body);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate Slack bot token.");
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
