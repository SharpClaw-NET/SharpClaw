namespace SharpClaw.Services;

/// <summary>
/// Tracks whether the first-time setup wizard has been completed on this machine.
/// The marker file lives at <c>%LOCALAPPDATA%/SharpClaw/.setup-complete</c> and
/// contains the major version number that was current when setup finished.
/// When the app's major version advances, <see cref="NeedsUpgradeRerun"/> returns
/// <c>true</c> so the user can optionally redo setup for the new version.
/// </summary>
internal sealed class FirstSetupMarker
{
    private readonly FrontendInstanceService _frontendInstance;

    public FirstSetupMarker(FrontendInstanceService frontendInstance)
    {
        _frontendInstance = frontendInstance;
    }

    /// <summary>True when setup has been completed at least once (any version).</summary>
    public bool IsCompleted => File.Exists(_frontendInstance.SetupMarkerPath);

    /// <summary>
    /// The major version that was running when setup was last completed.
    /// Returns <c>null</c> when the marker doesn't exist or was written by
    /// an older build that used the zero-byte format.
    /// </summary>
    public int? CompletedMajorVersion
    {
        get
        {
            if (!File.Exists(_frontendInstance.SetupMarkerPath)) return null;
            try
            {
                var text = File.ReadAllText(_frontendInstance.SetupMarkerPath).Trim();
                return int.TryParse(text, out var v) ? v : null;
            }
            catch { return null; }
        }
    }

    /// <summary>The assembly's current major version (first segment of Version).</summary>
    public int CurrentMajorVersion
    {
        get
        {
            var asm = typeof(FirstSetupMarker).Assembly;
            var ver = asm.GetName().Version;
            return ver?.Major ?? 0;
        }
    }

    /// <summary>
    /// True when setup was completed on an older major version, meaning the
    /// user should be offered the chance to redo it.
    /// </summary>
    public bool NeedsUpgradeRerun
        => IsCompleted
           && (CompletedMajorVersion is null || CompletedMajorVersion < CurrentMajorVersion);

    /// <summary>Write the current major version into the marker file.</summary>
    public void MarkCompleted()
    {
        var dir = Path.GetDirectoryName(_frontendInstance.SetupMarkerPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(_frontendInstance.SetupMarkerPath, CurrentMajorVersion.ToString());
    }
}
