using System;
using System.IO;

namespace SharpClaw.Utils.Logging;

/// <summary>
/// Resolves the shared SharpClaw application data locations.
/// Local copy vendored into the VS extension because the Utils project targets net10
/// and cannot be referenced directly from a net472 VSIX project.
/// Keep in sync with SharpClaw.Utils\Logging\SharpClawAppDataPaths.cs.
/// </summary>
internal static class SharpClawAppDataPaths
{
    public static string GetSharpClawRootDirectory()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        if (!string.IsNullOrWhiteSpace(localAppData))
            return Path.Combine(localAppData, "SharpClaw");

        return Path.Combine(AppContext.BaseDirectory, "SharpClaw");
    }

    public static string GetLogsRootDirectory() =>
        Path.Combine(GetSharpClawRootDirectory(), "logs");

    public static string GetAppLogDirectory(string appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
            throw new ArgumentException("App name is required.", nameof(appName));

        return Path.Combine(GetLogsRootDirectory(), appName);
    }
}
