namespace SharpClaw.Contracts.Providers;

/// <summary>
/// A message in a simple (non-tool-aware) conversation history.
/// Optionally carries a base64 image for vision-capable models.
/// </summary>
public sealed record ChatCompletionMessage(string Role, string Content)
{
    /// <summary>
    /// Optional base64-encoded image data (e.g. a screenshot PNG).
    /// When set, providers should include this as a multipart content
    /// block alongside <see cref="Content"/>.
    /// </summary>
    public string? ImageBase64 { get; init; }

    /// <summary>
    /// MIME type of <see cref="ImageBase64"/> (e.g. <c>"image/png"</c>).
    /// </summary>
    public string? ImageMediaType { get; init; }

    public bool HasImage => ImageBase64 is not null;
}
