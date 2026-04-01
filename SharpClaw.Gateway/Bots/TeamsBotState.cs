namespace SharpClaw.Gateway.Bots;

/// <summary>
/// Singleton holding the current validated Microsoft Teams bot configuration.
/// Populated by <see cref="TeamsBotService"/> and consumed by the
/// webhook proxy endpoints.
/// </summary>
public sealed class TeamsBotState
{
    private volatile TeamsConfig? _config;

    /// <summary>Current validated configuration, or <c>null</c> if not ready.</summary>
    public TeamsConfig? Current => _config;

    /// <summary>
    /// <c>true</c> when the bot is enabled and all required fields
    /// (bot token / client secret, app ID) are present.
    /// </summary>
    public bool IsConfigured => _config is { Enabled: true }
        && !string.IsNullOrWhiteSpace(_config.BotToken)
        && !string.IsNullOrWhiteSpace(_config.AppId);

    /// <summary>Replace the current configuration snapshot.</summary>
    public void Update(TeamsConfig? config) => _config = config;

    /// <summary>Immutable configuration snapshot.</summary>
    public sealed record TeamsConfig(
        bool Enabled,
        string? BotToken,
        string? AppId,
        Guid? DefaultChannelId,
        Guid? DefaultThreadId);
}
