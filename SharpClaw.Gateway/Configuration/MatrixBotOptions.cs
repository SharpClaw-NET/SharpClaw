namespace SharpClaw.Gateway.Configuration;

/// <summary>
/// Configuration for the Matrix bot integration.
/// Loaded from the <c>Gateway:Bots:Matrix</c> configuration section.
/// <para>
/// The homeserver URL lives in the gateway <c>.env</c> because it is a
/// deployment concern. The access token is stored in the core database
/// as <c>BotToken</c>.
/// </para>
/// </summary>
public sealed class MatrixBotOptions
{
    public const string SectionName = "Gateway:Bots:Matrix";

    /// <summary>Whether the Matrix bot is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Base URL of the Matrix homeserver (e.g. <c>https://matrix.org</c>).
    /// </summary>
    public string? HomeserverUrl { get; set; }
}
