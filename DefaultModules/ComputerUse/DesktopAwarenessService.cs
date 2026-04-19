using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using SharpClaw.Application.Infrastructure.Models.Resources;
using SharpClaw.Contracts.Modules.Contracts;
using SharpClaw.Utils.Security;

namespace SharpClaw.Modules.ComputerUse;

/// <summary>
/// Desktop awareness tools: window enumeration, application launch,
/// window management, clipboard, and process control.
/// Implements <see cref="IWindowManager"/> for cross-module consumption.
/// Windows only — macOS/Linux return stubs.
/// </summary>
public sealed class DesktopAwarenessService : IWindowManager
{
    /// <summary>
    /// Blocklisted executable names that agents are never allowed to launch.
    /// </summary>
    private static readonly HashSet<string> BlockedExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "regedit.exe",
        "reg.exe", "wscript.exe", "cscript.exe", "mshta.exe",
        "bash", "sh", "zsh",
    };

    /// <summary>
    /// Enumerate visible desktop windows.
    /// Returns a JSON array of window entries.
    /// </summary>
    public string EnumerateWindows()
    {
        if (!OperatingSystem.IsWindows())
            return JsonSerializer.Serialize(new
            {
                message = "Window enumeration is only supported on Windows.",
                windows = Array.Empty<object>(),
            });

        return EnumerateWindowsWindows();
    }

    /// <summary>
    /// Launch a registered native application.
    /// Returns the new PID and window title once the main window appears.
    /// </summary>
    public async Task<string> LaunchApplicationAsync(
        NativeApplicationDB app, string? arguments, string? filePath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        var exeName = Path.GetFileName(app.ExecutablePath);
        if (BlockedExecutables.Contains(exeName))
            throw new InvalidOperationException(
                $"Launching '{exeName}' is blocked for security reasons.");

        var executablePath = PathGuard.EnsureAbsolutePath(
            app.ExecutablePath, nameof(app.ExecutablePath));
        if (!File.Exists(executablePath))
            throw new FileNotFoundException(
                $"Executable not found: {executablePath}");

        var args = BuildArguments(arguments, filePath);

        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = args,
            UseShellExecute = true,
        };

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException(
                $"Failed to start process: {app.ExecutablePath}");

        // Poll for the main window to appear (up to 5 seconds)
        string? windowTitle = null;
        for (int i = 0; i < 50 && !ct.IsCancellationRequested; i++)
        {
            await Task.Delay(100, ct);
            process.Refresh();
            if (process.MainWindowHandle != nint.Zero)
            {
                windowTitle = process.MainWindowTitle;
                break;
            }
        }

        return JsonSerializer.Serialize(new
        {
            pid = process.Id,
            processName = process.ProcessName,
            windowTitle = windowTitle ?? "(no window detected)",
            applicationName = app.Name,
        });
    }

    // ═══════════════════════════════════════════════════════════════
    // Windows implementation
    // ═══════════════════════════════════════════════════════════════

    [SupportedOSPlatform("windows")]
    private static string EnumerateWindowsWindows()
    {
        var windows = new List<object>();

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd))
                return true;

            var titleLength = GetWindowTextLength(hWnd);
            if (titleLength == 0)
                return true;

            var titleBuffer = new char[titleLength + 1];
            GetWindowText(hWnd, titleBuffer, titleBuffer.Length);
            var title = new string(titleBuffer, 0, titleLength);

            // Skip system/shell windows
            if (IsSystemWindow(title))
                return true;

            GetWindowThreadProcessId(hWnd, out uint processId);

            string? processName = null;
            string? executablePath = null;
            try
            {
                var process = Process.GetProcessById((int)processId);
                processName = process.ProcessName;
                try
                {
                    executablePath = process.MainModule?.FileName;
                }
                catch (Exception)
                {
                    // Access denied for some system processes
                }
            }
            catch (ArgumentException)
            {
                // Process may have exited
            }

            windows.Add(new
            {
                title,
                processName,
                processId,
                mainWindowHandle = hWnd.ToString(),
                executablePath,
            });

            return true;
        }, nint.Zero);

        return JsonSerializer.Serialize(windows);
    }

    private static bool IsSystemWindow(string title) =>
        title is "Program Manager" or "Windows Input Experience" or
            "Windows Shell Experience Host" or "Microsoft Text Input Application" or
            "MSCTFIME UI";

    private static string BuildArguments(string? arguments, string? filePath)
    {
        if (filePath is not null && arguments is not null)
            return $"\"{filePath}\" {arguments}";
        if (filePath is not null)
            return $"\"{filePath}\"";
        return arguments ?? "";
    }

    // ═══════════════════════════════════════════════════════════════
    // Shared window finder
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Find a window handle by PID, process name, or title substring.
    /// At least one parameter must be non-null.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static nint FindWindow(int? processId, string? processName, string? titleContains)
    {
        // Normalize empty/whitespace strings to null so they don't participate in matching.
        if (string.IsNullOrWhiteSpace(processName)) processName = null;
        if (string.IsNullOrWhiteSpace(titleContains)) titleContains = null;

        if (processId is null && processName is null && titleContains is null)
            throw new ArgumentException("At least one of processId, processName, or titleContains is required.");

        nint found = nint.Zero;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd))
                return true;

            var titleLength = GetWindowTextLength(hWnd);
            if (titleLength == 0)
                return true;

            var titleBuffer = new char[titleLength + 1];
            GetWindowText(hWnd, titleBuffer, titleBuffer.Length);
            var title = new string(titleBuffer, 0, titleLength);

            if (IsSystemWindow(title))
                return true;

            GetWindowThreadProcessId(hWnd, out uint pid);

            if (processId is not null && (int)pid != processId.Value)
                return true;

            if (processName is not null)
            {
                try
                {
                    var proc = Process.GetProcessById((int)pid);
                    if (!proc.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (ArgumentException)
                {
                    return true; // process exited
                }
            }

            if (titleContains is not null &&
                !title.Contains(titleContains, StringComparison.OrdinalIgnoreCase))
                return true;

            found = hWnd;
            return false; // stop enumeration
        }, nint.Zero);

        if (found == nint.Zero)
            throw new InvalidOperationException(
                $"No window found matching processId={processId}, processName={processName}, titleContains={titleContains}.");

        return found;
    }

    /// <summary>Get the title of a window handle.</summary>
    [SupportedOSPlatform("windows")]
    private static string GetWindowTitle(nint hWnd)
    {
        var len = GetWindowTextLength(hWnd);
        if (len == 0) return "";
        var buf = new char[len + 1];
        GetWindowText(hWnd, buf, buf.Length);
        return new string(buf, 0, len);
    }

    /// <summary>Get the PID of a window handle.</summary>
    [SupportedOSPlatform("windows")]
    private static int GetWindowPid(nint hWnd)
    {
        GetWindowThreadProcessId(hWnd, out uint pid);
        return (int)pid;
    }

    // ═══════════════════════════════════════════════════════════════
    // Window management: focus, close, resize
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Bring a window to the foreground.</summary>
    public string FocusWindow(int? processId, string? processName, string? titleContains)
    {
        if (!OperatingSystem.IsWindows())
            return NotSupportedJson("Focus window");

        var hWnd = FindWindow(processId, processName, titleContains);
        ShowWindow(hWnd, SW_RESTORE);
        SetForegroundWindow(hWnd);

        return JsonSerializer.Serialize(new
        {
            title = GetWindowTitle(hWnd),
            processId = GetWindowPid(hWnd),
            focused = true,
        });
    }

    /// <summary>Send WM_CLOSE to a window (graceful close).</summary>
    public string CloseWindow(int? processId, string? processName, string? titleContains)
    {
        if (!OperatingSystem.IsWindows())
            return NotSupportedJson("Close window");

        var hWnd = FindWindow(processId, processName, titleContains);
        var title = GetWindowTitle(hWnd);
        var pid = GetWindowPid(hWnd);

        PostMessage(hWnd, WM_CLOSE, nint.Zero, nint.Zero);

        return JsonSerializer.Serialize(new
        {
            title,
            processId = pid,
            closeSent = true,
        });
    }

    /// <summary>Move and/or resize a window. Optionally set minimize/maximize/restore state.</summary>
    public string ResizeWindow(
        int? processId, string? titleContains,
        int? x, int? y, int? width, int? height, string? state)
    {
        if (!OperatingSystem.IsWindows())
            return NotSupportedJson("Resize window");

        var hWnd = FindWindow(processId, null, titleContains);
        var title = GetWindowTitle(hWnd);
        var pid = GetWindowPid(hWnd);

        // Apply state change first
        if (state is not null)
        {
            var cmd = state.ToLowerInvariant() switch
            {
                "minimized" => SW_MINIMIZE,
                "maximized" => SW_MAXIMIZE,
                "normal" => SW_RESTORE,
                _ => throw new ArgumentException($"Invalid state '{state}'. Use normal, minimized, or maximized."),
            };
            ShowWindow(hWnd, cmd);
        }

        // Apply position/size if any specified
        if (x is not null || y is not null || width is not null || height is not null)
        {
            GetWindowRect(hWnd, out RECT rect);
            var newX = x ?? rect.Left;
            var newY = y ?? rect.Top;
            var newW = width ?? (rect.Right - rect.Left);
            var newH = height ?? (rect.Bottom - rect.Top);
            MoveWindow(hWnd, newX, newY, newW, newH, true);
        }

        GetWindowRect(hWnd, out RECT finalRect);
        return JsonSerializer.Serialize(new
        {
            title,
            processId = pid,
            x = finalRect.Left,
            y = finalRect.Top,
            width = finalRect.Right - finalRect.Left,
            height = finalRect.Bottom - finalRect.Top,
            state = state ?? "normal",
        });
    }

    // ═══════════════════════════════════════════════════════════════
    // Hotkey
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Send a keyboard shortcut. Optionally focus a window first.</summary>
    public string SendHotkey(string keys, int? processId, string? titleContains)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keys);

        if (!OperatingSystem.IsWindows())
            return NotSupportedJson("Send hotkey");

        string? focusedWindow = null;
        if (processId is not null || titleContains is not null)
        {
            var hWnd = FindWindow(processId, null, titleContains);
            ShowWindow(hWnd, SW_RESTORE);
            SetForegroundWindow(hWnd);
            focusedWindow = GetWindowTitle(hWnd);
        }

        var parts = keys.ToLowerInvariant().Split('+', StringSplitOptions.TrimEntries);
        var modifiers = new List<ushort>();
        ushort mainKey = 0;

        foreach (var part in parts)
        {
            var vk = MapKeyName(part);
            if (part is "ctrl" or "alt" or "shift" or "win")
                modifiers.Add(vk);
            else
                mainKey = vk;
        }

        if (mainKey == 0)
            throw new ArgumentException($"No main key found in '{keys}'. Provide a non-modifier key.");

        // Build INPUT array: modifiers down, main down, main up, modifiers up
        var inputs = new List<INPUT>();
        foreach (var mod in modifiers)
            inputs.Add(MakeKeyInput(mod, down: true));
        inputs.Add(MakeKeyInput(mainKey, down: true));
        inputs.Add(MakeKeyInput(mainKey, down: false));
        for (int i = modifiers.Count - 1; i >= 0; i--)
            inputs.Add(MakeKeyInput(modifiers[i], down: false));

        var arr = inputs.ToArray();
        SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());

        return JsonSerializer.Serialize(new
        {
            keys,
            sent = true,
            focusedWindow,
        });
    }

    [SupportedOSPlatform("windows")]
    private static INPUT MakeKeyInput(ushort vk, bool down) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = down ? 0u : KEYEVENTF_KEYUP,
                time = 0,
                dwExtraInfo = nint.Zero,
            }
        }
    };

    private static ushort MapKeyName(string name) => name switch
    {
        "ctrl" or "control" => VK_CONTROL,
        "alt" or "menu"     => VK_MENU,
        "shift"             => VK_SHIFT,
        "win" or "windows"  => VK_LWIN,
        "enter" or "return" => 0x0D,
        "tab"               => 0x09,
        "escape" or "esc"   => 0x1B,
        "space"             => 0x20,
        "backspace"         => 0x08,
        "delete" or "del"   => 0x2E,
        "insert"            => 0x2D,
        "home"              => 0x24,
        "end"               => 0x23,
        "pageup"            => 0x21,
        "pagedown"          => 0x22,
        "up"                => 0x26,
        "down"              => 0x28,
        "left"              => 0x25,
        "right"             => 0x27,
        "f1"  => 0x70, "f2"  => 0x71, "f3"  => 0x72, "f4"  => 0x73,
        "f5"  => 0x74, "f6"  => 0x75, "f7"  => 0x76, "f8"  => 0x77,
        "f9"  => 0x78, "f10" => 0x79, "f11" => 0x7A, "f12" => 0x7B,
        _ when name.Length == 1 && char.IsAsciiLetterUpper(name[0]) => (ushort)name[0],
        _ when name.Length == 1 && char.IsAsciiLetterLower(name[0]) => (ushort)char.ToUpperInvariant(name[0]),
        _ when name.Length == 1 && char.IsAsciiDigit(name[0])       => (ushort)name[0],
        _ => throw new ArgumentException($"Unrecognized key: '{name}'."),
    };

    // ═══════════════════════════════════════════════════════════════
    // Window capture
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Screenshot a single window. Returns base64 PNG.</summary>
    public string CaptureWindow(int? processId, string? processName, string? titleContains)
    {
        if (!OperatingSystem.IsWindows())
            return NotSupportedJson("Capture window");

        return CaptureWindowWindows(processId, processName, titleContains);
    }

    [SupportedOSPlatform("windows")]
    private string CaptureWindowWindows(int? processId, string? processName, string? titleContains)
    {
        var hWnd = FindWindow(processId, processName, titleContains);
        var title = GetWindowTitle(hWnd);
        var pid = GetWindowPid(hWnd);

        GetWindowRect(hWnd, out RECT rect);
        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;

        if (w <= 0 || h <= 0)
            return JsonSerializer.Serialize(new
            {
                title, processId = pid, width = w, height = h,
                error = "Window has zero dimensions (possibly minimized).",
            });

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        var hdc = g.GetHdc();
        bool printed = PrintWindow(hWnd, hdc, PW_RENDERFULLCONTENT);
        g.ReleaseHdc(hdc);

        // Fallback to BitBlt if PrintWindow fails
        if (!printed)
        {
            var screenDc = GetDC(nint.Zero);
            hdc = g.GetHdc();
            BitBlt(hdc, 0, 0, w, h, screenDc, rect.Left, rect.Top, SRCCOPY);
            g.ReleaseHdc(hdc);
            ReleaseDC(nint.Zero, screenDc);
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        var base64 = Convert.ToBase64String(ms.ToArray());

        return JsonSerializer.Serialize(new
        {
            title, processId = pid, width = w, height = h,
            imageBase64 = base64,
        });
    }

    // ═══════════════════════════════════════════════════════════════
    // Clipboard
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Read clipboard contents. Runs on STA thread.</summary>
    public Task<string> ReadClipboardAsync(string? format)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(NotSupportedJson("Read clipboard"));

        #pragma warning disable CA1416 // Guarded by OperatingSystem.IsWindows() above
                return RunOnStaThread(() => ReadClipboardWindows(format));
        #pragma warning restore CA1416
    }

    /// <summary>Write text or file paths to clipboard. Runs on STA thread.</summary>
    public Task<string> WriteClipboardAsync(string? text, string[]? filePaths)
    {
        if (text is null && (filePaths is null || filePaths.Length == 0))
            throw new ArgumentException("Provide either text or filePaths.");
        if (text is not null && filePaths is not null && filePaths.Length > 0)
            throw new ArgumentException("Provide either text or filePaths, not both.");

        if (!OperatingSystem.IsWindows())
            return Task.FromResult(NotSupportedJson("Write clipboard"));

        #pragma warning disable CA1416 // Guarded by OperatingSystem.IsWindows() above
                return RunOnStaThread(() => WriteClipboardWindows(text, filePaths));
        #pragma warning restore CA1416
    }

    [SupportedOSPlatform("windows")]
    private static string ReadClipboardWindows(string? format)
    {
        if (!OpenClipboard(nint.Zero))
            return JsonSerializer.Serialize(new { format = "error", content = "Failed to open clipboard." });

        try
        {
            // Text
            if (format is null or "text")
            {
                var hData = GetClipboardData(CF_UNICODETEXT);
                if (hData != nint.Zero)
                {
                    var ptr = GlobalLock(hData);
                    if (ptr != nint.Zero)
                    {
                        var content = Marshal.PtrToStringUni(ptr) ?? "";
                        GlobalUnlock(hData);
                        return JsonSerializer.Serialize(new { format = "text", content });
                    }
                }
                if (format is "text")
                    return JsonSerializer.Serialize(new { format = "text", content = (string?)null });
            }

            // Files
            if (format is null or "files")
            {
                var hDrop = GetClipboardData(CF_HDROP);
                if (hDrop != nint.Zero)
                {
                    var count = DragQueryFile(hDrop, 0xFFFFFFFF, null!, 0);
                    var files = new string[(int)count];
                    for (uint i = 0; i < count; i++)
                    {
                        var buf = new char[260];
                        DragQueryFile(hDrop, i, buf, (uint)buf.Length);
                        files[i] = new string(buf).TrimEnd('\0');
                    }
                    return JsonSerializer.Serialize(new { format = "files", content = files });
                }
                if (format is "files")
                    return JsonSerializer.Serialize(new { format = "files", content = Array.Empty<string>() });
            }

            // Image (DIB → PNG base64)
            if (format is null or "image")
            {
                var hBmp = GetClipboardData(CF_BITMAP);
                if (hBmp != nint.Zero)
                {
                    using var bmp = Image.FromHbitmap(hBmp);
                    using var ms = new MemoryStream();
                    bmp.Save(ms, ImageFormat.Png);
                    return JsonSerializer.Serialize(new
                    {
                        format = "image",
                        imageBase64 = Convert.ToBase64String(ms.ToArray()),
                        width = bmp.Width,
                        height = bmp.Height,
                    });
                }
                if (format is "image")
                    return JsonSerializer.Serialize(new { format = "image", content = (string?)null });
            }

            return JsonSerializer.Serialize(new { format = "empty", content = (string?)null });
        }
        finally
        {
            CloseClipboard();
        }
    }

    [SupportedOSPlatform("windows")]
    private static string WriteClipboardWindows(string? text, string[]? filePaths)
    {
        if (!OpenClipboard(nint.Zero))
            return JsonSerializer.Serialize(new { written = false, error = "Failed to open clipboard." });

        try
        {
            EmptyClipboard();

            if (text is not null)
            {
                var hGlobal = Marshal.StringToHGlobalUni(text);
                SetClipboardData(CF_UNICODETEXT, hGlobal);
                // Do not free hGlobal — clipboard owns it
                return JsonSerializer.Serialize(new { written = true, format = "text", length = text.Length });
            }

            if (filePaths is { Length: > 0 })
            {
                // Build DROPFILES structure
                var joined = string.Join('\0', filePaths) + "\0\0";
                var bytesNeeded = Marshal.SizeOf<DROPFILES>() + joined.Length * 2;
                var hGlobal = Marshal.AllocHGlobal(bytesNeeded);
                var df = new DROPFILES
                {
                    pFiles = (uint)Marshal.SizeOf<DROPFILES>(),
                    fWide = true,
                };
                Marshal.StructureToPtr(df, hGlobal, false);
                var dest = hGlobal + Marshal.SizeOf<DROPFILES>();
                var chars = joined.ToCharArray();
                Marshal.Copy(chars, 0, dest, chars.Length);
                SetClipboardData(CF_HDROP, hGlobal);
                return JsonSerializer.Serialize(new { written = true, format = "files", count = filePaths.Length });
            }

            return JsonSerializer.Serialize(new { written = false, error = "No data provided." });
        }
        finally
        {
            CloseClipboard();
        }
    }

    [SupportedOSPlatform("windows")]
    private static Task<string> RunOnStaThread(Func<string> action)
    {
        var tcs = new TaskCompletionSource<string>();
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(action()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    // ═══════════════════════════════════════════════════════════════
    // Process control
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Gracefully stop a process that matches a registered native application.
    /// </summary>
    public async Task<string> StopProcessAsync(
        int processId, bool force, NativeApplicationDB app, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return JsonSerializer.Serialize(new
            {
                processId,
                killed = false,
                error = $"No process with PID {processId} is running.",
            });
        }

        // Verify the process matches the registered application
        var exeName = Path.GetFileName(process.MainModule?.FileName ?? "");
        var appExeName = Path.GetFileName(app.ExecutablePath);
        if (!string.Equals(exeName, appExeName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Process {processId} ({exeName}) does not match registered app '{app.Name}' ({appExeName}).");

        if (force)
        {
            process.Kill(entireProcessTree: true);
            return JsonSerializer.Serialize(new { processId, killed = true, method = "force" });
        }

        // Graceful: CloseMainWindow, wait 5s, then kill
        process.CloseMainWindow();
        using var exitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        exitCts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(exitCts.Token);
            return JsonSerializer.Serialize(new
            {
                processId, killed = true, method = "graceful",
                exitCode = process.ExitCode,
            });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return JsonSerializer.Serialize(new { processId, killed = true, method = "force-after-timeout" });
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private static string NotSupportedJson(string action) =>
        JsonSerializer.Serialize(new { message = $"{action} is only supported on Windows." });

    // ═══════════════════════════════════════════════════════════════
    // P/Invoke declarations
    // ═══════════════════════════════════════════════════════════════

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, char[] lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(nint hWnd, int x, int y, int w, int h, bool repaint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint hWnd, nint hdc, uint nFlags);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(nint hdc, int x, int y, int cx, int cy,
        nint hdcSrc, int x1, int y1, uint rop);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern nint GetClipboardData(uint uFormat);

    [DllImport("user32.dll")]
    private static extern nint SetClipboardData(uint uFormat, nint hMem);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint hMem);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(nint hDrop, uint iFile, char[] lpszFile, uint cch);

    // ── Constants ─────────────────────────────────────────────────

    private const int SW_RESTORE = 9;
    private const int SW_MAXIMIZE = 3;
    private const int SW_MINIMIZE = 6;
    private const uint WM_CLOSE = 0x0010;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_LWIN = 0x5B;
    private const uint PW_RENDERFULLCONTENT = 2;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_HDROP = 15;
    private const uint CF_BITMAP = 2;

    // ── Structs ───────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DROPFILES
    {
        public uint pFiles;
        public int pt_x;
        public int pt_y;
        public bool fNC;
        public bool fWide;
    }
}
