namespace SharpClaw.Gateway.Configuration;

/// <summary>
/// Configuration for the Microsoft Teams bot integration.
/// Loaded from the <c>Gateway:Bots:Teams</c> configuration section.
/// <para>
/// The App ID lives in the gateway <c>.env</c>. The app password
/// (client secret) is stored in the core database as <c>BotToken</c>.
/// Uses the Bot Framework v3 REST API with Azure AD OAuth2
/// client-credentials flow for outbound replies.
/// </para>
/// </summary>
public sealed class TeamsBotOptions
{
    public const string SectionName = "Gateway:Bots:Teams";

    /// <summary>Whether the Teams bot is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Microsoft App ID (GUID) from the Azure Bot registration.
    /// </summary>
    public string? AppId { get; set; }
}
