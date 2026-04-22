namespace SharpClaw.Utils.Logging;

/// <summary>
/// Resolves the shared SharpClaw application data locations.
/// </summary>
public static class SharpClawAppDataPaths
{
    /// <summary>
    /// Returns the root SharpClaw directory under local application data.
    /// Falls back to <see cref="AppContext.BaseDirectory"/> when the special
    /// folder is unavailable.
    /// </summary>
    public static string GetSharpClawRootDirectory()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        if (!string.IsNullOrWhiteSpace(localAppData))
            return Path.Combine(localAppData, "SharpClaw");

        return Path.Combine(AppContext.BaseDirectory, "SharpClaw");
    }

    /// <summary>
    /// Returns the shared logs directory under the SharpClaw root.
    /// </summary>
    public static string GetLogsRootDirectory() =>
        Path.Combine(GetSharpClawRootDirectory(), "logs");

    /// <summary>
    /// Returns the per-application log directory for the specified app name.
    /// </summary>
    public static string GetAppLogDirectory(string appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
            throw new ArgumentException(
                "App name is required.",
                nameof(appName));

        return Path.Combine(GetLogsRootDirectory(), appName);
    }
}
