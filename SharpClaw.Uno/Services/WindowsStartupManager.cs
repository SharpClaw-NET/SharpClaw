namespace SharpClaw.Services;

/// <summary>
/// Manages Windows auto-start entries for the SharpClaw backend and
/// gateway processes via the user's <b>Startup folder</b>
/// (<c>shell:startup</c>).
/// <para>
/// Each entry is a tiny <c>.vbs</c> launcher script that starts the
/// target process hidden (window style 0) — no console flash on login.
/// </para>
/// <para>
/// <b>Why not the <c>HKCU\…\Run</c> registry key?</b>  Under MSIX
/// packaging, registry writes are virtualised into a per-package hive
/// that Windows never reads at login.  The Startup folder, however, is
/// a real filesystem path even inside an MSIX container and survives
/// both packaged and unpackaged deployment modes.
/// </para>
/// <para>
/// Because MSIX package paths include the version number, the scripts
/// are <b>refreshed on every app launch</b> when auto-start is enabled
/// so they always point to the current executable location.
/// </para>
/// <para>
/// On non-Windows platforms all methods are safe no-ops.
/// </para>
/// </summary>
public static class WindowsStartupManager
{
    private const string BackendScriptName = "SharpClaw.Backend.vbs";
    private const string GatewayScriptName = "SharpClaw.Gateway.vbs";

    /// <summary>
    /// Registers (or removes) the backend process auto-start entry.
    /// </summary>
    public static void SetBackendAutoStart(bool enabled, string? executablePath = null, string? apiUrl = null)
    {
        if (!OperatingSystem.IsWindows()) return;

        if (enabled && executablePath is not null && File.Exists(executablePath))
            WriteStartupScript(BackendScriptName, executablePath, apiUrl);
        else
            RemoveStartupScript(BackendScriptName);
    }

    /// <summary>
    /// Registers (or removes) the gateway process auto-start entry.
    /// </summary>
    public static void SetGatewayAutoStart(bool enabled, string? executablePath = null, string? gatewayUrl = null)
    {
        if (!OperatingSystem.IsWindows()) return;

        if (enabled && executablePath is not null && File.Exists(executablePath))
            WriteStartupScript(GatewayScriptName, executablePath, gatewayUrl);
        else
            RemoveStartupScript(GatewayScriptName);
    }

    /// <summary>Returns <c>true</c> when the backend auto-start script exists.</summary>
    public static bool IsBackendAutoStartEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        return File.Exists(GetStartupScriptPath(BackendScriptName));
    }

    /// <summary>Returns <c>true</c> when the gateway auto-start script exists.</summary>
    public static bool IsGatewayAutoStartEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        return File.Exists(GetStartupScriptPath(GatewayScriptName));
    }

    /// <summary>
    /// Re-writes any existing startup scripts with the current executable
    /// paths.  Call on every app launch to handle MSIX path changes after
    /// updates.
    /// </summary>
    public static void RefreshIfNeeded(
        string? backendExePath, string? backendUrl,
        string? gatewayExePath, string? gatewayUrl)
    {
        if (!OperatingSystem.IsWindows()) return;

        if (IsBackendAutoStartEnabled() && backendExePath is not null)
            SetBackendAutoStart(true, backendExePath, backendUrl);

        if (IsGatewayAutoStartEnabled() && gatewayExePath is not null)
            SetGatewayAutoStart(true, gatewayExePath, gatewayUrl);
    }

    // ── Internal ─────────────────────────────────────────────────

    private static string GetStartupFolder()
        => Environment.GetFolderPath(Environment.SpecialFolder.Startup);

    private static string GetStartupScriptPath(string scriptName)
        => Path.Combine(GetStartupFolder(), scriptName);

    /// <summary>
    /// Writes a VBScript that launches the target executable hidden.
    /// <code>
    /// CreateObject("WScript.Shell").Run """C:\path\to\exe"" --urls ""http://..."" ", 0, False
    /// </code>
    /// Window style 0 = hidden, <c>False</c> = don't wait for exit.
    /// </summary>
    private static void WriteStartupScript(string scriptName, string exePath, string? url)
    {
        try
        {
            var startupDir = GetStartupFolder();
            if (!Directory.Exists(startupDir)) return;

            // Build the command string for WScript.Shell.Run.
            // VBScript requires doubling quotes inside a string literal.
            var cmd = $"\"\"{exePath}\"\"";
            if (!string.IsNullOrEmpty(url))
                cmd += $" --urls \"\"{url}\"\"";

            var script = $"CreateObject(\"WScript.Shell\").Run \"{cmd} \", 0, False";

            File.WriteAllText(Path.Combine(startupDir, scriptName), script);
        }
        catch { /* best-effort — may fail in restricted environments */ }
    }

    private static void RemoveStartupScript(string scriptName)
    {
        try
        {
            var path = GetStartupScriptPath(scriptName);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { /* best-effort */ }
    }
}
