using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Modules.ComputerUse.Contracts;
using SharpClaw.Modules.ComputerUse.Triggers;

namespace SharpClaw.Modules.ComputerUse.Services;

/// <summary>
/// Manages OS-level shortcut files for tasks declared with
/// <c>[OsShortcut]</c>.
///
/// Two files are created per task:
/// <list type="bullet">
///   <item>A stub launcher (.bat on Windows, executable .sh on Linux) that
///   invokes the SharpClaw API server with the task's custom ID.</item>
///   <item>A shortcut file (.lnk on Windows, .desktop on Linux) that the
///   OS uses to display the entry in the application launcher / desktop.</item>
/// </list>
///
/// macOS is not supported. When called on macOS a <c>TASK441</c> warning is
/// logged and the method returns without throwing.
/// </summary>
public sealed class ShortcutLauncherService(ILogger<ShortcutLauncherService> logger) : IShortcutLauncher
{
    // ── Directory layout ─────────────────────────────────────────
    // All files are placed under %LOCALAPPDATA%\SharpClaw\Shortcuts on Windows
    // and ~/.local/share/sharpclaw/shortcuts on Linux so they are per-user and
    // do not require elevated permissions.

    private static string StubDirectory => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SharpClaw", "Shortcuts", "Stubs")
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "sharpclaw", "shortcuts", "stubs");

    private static string ShortcutDirectory => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SharpClaw", "Shortcuts")
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "sharpclaw", "shortcuts");

    private static string DesktopApplicationsDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "applications");

    // ── Public API ───────────────────────────────────────────────

    /// <summary>
    /// Writes the stub launcher file for <paramref name="customId"/>.
    /// Idempotent: skipped when the file already exists with identical content.
    /// </summary>
    public async Task WriteStubAsync(string customId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customId);

        if (IsMacOs())
        {
            LogMacOsWarning("WriteStubAsync");
            return;
        }

        Directory.CreateDirectory(StubDirectory);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var stubPath = Path.Combine(StubDirectory, $"{Sanitize(customId)}.bat");
            var content = BuildWindowsStub(customId);
            await WriteIfChangedAsync(stubPath, content, ct);
        }
        else
        {
            var stubPath = Path.Combine(StubDirectory, $"{Sanitize(customId)}.sh");
            var content = BuildLinuxStub(customId);
            await WriteIfChangedAsync(stubPath, content, ct);
            MakeExecutable(stubPath);
        }
    }

    /// <summary>
    /// Writes the OS shortcut file (.lnk on Windows, .desktop on Linux) for
    /// the given task definition and custom ID. Calls
    /// <see cref="WriteStubAsync"/> first to ensure the stub target exists.
    /// </summary>
    public async Task WriteShortcutAsync(
        TaskTriggerDefinition definition,
        string customId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(customId);

        if (IsMacOs())
        {
            LogMacOsWarning("WriteShortcutAsync");
            return;
        }

        await WriteStubAsync(customId, ct);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            await WriteWindowsShortcutAsync(definition, customId, ct);
        else
            await WriteLinuxDesktopEntryAsync(definition, customId, ct);
    }

    /// <summary>
    /// Rewrites the shortcut file only; leaves the stub untouched.
    /// Useful for updating the label, icon, or category without recreating
    /// the stub.
    /// </summary>
    public async Task RefreshShortcutsAsync(
        TaskTriggerDefinition definition,
        string customId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(customId);

        if (IsMacOs())
        {
            LogMacOsWarning("RefreshShortcutsAsync");
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            await WriteWindowsShortcutAsync(definition, customId, ct);
        else
            await WriteLinuxDesktopEntryAsync(definition, customId, ct);
    }

    /// <summary>
    /// Deletes the stub launcher and all shortcut files for <paramref name="customId"/>.
    /// </summary>
    public Task RemoveShortcutsAsync(string customId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customId);

        if (IsMacOs())
        {
            LogMacOsWarning("RemoveShortcutsAsync");
            return Task.CompletedTask;
        }

        var safe = Sanitize(customId);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            DeleteIfExists(Path.Combine(StubDirectory, $"{safe}.bat"));
            DeleteIfExists(Path.Combine(ShortcutDirectory, $"{safe}.lnk"));
        }
        else
        {
            DeleteIfExists(Path.Combine(StubDirectory, $"{safe}.sh"));
            var desktopPath = DesktopEntryPath(safe);
            if (desktopPath is not null)
                DeleteIfExists(desktopPath);
            TriggerDesktopDatabaseUpdate();
        }

        return Task.CompletedTask;
    }

    // ── Windows implementation ───────────────────────────────────

    private static string BuildWindowsStub(string customId)
    {
        // Launches the SharpClaw CLI with the task run command so the shortcut
        // works even when the API server is not already running.
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine($"sharpclaw task run \"{customId}\"");
        return sb.ToString();
    }

    private async Task WriteWindowsShortcutAsync(
        TaskTriggerDefinition definition,
        string customId,
        CancellationToken ct)
    {
        var safe = Sanitize(customId);
        var stubPath = Path.Combine(StubDirectory, $"{safe}.bat");
        var lnkPath = Path.Combine(ShortcutDirectory, $"{safe}.lnk");

        Directory.CreateDirectory(ShortcutDirectory);

        try
        {
            // WshShell COM — available on all Windows versions.
            var wshType = Type.GetTypeFromProgID("WScript.Shell");
            if (wshType is null)
            {
                logger.LogWarning(
                    "WScript.Shell COM object not available; cannot create .lnk shortcut for '{CustomId}'.",
                    customId);
                return;
            }

            var shell = Activator.CreateInstance(wshType)!;
            var shortcut = wshType.InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                null, shell, [lnkPath])!;

            var shortcutType = shortcut.GetType();

            shortcutType.InvokeMember("TargetPath",
                System.Reflection.BindingFlags.SetProperty, null, shortcut,
                [stubPath]);

            var label = ReadParam(definition, OsShortcutTriggerKeys.ShortcutLabel) ?? customId;
            shortcutType.InvokeMember("Description",
                System.Reflection.BindingFlags.SetProperty, null, shortcut,
                [label]);

            var icon = ReadParam(definition, OsShortcutTriggerKeys.ShortcutIcon);
            if (!string.IsNullOrWhiteSpace(icon))
                shortcutType.InvokeMember("IconLocation",
                    System.Reflection.BindingFlags.SetProperty, null, shortcut,
                    [icon]);

            shortcutType.InvokeMember("Save",
                System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);

            logger.LogDebug("Created Windows shortcut '{Path}' for '{CustomId}'.", lnkPath, customId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to create Windows shortcut for '{CustomId}'; stub still available at '{StubPath}'.",
                customId, stubPath);
        }

        await Task.CompletedTask;
    }

    // ── Linux implementation ──────────────────────────────────────

    private static string BuildLinuxStub(string customId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/sh");
        sb.AppendLine($"sharpclaw task run \"{customId}\"");
        return sb.ToString();
    }

    private async Task WriteLinuxDesktopEntryAsync(
        TaskTriggerDefinition definition,
        string customId,
        CancellationToken ct)
    {
        var safe = Sanitize(customId);
        var stubPath = Path.Combine(StubDirectory, $"{safe}.sh");

        // Prefer ~/.local/share/applications for per-user .desktop entries.
        Directory.CreateDirectory(DesktopApplicationsDirectory);
        var desktopPath = Path.Combine(DesktopApplicationsDirectory, $"sharpclaw-{safe}.desktop");

        var label = ReadParam(definition, OsShortcutTriggerKeys.ShortcutLabel) ?? customId;
        var icon = ReadParam(definition, OsShortcutTriggerKeys.ShortcutIcon) ?? "utilities-terminal";
        var rawCategory = ReadParam(definition, OsShortcutTriggerKeys.ShortcutCategory);
        var category = string.IsNullOrWhiteSpace(rawCategory)
            ? "Utility;"
            : $"{rawCategory};";

        var sb = new StringBuilder();
        sb.AppendLine("[Desktop Entry]");
        sb.AppendLine("Version=1.0");
        sb.AppendLine("Type=Application");
        sb.AppendLine($"Name={label}");
        sb.AppendLine($"Exec={stubPath}");
        sb.AppendLine($"Icon={icon}");
        sb.AppendLine($"Categories={category}");
        sb.AppendLine("Terminal=false");
        sb.AppendLine($"Comment=SharpClaw task: {customId}");

        await WriteIfChangedAsync(desktopPath, sb.ToString(), ct);

        TriggerDesktopDatabaseUpdate();

        logger.LogDebug(
            "Created Linux .desktop entry '{Path}' for '{CustomId}'.", desktopPath, customId);
    }

    private static string? DesktopEntryPath(string safeCustomId)
    {
        var path = Path.Combine(DesktopApplicationsDirectory, $"sharpclaw-{safeCustomId}.desktop");
        return File.Exists(path) ? path : null;
    }

    private void TriggerDesktopDatabaseUpdate()
    {
        // update-desktop-database refreshes the MIME-type cache; best-effort.
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "update-desktop-database",
                Arguments = DesktopApplicationsDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            // Fire and forget — don't await; not critical.
        }
        catch
        {
            // update-desktop-database may not be installed on all distros.
        }
    }

    // ── Shared helpers ───────────────────────────────────────────

    private static async Task WriteIfChangedAsync(
        string path, string content, CancellationToken ct)
    {
        if (File.Exists(path))
        {
            var existing = await File.ReadAllTextAsync(path, ct);
            if (existing == content)
                return; // idempotent
        }

        await File.WriteAllTextAsync(path, content, Encoding.UTF8, ct);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void MakeExecutable(string path)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"+x \"{path}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            // Best-effort; the user can chmod manually if this fails.
        }
        catch
        {
            // chmod may not be available in all environments.
        }
    }

    /// <summary>
    /// Reads a shortcut parameter from <see cref="TaskTriggerDefinition.Parameters"/>.
    /// Returns <see langword="null"/> when the key is absent or the value is
    /// blank. The launcher reads exclusively from <c>Parameters</c> so the
    /// shim typed properties on <see cref="TaskTriggerDefinition"/> can be
    /// removed in a later phase.
    /// </summary>
    private static string? ReadParam(TaskTriggerDefinition definition, string key)
    {
        if (definition.Parameters.TryGetValue(key, out var value)
            && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return null;
    }

    /// <summary>Sanitizes a custom ID for use as a file name.</summary>
    internal static string Sanitize(string customId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(customId.Length);
        foreach (var c in customId)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }

    private bool IsMacOs()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return false;

        return true;
    }

    private void LogMacOsWarning(string operation)
    {
        logger.LogWarning(
            "[TASK441] {Operation}: OS shortcut management is not supported on macOS.",
            operation);
    }
}
