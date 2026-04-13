namespace SharpClaw.Utils.Security;

/// <summary>
/// Path validation helpers that prevent directory traversal and path injection.
/// Each method is designed so static-analysis tools (CodeQL, Roslyn analyzers)
/// can recognize the taint-sanitization boundary at the call site.
/// </summary>
public static class PathGuard
{
    /// <summary>
    /// Validates that <paramref name="combined"/> resolves to a path strictly
    /// inside <paramref name="parentDir"/>. Returns the canonical path.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="combined"/> escapes <paramref name="parentDir"/>.
    /// </exception>
    public static string EnsureContainedIn(string combined, string parentDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(combined);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDir);

        if (combined.Contains('\0') || parentDir.Contains('\0'))
            throw new InvalidOperationException("Path contains null bytes.");

        var canonical = Path.GetFullPath(combined);
        var canonicalParent = Path.GetFullPath(parentDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var canonicalParentWithSep = canonicalParent + Path.DirectorySeparatorChar;

        if (!canonical.Equals(canonicalParent, StringComparison.OrdinalIgnoreCase) &&
            !canonical.StartsWith(canonicalParentWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Path '{combined}' escapes the allowed directory '{parentDir}'.");
        }

        return canonical;
    }

    /// <summary>
    /// Validates that <paramref name="name"/> is a simple file name with no
    /// path separators or traversal sequences. Returns the name unchanged.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="name"/> contains <c>..</c>, <c>/</c>,
    /// <c>\</c>, or null bytes.
    /// </exception>
    public static string EnsureFileName(string name, string paramName = "name")
    {
        ArgumentNullException.ThrowIfNull(name, paramName);

        if (name.Length == 0)
            throw new ArgumentException("File name cannot be empty.", paramName);

        if (name.Contains('\0'))
            throw new ArgumentException("File name contains null bytes.", paramName);

        if (name.Contains("..") || name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException(
                $"File name '{name}' must not contain path separators or traversal sequences.",
                paramName);

        return name;
    }

    /// <summary>
    /// Canonicalises a user-supplied file path and rejects null bytes.
    /// Use when the path must be absolute but is not scoped to a specific directory.
    /// Returns the canonical (fully-qualified) path.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="path"/> is null, empty, or contains null bytes.
    /// </exception>
    public static string EnsureAbsolutePath(string path, string paramName = "path")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, paramName);

        if (path.Contains('\0'))
            throw new ArgumentException("Path contains null bytes.", paramName);

        return Path.GetFullPath(path);
    }

    /// <summary>
    /// Strips newlines and control characters from a string so it can be
    /// safely interpolated into structured-log templates without triggering
    /// <c>cs/log-forging</c> (CWE-117) findings.
    /// </summary>
    public static string SanitizeForLog(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return string.Create(value.Length, value, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
                span[i] = char.IsControl(src[i]) ? '_' : src[i];
        });
    }

    /// <summary>
    /// Ensures the given path has one of the specified extensions
    /// (case-insensitive comparison).
    /// </summary>
    public static string EnsureExtension(string path, params ReadOnlySpan<string> allowed)
    {
        var ext = Path.GetExtension(path);
        foreach (var a in allowed)
        {
            if (ext.Equals(a, StringComparison.OrdinalIgnoreCase))
                return path;
        }

        throw new InvalidOperationException(
            $"Path '{Path.GetFileName(path)}' has disallowed extension '{ext}'.");
    }
}
