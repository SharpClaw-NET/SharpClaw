namespace SharpClaw.Services;

/// <summary>
/// Tracks whether the first-time setup wizard has been completed on this machine.
/// The marker is a zero-byte file in <c>%LOCALAPPDATA%/SharpClaw/.setup-complete</c>.
/// </summary>
internal static class FirstSetupMarker
{
    private static readonly string MarkerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SharpClaw", ".setup-complete");

    public static bool IsCompleted => File.Exists(MarkerPath);

    public static void MarkCompleted()
    {
        var dir = Path.GetDirectoryName(MarkerPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(MarkerPath, []);
    }
}
