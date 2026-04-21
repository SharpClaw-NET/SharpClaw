namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// Thrown by <see cref="LocalInferenceApiClient"/> when the model emits
/// output that the grammar-constrained envelope parser cannot decode —
/// typically because the GBNF sampler was defeated on a heavily
/// quantised checkpoint. Replaces the previous canned-apology string
/// (finding L-017) so callers can surface the failure as an error
/// rather than silently treating it as a successful empty response.
/// </summary>
public sealed class LocalInferenceEnvelopeException : Exception
{
    /// <summary>
    /// The first 200 characters of the malformed payload, retained for
    /// log/diagnostic display.
    /// </summary>
    public string PayloadPreview { get; }

    public LocalInferenceEnvelopeException(string payloadPreview, Exception inner)
        : base(
            "Local model returned malformed envelope output. The quantization level may be too aggressive for reliable tool calling. Try a higher-bit-depth variant of this model.",
            inner)
    {
        PayloadPreview = payloadPreview;
    }
}
