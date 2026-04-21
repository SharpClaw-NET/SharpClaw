namespace SharpClaw.Application.Core.LocalInference;

/// <summary>
/// Tracks which tool-calling implementation is active on
/// <see cref="Clients.LocalInferenceApiClient"/>.
/// </summary>
internal enum LocalToolCallingMode
{
    None,

    /// <summary>
    /// Legacy implementation: prompt-injected JSON convention and
    /// text scanning. <c>SupportsNativeToolCalling</c> is <see langword="false"/>
    /// while this mode is active.
    /// </summary>
    PromptText,

    /// <summary>
    /// GBNF-constrained envelope with structured history round-tripping.
    /// <c>SupportsNativeToolCalling</c> is <see langword="true"/> when this mode
    /// is active.
    /// </summary>
    StructuredGrammar,
}
