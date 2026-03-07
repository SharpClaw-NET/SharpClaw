namespace SharpClaw.Contracts.Enums;

/// <summary>
/// Controls how the live transcription pipeline processes audio.
/// </summary>
public enum TranscriptionMode
{
    /// <summary>
    /// Two-pass sliding window.  Segments are emitted provisionally as
    /// soon as they pass quality filters, then finalized (or retracted)
    /// once the commit delay confirms them.  Consumers see text within
    /// ~1 inference tick and receive a second event when the segment is
    /// confirmed.  This is the default.
    /// </summary>
    SlidingWindow = 0,

    /// <summary>
    /// Sequential non-overlapping chunks.  Each chunk is transcribed
    /// independently and all segments are emitted immediately.  Lower
    /// latency, fewer API calls, but no cross-window context and no
    /// deduplication.
    /// </summary>
    Simple = 1,

    /// <summary>
    /// Single-pass sliding window.  Segments are only emitted after the
    /// full commit delay, deduplication, and hallucination filtering
    /// pipeline confirms them.  Higher accuracy but ~5–8 s perceived
    /// latency before the first text appears.
    /// </summary>
    StrictSlidingWindow = 2,
}
