using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace SharpClaw.Services;

/// <summary>Packaged Windows activations share one frontend per interactive user.</summary>
internal sealed class InstalledClientInstanceGuard : IDisposable
{
    private readonly Mutex? _presence;

    private InstalledClientInstanceGuard(Mutex? presence, bool isPrimary)
    {
        _presence = presence;
        IsPrimary = isPrimary;
    }

    internal bool IsPrimary { get; }

    internal static InstalledClientInstanceGuard Acquire()
    {
        if (!OperatingSystem.IsWindows() ||
            !File.Exists(Path.Combine(AppContext.BaseDirectory, "sharpclaw-installation.json")))
            return new InstalledClientInstanceGuard(null, isPrimary: true);

        using var identity = WindowsIdentity.GetCurrent();
        // The handle's lifetime is the presence lease, not thread-affine mutex
        // ownership. Local scopes it to the interactive session; SID scopes user.
        var presence = new Mutex(initiallyOwned: false,
            "Local\\SharpClaw.InstalledFrontend." + identity.User!.Value, out var primary);
        if (!primary)
        {
            using var current = Process.GetCurrentProcess();
            var processes = Process.GetProcessesByName("SharpClaw.Client.Uno");
            try
            {
                foreach (var process in processes)
                {
                    if (process.Id == Environment.ProcessId ||
                        process.SessionId != current.SessionId)
                        continue;
                    try
                    {
                        if (!string.Equals(process.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }
                    catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        continue;
                    }
                    var window = process.MainWindowHandle;
                    if (window != IntPtr.Zero)
                    {
                        ShowWindow(window, 9); // SW_RESTORE, including minimized windows.
                        SetForegroundWindow(window);
                        break;
                    }
                }
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }
        return new InstalledClientInstanceGuard(presence, primary);
    }

    public void Dispose() => _presence?.Dispose();

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
