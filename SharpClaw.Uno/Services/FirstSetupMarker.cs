using System.Reflection;

namespace SharpClaw.Services;

/// <summary>
/// Tracks whether the first-time setup wizard has been completed on this machine.
/// The marker file lives at <c>%LOCALAPPDATA%/SharpClaw/.setup-complete</c> and
/// contains the major version number that was current when setup finished.
/// When the app's major version advances, <see cref="NeedsUpgradeRerun"/> returns
/// <c>true</c> so the user can optionally redo setup for the new version.
/// </summary>
internal static class FirstSetupMarker
{
    private static readonly string MarkerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SharpClaw", ".setup-complete");

    /// <summary>True when setup has been completed at least once (any version).</summary>
    public static bool IsCompleted => File.Exists(MarkerPath);

    /// <summary>
    /// The major version that was running when setup was last completed.
    /// Returns <c>null</c> when the marker doesn't exist or was written by
    /// an older build that used the zero-byte format.
    /// </summary>
    public static int? CompletedMajorVersion
    {
        get
        {
            if (!File.Exists(MarkerPath)) return null;
            try
            {
                var text = File.ReadAllText(MarkerPath).Trim();
                return int.TryParse(text, out var v) ? v : null;
            }
            catch { return null; }
        }
    }

    /// <summary>The assembly's current major version (first segment of Version).</summary>
    public static int CurrentMajorVersion
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var ver = asm.GetName().Version;
            return ver?.Major ?? 0;
        }
    }

    /// <summary>
    /// True when setup was completed on an older major version, meaning the
    /// user should be offered the chance to redo it.
    /// </summary>
    public static bool NeedsUpgradeRerun
        => IsCompleted
           && (CompletedMajorVersion is null || CompletedMajorVersion < CurrentMajorVersion);

    /// <summary>Write the current major version into the marker file.</summary>
    public static void MarkCompleted()
    {
        var dir = Path.GetDirectoryName(MarkerPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(MarkerPath, CurrentMajorVersion.ToString());
    }
}
