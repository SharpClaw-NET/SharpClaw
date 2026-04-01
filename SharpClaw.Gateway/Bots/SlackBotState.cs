namespace SharpClaw.Gateway.Bots;

/// <summary>
/// Singleton holding the current validated Slack bot configuration.
/// Populated by <see cref="SlackBotService"/> and consumed by the
/// webhook proxy endpoints.
/// </summary>
public sealed class SlackBotState
{
    private volatile SlackConfig? _config;

    /// <summary>Current validated configuration, or <c>null</c> if not ready.</summary>
    public SlackConfig? Current => _config;

    /// <summary>
    /// <c>true</c> when the bot is enabled and all required fields
    /// (bot token, signing secret) are present.
    /// </summary>
    public bool IsConfigured => _config is { Enabled: true }
        && !string.IsNullOrWhiteSpace(_config.BotToken)
        && !string.IsNullOrWhiteSpace(_config.SigningSecret);

    /// <summary>Replace the current configuration snapshot.</summary>
    public void Update(SlackConfig? config) => _config = config;

    /// <summary>Immutable configuration snapshot.</summary>
    public sealed record SlackConfig(
        bool Enabled,
        string? BotToken,
        string? SigningSecret,
        Guid? DefaultChannelId,
        Guid? DefaultThreadId);
}
