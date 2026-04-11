using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpClaw.Modules.ModuleDev.Services;

/// <summary>
/// Read-only process introspection: loaded modules (DLLs), PE export names,
/// window classes, and thread info. Windows uses Toolhelp32; Linux uses /proc.
/// </summary>
internal sealed partial class ProcessInspectionService
{
    internal sealed record ProcessInspectionResult(
        int ProcessId,
        string ProcessName,
        IReadOnlyList<LoadedModuleInfo>? Modules,
        IReadOnlyList<string>? WindowClasses,
        IReadOnlyList<ThreadInfo>? Threads,
        string? Error);

    internal sealed record LoadedModuleInfo(
        string Name,
        string Path,
        IReadOnlyList<string>? Exports);

    internal sealed record ThreadInfo(int ThreadId, string State);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Inspect a process by PID, name, or window title substring.
    /// </summary>
    public Task<string> InspectAsync(
        string target, IReadOnlyList<string>? include = null, string? exportFilter = null,
        CancellationToken ct = default)
    {
        var process = ResolveProcess(target);
        if (process is null)
            return Task.FromResult(JsonSerializer.Serialize(
                new ProcessInspectionResult(0, target, null, null, null, $"Process '{target}' not found."),
                JsonOpts));

        var sections = include?.Select(s => s.ToLowerInvariant()).ToHashSet()
            ?? ["modules", "exports", "window_classes", "threads"];

        var includeModules = sections.Contains("modules") || sections.Contains("exports");
        var includeExports = sections.Contains("exports");
        var includeWindowClasses = sections.Contains("window_classes");
        var includeThreads = sections.Contains("threads");

        List<LoadedModuleInfo>? modules = null;
        List<string>? windowClasses = null;
        List<ThreadInfo>? threads = null;

        if (includeModules)
            modules = GetLoadedModules(process, includeExports, exportFilter);

        if (includeWindowClasses && OperatingSystem.IsWindows())
            windowClasses = GetWindowClasses(process);

        if (includeThreads)
            threads = GetThreads(process);

        var result = new ProcessInspectionResult(
            process.Id, process.ProcessName, modules, windowClasses, threads, null);

        return Task.FromResult(JsonSerializer.Serialize(result, JsonOpts));
    }

    // ── Process resolution ────────────────────────────────────────

    private static Process? ResolveProcess(string target)
    {
        // Try PID first
        if (int.TryParse(target, out var pid))
        {
            try { return Process.GetProcessById(pid); }
            catch { return null; }
        }

        // Try process name (exact)
        var byName = Process.GetProcessesByName(target);
        if (byName.Length > 0)
            return byName[0];

        // Try process name without extension
        var withoutExt = Path.GetFileNameWithoutExtension(target);
        byName = Process.GetProcessesByName(withoutExt);
        if (byName.Length > 0)
            return byName[0];

        // Try window title substring
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (!string.IsNullOrEmpty(p.MainWindowTitle) &&
                    p.MainWindowTitle.Contains(target, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            catch { /* Access denied for some processes */ }
        }

        return null;
    }

    // ── Module enumeration ────────────────────────────────────────

    private static List<LoadedModuleInfo> GetLoadedModules(
        Process process, bool includeExports, string? exportFilter)
    {
        var result = new List<LoadedModuleInfo>();

        try
        {
            foreach (ProcessModule mod in process.Modules)
            {
                IReadOnlyList<string>? exports = null;

                if (includeExports && OperatingSystem.IsWindows())
                {
                    try
                    {
                        exports = PeExportReader.ReadExports(mod.FileName!, exportFilter);
                    }
                    catch { /* PE parsing failure — skip exports */ }
                }

                result.Add(new LoadedModuleInfo(
                    mod.ModuleName ?? "unknown",
                    mod.FileName ?? "unknown",
                    exports));
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access denied — return partial results
        }

        return result;
    }

    // ── Window class enumeration (Windows only) ───────────────────

    private static List<string> GetWindowClasses(Process process)
    {
        if (!OperatingSystem.IsWindows())
            return [];

        var classes = new HashSet<string>();

        foreach (ProcessThread thread in process.Threads)
        {
            NativeMethods.EnumThreadWindows(thread.Id, (hWnd, _) =>
            {
                var sb = new char[256];
                var len = NativeMethods.GetClassName(hWnd, sb, sb.Length);
                if (len > 0)
                    classes.Add(new string(sb, 0, len));
                return true;
            }, IntPtr.Zero);
        }

        return classes.Order().ToList();
    }

    // ── Thread enumeration ────────────────────────────────────────

    private static List<ThreadInfo> GetThreads(Process process)
    {
        var result = new List<ThreadInfo>();

        try
        {
            foreach (ProcessThread thread in process.Threads)
            {
                result.Add(new ThreadInfo(thread.Id, thread.ThreadState.ToString()));
            }
        }
        catch { /* Access denied */ }

        return result;
    }

    // ── Win32 interop ─────────────────────────────────────────────

    private static partial class NativeMethods
    {
        public delegate bool EnumThreadWndProc(IntPtr hWnd, IntPtr lParam);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool EnumThreadWindows(
            int dwThreadId,
            EnumThreadWndProc lpfn,
            IntPtr lParam);

        [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial int GetClassName(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);
    }
}
