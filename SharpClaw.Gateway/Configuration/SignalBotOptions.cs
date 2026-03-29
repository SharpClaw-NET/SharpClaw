namespace SharpClaw.Gateway.Configuration;

/// <summary>
/// Configuration for the Signal bot integration.
/// Loaded from the <c>Gateway:Bots:Signal</c> configuration section.
/// <para>
/// Requires a running <c>signal-cli</c> REST API instance. The API URL
/// and registered phone number live in the gateway <c>.env</c>. No bot
/// token is needed — signal-cli handles authentication locally.
/// </para>
/// </summary>
public sealed class SignalBotOptions
{
    public const string SectionName = "Gateway:Bots:Signal";

    /// <summary>Whether the Signal bot is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Base URL of the signal-cli REST API (e.g. <c>http://localhost:8080</c>).
    /// </summary>
    public string? ApiUrl { get; set; }

    /// <summary>
    /// The registered phone number in E.164 format (e.g. <c>+1234567890</c>).
    /// </summary>
    public string? PhoneNumber { get; set; }
}
