namespace SharpClaw.Gateway.Bots;

/// <summary>
/// Singleton holding the current validated WhatsApp bot configuration.
/// Populated by <see cref="WhatsAppBotService"/> and consumed by the
/// webhook proxy endpoints.
/// </summary>
public sealed class WhatsAppBotState
{
    private volatile WhatsAppConfig? _config;

    /// <summary>Current validated configuration, or <c>null</c> if not ready.</summary>
    public WhatsAppConfig? Current => _config;

    /// <summary>
    /// <c>true</c> when the bot is enabled and all required fields
    /// (access token, phone number ID) are present.
    /// </summary>
    public bool IsConfigured => _config is { Enabled: true }
        && !string.IsNullOrWhiteSpace(_config.AccessToken)
        && !string.IsNullOrWhiteSpace(_config.PhoneNumberId);

    /// <summary>Replace the current configuration snapshot.</summary>
    public void Update(WhatsAppConfig? config) => _config = config;

    /// <summary>Immutable configuration snapshot.</summary>
    public sealed record WhatsAppConfig(
        bool Enabled,
        string? AccessToken,
        string? PhoneNumberId,
        string? VerifyToken,
        Guid? DefaultChannelId,
        Guid? DefaultThreadId);
}
