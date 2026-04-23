namespace SharpClaw.Gateway.Infrastructure;

/// <summary>
/// Configuration for connecting to the internal SharpClaw Application API.
/// </summary>
public sealed class InternalApiOptions
{
    public const string SectionName = "InternalApi";

    /// <summary>Base URL of the internal API (default: the localhost endpoint).</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:48923";

    /// <summary>
    /// Timeout in seconds for requests to the internal API.
    /// Agent tool-call chains (wait, screenshot, click, type) can take
    /// several minutes, so the default is generous. Default: 300 (5 min).
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Explicit API key. When <c>null</c>, the key is read from the well-known
    /// file written by the internal API's <c>ApiKeyProvider</c>.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Explicit path to the backend runtime API key file.
    /// </summary>
    public string? ApiKeyFilePath { get; set; }

    /// <summary>
    /// Gateway service token for authenticating with the core API without
    /// a user JWT. When <c>null</c>, read from the <c>.gateway-token</c> file.
    /// </summary>
    public string? GatewayToken { get; set; }

    /// <summary>
    /// Explicit path to the backend runtime gateway token file.
    /// </summary>
    public string? GatewayTokenFilePath { get; set; }
}
