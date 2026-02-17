namespace SharpClaw.PublicAPI.Infrastructure;

/// <summary>
/// Configuration for connecting to the internal SharpClaw Application API.
/// </summary>
public sealed class InternalApiOptions
{
    public const string SectionName = "InternalApi";

    /// <summary>Base URL of the internal API (default: the localhost endpoint).</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:48923";

    /// <summary>
    /// Explicit API key. When <c>null</c>, the key is read from the well-known
    /// file written by the internal API's <c>ApiKeyProvider</c>.
    /// </summary>
    public string? ApiKey { get; set; }
}
