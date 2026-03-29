using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Bots;

/// <summary>
/// Hosted service that manages the Microsoft Teams bot lifecycle.
/// Fetches configuration from the core API (client secret as bot token,
/// channel, thread), merges Teams-specific settings from the gateway
/// <c>.env</c> (App ID), validates credentials via Azure AD OAuth2
/// client-credentials flow, and publishes the combined result to
/// <see cref="TeamsBotState"/> so the webhook proxy can process incoming
/// activities.
/// <para>
/// Automatically reloads configuration when <see cref="BotReloadSignal"/>
/// fires (e.g. after bot settings are changed via the gateway).
/// </para>
/// </summary>
public sealed class TeamsBotService : BackgroundService
{
    private readonly InternalApiClient _coreApi;
    private readonly ILogger<TeamsBotService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BotReloadSignal _reloadSignal;
    private readonly TeamsBotState _state;
    private readonly IOptionsMonitor<TeamsBotOptions> _options;

    public TeamsBotService(
        InternalApiClient coreApi,
        ILogger<TeamsBotService> logger,
        IHttpClientFactory httpClientFactory,
        BotReloadSignal reloadSignal,
        TeamsBotState state,
        IOptionsMonitor<TeamsBotOptions> options)
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
                    "/bots/config/teams", stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to fetch Teams bot config from core API. Retrying on reload...");
                config = null;
            }

            var opts = _options.CurrentValue;

            if (config is null || !config.Enabled
                || string.IsNullOrWhiteSpace(config.BotToken))
            {
                _state.Update(null);

                if (config is { Enabled: true } && string.IsNullOrWhiteSpace(config.BotToken))
                    _logger.LogWarning(
                        "Teams bot is enabled but no client secret (BotToken) is configured.");
                else
                    _logger.LogInformation(
                        "Teams bot is disabled or not configured. Waiting for reload signal...");

                try { await _reloadSignal.WaitAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            if (string.IsNullOrWhiteSpace(opts.AppId))
            {
                _state.Update(null);
                _logger.LogWarning(
                    "Teams bot is enabled but no AppId is configured in gateway .env.");
                try { await _reloadSignal.WaitAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            if (!await ValidateCredentialsAsync(opts.AppId, config.BotToken, stoppingToken))
            {
                _state.Update(null);
                _logger.LogError(
                    "Teams credential validation failed. Waiting for reload signal...");
                try { await _reloadSignal.WaitAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            _state.Update(new TeamsBotState.TeamsConfig(
                config.Enabled,
                config.BotToken,
                opts.AppId,
                config.DefaultChannelId,
                config.DefaultThreadId));

            _logger.LogInformation(
                "Teams bot configuration loaded. Webhook ready at /api/bots/teams/messages.");

            try { await _reloadSignal.WaitAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }

            _logger.LogInformation("Teams bot reloading configuration...");
        }

        _state.Update(null);
        _logger.LogInformation("Teams bot stopped.");
    }

    /// <summary>
    /// Validates credentials by performing an Azure AD OAuth2 client-credentials
    /// token request against the Bot Framework token endpoint.
    /// </summary>
    private async Task<bool> ValidateCredentialsAsync(
        string appId, string clientSecret, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("TeamsBot");

            var form = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("client_id", appId),
                new KeyValuePair<string, string>("client_secret", clientSecret),
                new KeyValuePair<string, string>("scope", "https://api.botframework.com/.default"),
            ]);

            var response = await client.PostAsync(
                "https://login.microsoftonline.com/botframework.com/oauth2/v2.0/token",
                form, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Teams OAuth2 token request returned {Status}.", response.StatusCode);
                return false;
            }

            _logger.LogInformation("Teams bot credentials validated via OAuth2.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate Teams bot credentials.");
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
