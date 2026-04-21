namespace SharpClaw.Application.Services;

/// <summary>
/// Shared formatting helpers for chat-header notices — pieces of metadata
/// that are appended to the header string in both the default and custom
/// (template-based) header paths so they stay in sync.
/// </summary>
internal static class ChatHeaderNotices
{
    /// <summary>
    /// Formats the <c>reasoning-effort</c> informational notice.
    /// Used by providers that accept the hint for UX but have no
    /// mechanical reasoning-effort control in their runtime.
    /// Returns an empty string when <paramref name="effort"/> is null/blank,
    /// so callers can safely append the result unconditionally.
    /// </summary>
    public static string FormatReasoningEffortNotice(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
            return "";

        return $"reasoning-effort: {effort.Trim().ToLowerInvariant()} " +
               "(informational; this model has no mechanical reasoning-effort control)";
    }
}
