namespace SharpClaw.Contracts.Providers;

/// <summary>
/// Thrown by a local-inference provider client when the model emits
/// output that the grammar-constrained envelope parser cannot decode —
/// typically because the GBNF sampler was defeated on a heavily
/// quantised checkpoint. Lives in the contracts layer so the chat
/// pipeline (Core) can catch it without referencing any provider
/// shared library.
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
