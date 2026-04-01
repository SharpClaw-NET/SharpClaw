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
    /// Non-overlapping sequential windows (default 10 s).  Each window of
    /// audio is transcribed exactly once — one API call per window.
    /// Cross-window continuity is maintained through prompt conditioning.
    /// The full deduplication and hallucination filtering pipeline still
    /// runs as a safety net.  Minimal token cost; perceived latency
    /// equals the window length.  Use <c>windowSeconds</c> to control
    /// the window size (clamped to [5, 15]).
    /// </summary>
    StrictWindow = 2,
}
