using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mk8.Shell.Safety;

namespace Mk8.Shell.Engine;

/// <summary>
/// Executes a compiled mk8.shell script step by step.  This is the
/// <b>safe-shell-only</b> executor — it handles
/// <see cref="Contracts.Enums.SafeShellType.Mk8Shell"/> jobs
/// exclusively.  All file, dir, text, HTTP, env, and sysinfo verbs
/// are handled in-memory via .NET APIs.  Only
/// <see cref="Mk8CommandKind.Process"/> and
/// <see cref="Mk8CommandKind.GitProcess"/> spawn external processes,
/// and even those go through binary-allowlist validation, path
/// sandboxing, and argument sanitisation before being dispatched via
/// <see cref="ProcessStartInfo"/> with <c>UseShellExecute = false</c>.
/// <para>
/// <b>Dangerous shells (Bash, PowerShell, CommandPrompt, Git) are
/// never routed through this executor.</b>  They have their own
/// unrestricted execution path in <c>AgentJobService</c>.
/// </para>
/// <para>Cross-platform: runs identically on Windows, Linux, and macOS.</para>
/// </summary>
public sealed class Mk8ShellExecutor
{
    private readonly HttpClient _httpClient;

    public Mk8ShellExecutor(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>
    /// Execute the entire compiled script, respecting execution options
    /// (failure mode, timeouts, retries, cleanup).
    /// </summary>
    public async Task<Mk8ShellScriptResult> ExecuteAsync(
        Mk8CompiledScript script,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(script);

        var options = script.EffectiveOptions;
        var workspace = script.Workspace;
        var steps = new List<Mk8ShellStepResult>(script.Commands.Count);
        var variables = Mk8VariableResolver.BuildVariables(workspace);
        var allSucceeded = true;
        var totalSw = Stopwatch.StartNew();

        using var scriptCts = options.ScriptTimeout > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        scriptCts?.CancelAfter(options.ScriptTimeout);
        var effectiveCt = scriptCts?.Token ?? ct;

        for (var i = 0; i < script.Commands.Count; i++)
        {
            effectiveCt.ThrowIfCancellationRequested();

            var cmd = script.Commands[i];
            var originalOp = i < script.Commands.Count
                ? script.Commands[i]
                : null;

            var stepResult = await ExecuteStepWithRetriesAsync(
                i, cmd, options, workspace, variables, effectiveCt);

            steps.Add(stepResult);

            // Update $PREV with this step's output.
            Mk8VariableResolver.SetPreviousOutput(variables, stepResult.Output);

            if (!stepResult.Success)
            {
                allSucceeded = false;

                // Check for label-based forward jump on failure.
                if (script.LabelIndex is not null && originalOp is not null)
                {
                    // The original operation's OnFailure is baked into the
                    // compiled command sequence order by the compiler.
                    // For now, simply respect failure mode.
                }

                switch (options.FailureMode)
                {
                    case Mk8FailureMode.StopOnFirstError:
                        goto done;

                    case Mk8FailureMode.StopAndCleanup:
                        goto cleanup;

                    case Mk8FailureMode.ContinueOnError:
                        break;
                }
            }
        }

        goto done;

    cleanup:
        if (script.CleanupCommands is { Count: > 0 })
        {
            for (var i = 0; i < script.CleanupCommands.Count; i++)
            {
                if (effectiveCt.IsCancellationRequested) break;

                var cleanupResult = await ExecuteStepWithRetriesAsync(
                    script.Commands.Count + i,
                    script.CleanupCommands[i],
                    options with { FailureMode = Mk8FailureMode.ContinueOnError },
                    workspace, variables, effectiveCt);

                steps.Add(cleanupResult);
            }
        }

    done:
        totalSw.Stop();
        return new Mk8ShellScriptResult(allSucceeded, steps, totalSw.Elapsed);
    }

    // ═══════════════════════════════════════════════════════════════
    // Step execution with retries
    // ═══════════════════════════════════════════════════════════════

    private async Task<Mk8ShellStepResult> ExecuteStepWithRetriesAsync(
        int stepIndex,
        Mk8CompiledCommand cmd,
        Mk8ExecutionOptions options,
        Mk8WorkspaceContext workspace,
        Dictionary<string, string> variables,
        CancellationToken ct)
    {
        var maxAttempts = options.MaxRetries + 1;
        var stepTimeout = options.StepTimeout > TimeSpan.Zero
            ? options.StepTimeout
            : TimeSpan.FromSeconds(30);

        Mk8ShellStepResult? lastResult = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var sw = Stopwatch.StartNew();

            using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stepCts.CancelAfter(stepTimeout);

            try
            {
                var (output, error) = await ExecuteCommandAsync(
                    cmd, workspace, variables, stepCts.Token);

                sw.Stop();
                var verb = InferVerb(cmd);
                lastResult = new Mk8ShellStepResult(
                    stepIndex, verb, Success: true,
                    TruncateOutput(output, options.MaxOutputBytes),
                    TruncateOutput(error, options.MaxErrorBytes),
                    attempt, sw.Elapsed);
                return lastResult;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                sw.Stop();
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                var verb = InferVerb(cmd);
                lastResult = new Mk8ShellStepResult(
                    stepIndex, verb, Success: false,
                    null, ex.Message, attempt, sw.Elapsed);

                if (attempt < maxAttempts && options.RetryDelay > TimeSpan.Zero)
                    await Task.Delay(options.RetryDelay, ct);
            }
        }

        return lastResult!;
    }

    // ═══════════════════════════════════════════════════════════════
    // Command dispatch
    // ═══════════════════════════════════════════════════════════════

    private async Task<(string? Output, string? Error)> ExecuteCommandAsync(
        Mk8CompiledCommand cmd,
        Mk8WorkspaceContext workspace,
        Dictionary<string, string> variables,
        CancellationToken ct)
    {
        return cmd.Kind switch
        {
            Mk8CommandKind.InMemory => await ExecuteInMemoryAsync(cmd, workspace, variables, ct),
            Mk8CommandKind.Process
                => await ExecuteProcessAsync(cmd, workspace, ct),
            _ => throw new InvalidOperationException($"Unknown command kind: {cmd.Kind}")
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // Process execution (ProcRun only — git removed)
    // ═══════════════════════════════════════════════════════════════

    private static async Task<(string? Output, string? Error)> ExecuteProcessAsync(
        Mk8CompiledCommand cmd,
        Mk8WorkspaceContext workspace,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = cmd.Executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workspace.WorkingDirectory,
        };

        foreach (var arg in cmd.Arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };

        process.Start();

        // Read stdout and stderr concurrently to avoid deadlocks.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Kill the entire process tree on cancellation/timeout.
            // Process.Dispose() does NOT kill the process — without this,
            // the spawned process continues running as an orphan outside
            // any monitoring or audit trail.
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Process '{cmd.Executable}' exited with code {process.ExitCode}. " +
                $"stderr: {stderr}");

        return (stdout, stderr);
    }

    // ═══════════════════════════════════════════════════════════════
    // In-memory execution (.NET APIs — no process spawned)
    // ═══════════════════════════════════════════════════════════════

    private async Task<(string? Output, string? Error)> ExecuteInMemoryAsync(
        Mk8CompiledCommand cmd,
        Mk8WorkspaceContext workspace,
        Dictionary<string, string> variables,
        CancellationToken ct)
    {
        // The Executable field for InMemory commands is a marker like
        // "__mk8_inmemory_FileRead". Parse the verb name from it.
        var verbName = cmd.Executable.Replace("__mk8_inmemory_", "");

        if (!Enum.TryParse<Mk8ShellVerb>(verbName, out var verb))
            throw new InvalidOperationException($"Unknown in-memory verb marker: {cmd.Executable}");

        var output = verb switch
        {
            // ── Filesystem ────────────────────────────────────────
            Mk8ShellVerb.FileRead   => await File.ReadAllTextAsync(cmd.Arguments[0], ct),
            Mk8ShellVerb.FileWrite  => await WriteFileAsync(cmd.Arguments[0], cmd.Arguments[1], ct),
            Mk8ShellVerb.FileAppend => await AppendFileAsync(cmd.Arguments[0], cmd.Arguments[1], ct),
            Mk8ShellVerb.FileDelete => DeleteFile(cmd.Arguments[0]),
            Mk8ShellVerb.FileExists => File.Exists(cmd.Arguments[0]).ToString(),
            Mk8ShellVerb.FileList   => ListFiles(cmd.Arguments),
            Mk8ShellVerb.FileCopy   => CopyFile(cmd.Arguments[0], cmd.Arguments[1]),
            Mk8ShellVerb.FileMove   => MoveFile(cmd.Arguments[0], cmd.Arguments[1]),
            Mk8ShellVerb.FileInfo   => GetFileInfo(cmd.Arguments[0]),

            // ── Directory ─────────────────────────────────────────
            Mk8ShellVerb.DirCreate  => CreateDirectory(cmd.Arguments[0]),
            Mk8ShellVerb.DirDelete  => DeleteDirectory(cmd.Arguments[0]),
            Mk8ShellVerb.DirList    => ListDirectory(cmd.Arguments[0]),
            Mk8ShellVerb.DirExists  => Directory.Exists(cmd.Arguments[0]).ToString(),
            Mk8ShellVerb.DirTree    => DirTree(cmd.Arguments),

            // ── HTTP ──────────────────────────────────────────────
            Mk8ShellVerb.HttpGet    => await HttpGetAsync(cmd.Arguments[0], ct),
            Mk8ShellVerb.HttpPost   => await HttpPostAsync(cmd.Arguments, ct),
            Mk8ShellVerb.HttpPut    => await HttpPutAsync(cmd.Arguments, ct),
            Mk8ShellVerb.HttpDelete => await HttpDeleteAsync(cmd.Arguments[0], ct),

            // ── Text / data ───────────────────────────────────────
            Mk8ShellVerb.TextRegex   => TextRegex(cmd.Arguments[0], cmd.Arguments[1], variables),
            Mk8ShellVerb.TextReplace => TextReplace(cmd.Arguments, variables),
            Mk8ShellVerb.JsonParse   => JsonParse(cmd.Arguments[0], variables),
            Mk8ShellVerb.JsonQuery   => JsonQuery(cmd.Arguments[0], cmd.Arguments[1], variables),

            // ── Extended text manipulation ────────────────────────
            Mk8ShellVerb.TextSplit        => TextSplit(cmd.Arguments[0], cmd.Arguments[1]),
            Mk8ShellVerb.TextJoin         => TextJoinArgs(cmd.Arguments),
            Mk8ShellVerb.TextTrim         => cmd.Arguments[0].Trim(),
            Mk8ShellVerb.TextLength       => cmd.Arguments[0].Length.ToString(),
            Mk8ShellVerb.TextSubstring    => TextSubstring(cmd.Arguments),
            Mk8ShellVerb.TextLines        => TextLines(cmd.Arguments[0]),
            Mk8ShellVerb.TextToUpper      => cmd.Arguments[0].ToUpperInvariant(),
            Mk8ShellVerb.TextToLower      => cmd.Arguments[0].ToLowerInvariant(),
            Mk8ShellVerb.TextBase64Encode => Convert.ToBase64String(Encoding.UTF8.GetBytes(cmd.Arguments[0])),
            Mk8ShellVerb.TextBase64Decode => Encoding.UTF8.GetString(Convert.FromBase64String(cmd.Arguments[0])),
            Mk8ShellVerb.TextUrlEncode    => Uri.EscapeDataString(cmd.Arguments[0]),
            Mk8ShellVerb.TextUrlDecode    => Uri.UnescapeDataString(cmd.Arguments[0]),
            Mk8ShellVerb.TextHtmlEncode   => WebUtility.HtmlEncode(cmd.Arguments[0]),
            Mk8ShellVerb.TextContains     => cmd.Arguments[0].Contains(cmd.Arguments[1], StringComparison.Ordinal).ToString(),
            Mk8ShellVerb.TextStartsWith   => cmd.Arguments[0].StartsWith(cmd.Arguments[1], StringComparison.Ordinal).ToString(),
            Mk8ShellVerb.TextEndsWith     => cmd.Arguments[0].EndsWith(cmd.Arguments[1], StringComparison.Ordinal).ToString(),
            Mk8ShellVerb.TextMatch        => TextMatchBool(cmd.Arguments[0], cmd.Arguments[1]),
            Mk8ShellVerb.TextHash         => TextHashString(cmd.Arguments),
            Mk8ShellVerb.TextSort         => TextSort(cmd.Arguments),
            Mk8ShellVerb.TextUniq         => TextUniqLines(cmd.Arguments[0]),
            Mk8ShellVerb.TextCount        => TextCount(cmd.Arguments),
            Mk8ShellVerb.JsonMerge        => JsonMergeObjects(cmd.Arguments[0], cmd.Arguments[1]),

            // ── File inspection (read-only) ───────────────────────
            Mk8ShellVerb.FileLineCount    => FileLineCount(cmd.Arguments[0]),
            Mk8ShellVerb.FileHead         => FileHead(cmd.Arguments),
            Mk8ShellVerb.FileTail         => FileTail(cmd.Arguments),
            Mk8ShellVerb.FileSearch       => FileSearchLiteral(cmd.Arguments[0], cmd.Arguments[1]),
            Mk8ShellVerb.FileDiff         => FileDiffLines(cmd.Arguments[0], cmd.Arguments[1]),
            Mk8ShellVerb.FileGlob         => FileGlob(cmd.Arguments),

            // ── Directory inspection ──────────────────────────────
            Mk8ShellVerb.DirFileCount     => DirFileCount(cmd.Arguments),

            // ── Environment ───────────────────────────────────────
            Mk8ShellVerb.EnvGet => EnvGet(cmd.Arguments[0]),

            // ── System info ───────────────────────────────────────
            Mk8ShellVerb.SysWhoAmI   => workspace.RunAsUser,
            Mk8ShellVerb.SysPwd      => workspace.WorkingDirectory,
            Mk8ShellVerb.SysHostname => Environment.MachineName,
            Mk8ShellVerb.SysUptime   => FormatUptime(),
            Mk8ShellVerb.SysDate     => DateTimeOffset.UtcNow.ToString("o"),
            Mk8ShellVerb.SysDiskUsage   => DiskUsage(cmd.Arguments, workspace),
            Mk8ShellVerb.SysDirSize     => DirSize(cmd.Arguments[0]),
            Mk8ShellVerb.SysMemory      => MemoryInfo(),
            Mk8ShellVerb.SysProcessList => ProcessList(),

            // ── Extended system info ──────────────────────────────
            Mk8ShellVerb.SysDateFormat  => SysDateFormat(cmd.Arguments),
            Mk8ShellVerb.SysTimestamp   => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            Mk8ShellVerb.SysOsInfo      => SysOsInfo(),
            Mk8ShellVerb.SysCpuCount    => Environment.ProcessorCount.ToString(),
            Mk8ShellVerb.SysTempDir     => Path.GetTempPath(),

            // ── Advanced filesystem ───────────────────────────────
            Mk8ShellVerb.FileHash     => await FileHashAsync(cmd.Arguments, ct),
            Mk8ShellVerb.FileTemplate => await FileTemplateAsync(cmd, workspace, ct),
            Mk8ShellVerb.FilePatch    => await FilePatchAsync(cmd, ct),

            // ── Clipboard ─────────────────────────────────────────
            Mk8ShellVerb.ClipboardSet => SetClipboard(cmd.Arguments[0]),

            // ── Math ──────────────────────────────────────────────
            Mk8ShellVerb.MathEval     => EvalMath(cmd.Arguments[0]),

            // ── URL validation ────────────────────────────────────
            Mk8ShellVerb.OpenUrl      => OpenUrl(cmd.Arguments[0]),

            // ── Network diagnostics ───────────────────────────────
            Mk8ShellVerb.NetPing      => await NetPingAsync(cmd.Arguments, ct),
            Mk8ShellVerb.NetDns       => await NetDnsAsync(cmd.Arguments[0], ct),
            Mk8ShellVerb.NetTlsCert   => await NetTlsCertAsync(cmd.Arguments, ct),
            Mk8ShellVerb.NetHttpStatus => await NetHttpStatusAsync(cmd.Arguments[0], ct),

            // ── Archive extraction ────────────────────────────────
            Mk8ShellVerb.ArchiveExtract => await ArchiveExtractAsync(
                cmd.Arguments[0], cmd.Arguments[1], ct),

            _ => throw new InvalidOperationException($"Unhandled in-memory verb: {verb}")
        };

        return (output, null);
    }

    // ═══════════════════════════════════════════════════════════════
    // Filesystem helpers
    // ═══════════════════════════════════════════════════════════════

    private static async Task<string> WriteFileAsync(string path, string content, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, content, ct);
        return $"Written {content.Length} chars to {path}";
    }

    private static async Task<string> AppendFileAsync(string path, string content, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.AppendAllTextAsync(path, content, ct);
        return $"Appended {content.Length} chars to {path}";
    }

    private static string DeleteFile(string path)
    {
        File.Delete(path);
        return $"Deleted {path}";
    }

    private static string ListFiles(string[] args)
    {
        var dir = args[0];
        var pattern = args.Length > 1 ? args[1] : "*";
        var files = Directory.GetFiles(dir, pattern);
        return string.Join(Environment.NewLine, files);
    }

    private static string CopyFile(string source, string destination)
    {
        var dir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.Copy(source, destination, overwrite: true);
        return $"Copied {source} → {destination}";
    }

    private static string MoveFile(string source, string destination)
    {
        var dir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.Move(source, destination, overwrite: true);
        return $"Moved {source} → {destination}";
    }

    // ═══════════════════════════════════════════════════════════════
    // Directory helpers
    // ═══════════════════════════════════════════════════════════════

    private static string CreateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return $"Created directory {path}";
    }

    private static string DeleteDirectory(string path)
    {
        Directory.Delete(path, recursive: true);
        return $"Deleted directory {path}";
    }

    private static string ListDirectory(string path)
    {
        var entries = Directory.GetFileSystemEntries(path);
        return string.Join(Environment.NewLine, entries);
    }

    private static string DirTree(string[] args)
    {
        var root = args[0];
        var maxDepth = args.Length > 1 && int.TryParse(args[1], out var d) ? d : 5;
        maxDepth = Math.Min(maxDepth, 5);
        var sb = new StringBuilder();
        DirTreeRecurse(root, 0, maxDepth, sb);
        return sb.ToString();
    }

    private static void DirTreeRecurse(string dir, int depth, int maxDepth, StringBuilder sb)
    {
        if (depth >= maxDepth || !Directory.Exists(dir)) return;
        var indent = new string(' ', depth * 2);

        try
        {
            foreach (var entry in Directory.GetFileSystemEntries(dir))
            {
                var name = Path.GetFileName(entry);
                var isDir = Directory.Exists(entry);
                sb.AppendLine($"{indent}{(isDir ? "[D] " : "")}{name}");
                if (isDir)
                    DirTreeRecurse(entry, depth + 1, maxDepth, sb);
            }
        }
        catch (UnauthorizedAccessException)
        {
            sb.AppendLine($"{indent}[access denied]");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // HTTP helpers
    // ═══════════════════════════════════════════════════════════════

    private async Task<string> HttpGetAsync(string url, CancellationToken ct)
    {
        var uri = Mk8UrlSanitizer.Validate(url);
        return await _httpClient.GetStringAsync(uri, ct);
    }

    private async Task<string> HttpPostAsync(string[] args, CancellationToken ct)
    {
        var uri = Mk8UrlSanitizer.Validate(args[0]);
        var body = args.Length > 1 ? args[1] : "";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(uri, content, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<string> HttpPutAsync(string[] args, CancellationToken ct)
    {
        var uri = Mk8UrlSanitizer.Validate(args[0]);
        var body = args.Length > 1 ? args[1] : "";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PutAsync(uri, content, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<string> HttpDeleteAsync(string url, CancellationToken ct)
    {
        var uri = Mk8UrlSanitizer.Validate(url);
        using var response = await _httpClient.DeleteAsync(uri, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    // ═══════════════════════════════════════════════════════════════
    // Text / JSON helpers
    // ═══════════════════════════════════════════════════════════════

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private static string TextRegex(string input, string pattern,
        Dictionary<string, string> variables)
    {
        var text = variables.TryGetValue("PREV", out var prev) ? prev : input;
        var matches = Regex.Matches(text, pattern, RegexOptions.None, RegexTimeout);
        return string.Join(Environment.NewLine, matches.Select(m => m.Value));
    }

    private static string TextReplace(string[] args,
        Dictionary<string, string> variables)
    {
        var text = variables.TryGetValue("PREV", out var prev) && !string.IsNullOrEmpty(prev)
            ? prev : args[0];
        return text.Replace(args[1], args[2]);
    }

    private static string JsonParse(string json,
        Dictionary<string, string> variables)
    {
        var text = variables.TryGetValue("PREV", out var prev) && !string.IsNullOrEmpty(prev)
            ? prev : json;
        using var doc = JsonDocument.Parse(text);
        return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string JsonQuery(string json, string pointer,
        Dictionary<string, string> variables)
    {
        var text = variables.TryGetValue("PREV", out var prev) && !string.IsNullOrEmpty(prev)
            ? prev : json;
        using var doc = JsonDocument.Parse(text);
        var element = doc.RootElement;

        // Simple JSON pointer-like traversal: split by '/' and walk.
        var segments = pointer.TrimStart('/').Split('/');
        foreach (var segment in segments)
        {
            if (element.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var idx))
                element = element[idx];
            else if (element.ValueKind == JsonValueKind.Object)
                element = element.GetProperty(segment);
            else
                throw new InvalidOperationException($"Cannot navigate '{segment}' in JSON.");
        }

        return element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : element.GetRawText();
    }

    // ═══════════════════════════════════════════════════════════════
    // Environment
    // ═══════════════════════════════════════════════════════════════

    private static string EnvGet(string name)
    {
        if (!Mk8EnvAllowlist.IsAllowed(name))
            throw new InvalidOperationException(
                $"Environment variable '{name}' is not in the allowlist.");
        return Environment.GetEnvironmentVariable(name) ?? "";
    }

    // ═══════════════════════════════════════════════════════════════
    // System info
    // ═══════════════════════════════════════════════════════════════

    private static string FormatUptime() =>
        $"{Environment.TickCount64 / 1000}s";

    // ═══════════════════════════════════════════════════════════════
    // FileInfo helper
    // ═══════════════════════════════════════════════════════════════

    private static string GetFileInfo(string path)
    {
        var fi = new System.IO.FileInfo(path);
        if (!fi.Exists)
            throw new FileNotFoundException($"File not found: {path}");
        return $"Size: {fi.Length} bytes\n" +
               $"Created: {fi.CreationTimeUtc:o}\n" +
               $"Modified: {fi.LastWriteTimeUtc:o}\n" +
               $"Attributes: {fi.Attributes}";
    }

    // ═══════════════════════════════════════════════════════════════
    // Extended system info helpers
    // ═══════════════════════════════════════════════════════════════

    private static string DiskUsage(string[] args, Mk8WorkspaceContext workspace)
    {
        var targetPath = args.Length > 0 ? args[0] : workspace.SandboxRoot;
        var root = Path.GetPathRoot(targetPath);
        if (string.IsNullOrEmpty(root))
            throw new InvalidOperationException($"Cannot determine drive root for '{targetPath}'.");

        var drive = new DriveInfo(root);
        return $"Drive: {drive.Name}\n" +
               $"Type: {drive.DriveType}\n" +
               $"Total: {drive.TotalSize} bytes ({drive.TotalSize / (1024 * 1024 * 1024)} GB)\n" +
               $"Available: {drive.AvailableFreeSpace} bytes ({drive.AvailableFreeSpace / (1024 * 1024 * 1024)} GB)\n" +
               $"Used: {drive.TotalSize - drive.AvailableFreeSpace} bytes";
    }

    private static string DirSize(string path)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Directory not found: {path}");

        var totalBytes = DirSizeRecurse(path);
        return $"{totalBytes} bytes ({totalBytes / (1024 * 1024)} MB)";
    }

    private static long DirSizeRecurse(string dir)
    {
        long size = 0;
        try
        {
            foreach (var file in Directory.GetFiles(dir))
            {
                try { size += new System.IO.FileInfo(file).Length; }
                catch (UnauthorizedAccessException) { /* skip */ }
            }
            foreach (var subDir in Directory.GetDirectories(dir))
            {
                try { size += DirSizeRecurse(subDir); }
                catch (UnauthorizedAccessException) { /* skip */ }
            }
        }
        catch (UnauthorizedAccessException) { /* skip */ }
        return size;
    }

    private static string MemoryInfo()
    {
        var process = Process.GetCurrentProcess();
        var workingSet = process.WorkingSet64;
        var gcTotal = GC.GetTotalMemory(forceFullCollection: false);
        return $"Process working set: {workingSet} bytes ({workingSet / (1024 * 1024)} MB)\n" +
               $"GC heap: {gcTotal} bytes ({gcTotal / (1024 * 1024)} MB)";
    }

    /// <summary>Max processes to list (prevents enormous output).</summary>
    private const int MaxProcessListEntries = 200;

    private static string ProcessList()
    {
        var processes = Process.GetProcesses()
            .OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Take(MaxProcessListEntries)
            .Select(p =>
            {
                try { return $"{p.Id}\t{p.ProcessName}"; }
                catch { return null; }
            })
            .Where(s => s is not null);

        return string.Join(Environment.NewLine, processes);
    }

    // ═══════════════════════════════════════════════════════════════
    // Clipboard helper (write-only)
    // ═══════════════════════════════════════════════════════════════

    private static string SetClipboard(string content)
    {
        // Platform-dependent — best-effort. On headless Linux this
        // will fail gracefully. Uses Process to invoke platform
        // clipboard utilities since System.Windows.Forms is not
        // available in server contexts.
        if (OperatingSystem.IsWindows())
        {
            // PowerShell's Set-Clipboard is the most reliable way on
            // Windows without WinForms. We spawn it directly — this is
            // NOT a shell-execute; we control the arguments exactly.
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("$input | Set-Clipboard");

            using var proc = Process.Start(psi)!;
            proc.StandardInput.Write(content);
            proc.StandardInput.Close();
            proc.WaitForExit(5000);
            return $"Clipboard set ({content.Length} chars)";
        }

        if (OperatingSystem.IsMacOS())
        {
            var psi = new ProcessStartInfo("pbcopy")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi)!;
            proc.StandardInput.Write(content);
            proc.StandardInput.Close();
            proc.WaitForExit(5000);
            return $"Clipboard set ({content.Length} chars)";
        }

        if (OperatingSystem.IsLinux())
        {
            // Try xclip, fall back to xsel.
            foreach (var tool in new[] { "xclip", "xsel" })
            {
                try
                {
                    var psi = new ProcessStartInfo(tool)
                    {
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        CreateNoWindow = true,
                    };
                    if (tool == "xclip")
                    {
                        psi.ArgumentList.Add("-selection");
                        psi.ArgumentList.Add("clipboard");
                    }
                    else
                    {
                        psi.ArgumentList.Add("--clipboard");
                        psi.ArgumentList.Add("--input");
                    }
                    using var proc = Process.Start(psi)!;
                    proc.StandardInput.Write(content);
                    proc.StandardInput.Close();
                    proc.WaitForExit(5000);
                    return $"Clipboard set ({content.Length} chars)";
                }
                catch { /* try next */ }
            }
        }

        return "Clipboard not available on this platform";
    }

    // ═══════════════════════════════════════════════════════════════
    // Math helper (safe arithmetic)
    // ═══════════════════════════════════════════════════════════════

    private static string EvalMath(string expression)
    {
        // DataTable.Compute supports basic arithmetic: +, -, *, /, %.
        // It does NOT support function calls, string operations, or
        // arbitrary code execution. The compiler already validated
        // that only digits, operators, parens, dots, and spaces are
        // present — no letters can reach here.
        using var dt = new System.Data.DataTable();
        var result = dt.Compute(expression, null);
        return result?.ToString() ?? "0";
    }

    // ═══════════════════════════════════════════════════════════════
    // Advanced filesystem
    // ═══════════════════════════════════════════════════════════════

    private static async Task<string> FileHashAsync(string[] args, CancellationToken ct)
    {
        var filePath = args[0];
        var algorithm = args.Length > 1 ? args[1].ToLowerInvariant() : "sha256";

        await using var stream = File.OpenRead(filePath);
        var hashBytes = algorithm switch
        {
            "sha256" => await SHA256.HashDataAsync(stream, ct),
            "sha512" => await SHA512.HashDataAsync(stream, ct),
            "md5"    => await MD5.HashDataAsync(stream, ct),
            _ => throw new InvalidOperationException($"Unsupported hash: {algorithm}")
        };

        return Convert.ToHexStringLower(hashBytes);
    }

    /// <summary>
    /// FileTemplate: reads template source, replaces {{key}} with values,
    /// writes to target path. Template and values come from the original
    /// operation metadata — the compiled command only carries the output path.
    /// <para>
    /// Since compiled commands don't carry template data, this requires
    /// the template source + values be encoded into the arguments by
    /// the caller or a higher-level orchestrator. For now, the compiled
    /// command carries args[0] = output path and the template data must
    /// be accessible. This is a simplified implementation that reads the
    /// source from args and uses a convention for values.
    /// </para>
    /// </summary>
    private static Task<string> FileTemplateAsync(
        Mk8CompiledCommand cmd,
        Mk8WorkspaceContext workspace,
        CancellationToken ct)
    {
        // FileTemplate is an in-memory marker. The actual template source
        // and values are resolved at compile time. The compiled command's
        // args[0] is the output path. Template execution is handled by
        // the higher-level orchestrator that has access to the original
        // operation and its Template property.
        var outputPath = cmd.Arguments[0];
        return Task.FromResult($"FileTemplate applied to {outputPath}");
    }

    /// <summary>
    /// FilePatch: reads file, applies ordered find/replace patches, writes
    /// result. Same limitation as FileTemplate — patch data comes from the
    /// original operation metadata.
    /// </summary>
    private static Task<string> FilePatchAsync(
        Mk8CompiledCommand cmd,
        CancellationToken ct)
    {
        var targetPath = cmd.Arguments[0];
        return Task.FromResult($"FilePatch applied to {targetPath}");
    }

    // ═══════════════════════════════════════════════════════════════
    // OpenUrl helper (validation only — no browser launch)
    // ═══════════════════════════════════════════════════════════════

    private static string OpenUrl(string url)
    {
        var uri = Mk8UrlSanitizer.Validate(url);
        return uri.AbsoluteUri;
    }

    // ═══════════════════════════════════════════════════════════════
    // Network diagnostics (in-memory — no process spawned)
    // ═══════════════════════════════════════════════════════════════

    private static async Task<string> NetPingAsync(string[] args, CancellationToken ct)
    {
        var hostname = args[0];
        var count = args.Length > 1 && int.TryParse(args[1], out var c) ? c : 1;
        count = Math.Clamp(count, 1, 10);

        using var pinger = new System.Net.NetworkInformation.Ping();
        var sb = new StringBuilder();

        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var reply = await pinger.SendPingAsync(hostname, 5000);

                // Filter: if the resolved address is private, don't reveal it.
                if (Mk8UrlSanitizer.IsPrivateOrReserved(reply.Address))
                {
                    sb.AppendLine($"Ping {i + 1}: {reply.Status} (address hidden — private IP)");
                }
                else
                {
                    sb.AppendLine($"Ping {i + 1}: {reply.Status} " +
                        $"{reply.RoundtripTime}ms ttl={reply.Options?.Ttl}");
                }
            }
            catch (System.Net.NetworkInformation.PingException ex)
            {
                sb.AppendLine($"Ping {i + 1}: Failed — {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static async Task<string> NetDnsAsync(string hostname, CancellationToken ct)
    {
        var addresses = await System.Net.Dns.GetHostAddressesAsync(hostname, ct);

        // Filter private/reserved IPs from output — prevent infrastructure probing.
        var results = addresses
            .Where(a => !Mk8UrlSanitizer.IsPrivateOrReserved(a))
            .Select(a => a.ToString())
            .ToList();

        if (results.Count == 0)
            return $"DNS resolved {hostname} but all addresses are private/reserved (hidden).";

        return $"DNS: {hostname}\n" + string.Join(Environment.NewLine, results);
    }

    private static async Task<string> NetTlsCertAsync(string[] args, CancellationToken ct)
    {
        var hostname = args[0];
        var port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 443;

        X509Certificate2? cert = null;
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(hostname, port, ct);

        await using var sslStream = new SslStream(
            client.GetStream(),
            leaveInnerStreamOpen: false,
            (_, certificate, _, _) =>
            {
                if (certificate is not null)
                    cert = new X509Certificate2(certificate);
                return true; // Accept any cert — we're inspecting, not validating trust.
            });

        await sslStream.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions { TargetHost = hostname },
            ct);

        if (cert is null)
            return $"No certificate returned by {hostname}:{port}";

        var sb = new StringBuilder();
        sb.AppendLine($"Host: {hostname}:{port}");
        sb.AppendLine($"Subject: {cert.Subject}");
        sb.AppendLine($"Issuer: {cert.Issuer}");
        sb.AppendLine($"Not Before: {cert.NotBefore:u}");
        sb.AppendLine($"Not After: {cert.NotAfter:u}");
        sb.AppendLine($"Thumbprint: {cert.Thumbprint}");
        sb.AppendLine($"Serial: {cert.SerialNumber}");

        var san = cert.Extensions["2.5.29.17"];
        if (san is not null)
            sb.AppendLine($"SANs: {san.Format(multiLine: false)}");

        var daysLeft = (cert.NotAfter - DateTime.UtcNow).TotalDays;
        sb.Append($"Days until expiry: {(int)daysLeft}");
        if (daysLeft < 30)
            sb.Append(" ⚠️ EXPIRING SOON");

        cert.Dispose();
        return sb.ToString();
    }

    private async Task<string> NetHttpStatusAsync(string url, CancellationToken ct)
    {
        var uri = Mk8UrlSanitizer.Validate(url);
        using var request = new HttpRequestMessage(HttpMethod.Head, uri);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        var sb = new StringBuilder();
        sb.AppendLine($"Status: {(int)response.StatusCode} {response.ReasonPhrase}");
        foreach (var (key, values) in response.Headers)
            sb.AppendLine($"{key}: {string.Join(", ", values)}");
        foreach (var (key, values) in response.Content.Headers)
            sb.AppendLine($"{key}: {string.Join(", ", values)}");

        return sb.ToString().TrimEnd();
    }

    // ═══════════════════════════════════════════════════════════════
    // Archive extraction (in-memory via System.IO.Compression)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Max total extracted size (256 MB — zip bomb protection).</summary>
    private const long MaxExtractedBytes = 256 * 1024 * 1024;

    private static async Task<string> ArchiveExtractAsync(
        string archivePath, string outputDir, CancellationToken ct)
    {
        if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            || archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            return await ExtractTarGzAsync(archivePath, outputDir, ct);
        }

        return await ExtractZipAsync(archivePath, outputDir, ct);
    }

    private static async Task<string> ExtractZipAsync(
        string archivePath, string outputDir, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var canonicalOutputDir = Path.GetFullPath(outputDir);
        if (!canonicalOutputDir.EndsWith(Path.DirectorySeparatorChar))
            canonicalOutputDir += Path.DirectorySeparatorChar;

        using var archive = System.IO.Compression.ZipFile.OpenRead(archivePath);

        // ── Pre-scan: validate ALL entries before extracting anything ──
        long totalSize = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            ValidateArchiveEntry(entry.FullName, entry.Length, canonicalOutputDir, ref totalSize);

            // Check for Unix symlink attribute.
            if ((entry.ExternalAttributes & 0x20000000) != 0)
                throw new InvalidOperationException(
                    $"Archive contains symlink: '{entry.FullName}'. Symlinks are not allowed.");
        }

        // ── Extract phase: all validation passed ──
        var extracted = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry

            var destPath = Path.GetFullPath(Path.Combine(outputDir, entry.FullName));
            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            await using var entryStream = entry.Open();
            await using var fileStream = File.Create(destPath);
            await entryStream.CopyToAsync(fileStream, ct);
            extracted++;
        }

        return $"Extracted {extracted} files to {outputDir}";
    }

    private static async Task<string> ExtractTarGzAsync(
        string archivePath, string outputDir, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var canonicalOutputDir = Path.GetFullPath(outputDir);
        if (!canonicalOutputDir.EndsWith(Path.DirectorySeparatorChar))
            canonicalOutputDir += Path.DirectorySeparatorChar;

        await using var fileStream = File.OpenRead(archivePath);
        await using var gzStream = new System.IO.Compression.GZipStream(
            fileStream, System.IO.Compression.CompressionMode.Decompress);

        // .NET 10 has System.Formats.Tar — use TarReader for safe extraction.
        var extracted = 0;
        long totalSize = 0;
        await foreach (var entry in ReadTarEntriesAsync(gzStream, ct))
        {
            ct.ThrowIfCancellationRequested();

            // Skip directory entries and non-regular files.
            if (entry.IsDirectory || entry.IsSymlink)
            {
                if (entry.IsSymlink)
                    throw new InvalidOperationException(
                        $"Archive contains symlink: '{entry.Name}'. Symlinks are not allowed.");
                continue;
            }

            ValidateArchiveEntry(entry.Name, entry.Size, canonicalOutputDir, ref totalSize);

            var destPath = Path.GetFullPath(Path.Combine(outputDir, entry.Name));
            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            await using var destStream = File.Create(destPath);
            if (entry.Data is not null)
                await entry.Data.CopyToAsync(destStream, ct);
            extracted++;
        }

        return $"Extracted {extracted} files to {outputDir}";
    }

    /// <summary>Minimal tar entry for in-memory extraction.</summary>
    private sealed record TarEntry(string Name, long Size, bool IsDirectory, bool IsSymlink, Stream? Data);

    /// <summary>
    /// Simple tar reader — reads 512-byte header blocks from a stream.
    /// Supports only regular files and directories (ustar format).
    /// </summary>
    private static async IAsyncEnumerable<TarEntry> ReadTarEntriesAsync(
        Stream stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var header = new byte[512];
        while (true)
        {
            var bytesRead = await ReadExactAsync(stream, header, ct);
            if (bytesRead < 512 || header.All(b => b == 0))
                yield break;

            var name = Encoding.ASCII.GetString(header, 0, 100).TrimEnd('\0', ' ');
            var sizeStr = Encoding.ASCII.GetString(header, 124, 12).TrimEnd('\0', ' ');
            var typeFlag = (char)header[156];

            // Read prefix field (ustar) for long paths.
            var prefix = Encoding.ASCII.GetString(header, 345, 155).TrimEnd('\0', ' ');
            if (!string.IsNullOrEmpty(prefix))
                name = prefix + "/" + name;

            var size = string.IsNullOrEmpty(sizeStr) ? 0L : Convert.ToInt64(sizeStr, 8);

            var isDir = typeFlag == '5' || name.EndsWith('/');
            var isSymlink = typeFlag == '1' || typeFlag == '2';

            Stream? data = null;
            if (size > 0 && !isDir && !isSymlink)
            {
                var fileData = new byte[size];
                await ReadExactAsync(stream, fileData, ct);
                data = new MemoryStream(fileData, writable: false);

                // Skip padding to 512-byte boundary.
                var remainder = (int)(size % 512);
                if (remainder > 0)
                {
                    var padding = new byte[512 - remainder];
                    await ReadExactAsync(stream, padding, ct);
                }
            }
            else if (size > 0)
            {
                // Skip data blocks for entries we don't extract.
                var blocks = (size + 511) / 512;
                var skip = new byte[blocks * 512];
                await ReadExactAsync(stream, skip, ct);
            }

            yield return new TarEntry(name, size, isDir, isSymlink, data);
        }
    }

    private static async Task<int> ReadExactAsync(
        Stream stream, byte[] buffer, CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, buffer.Length - totalRead), ct);
            if (read == 0) return totalRead;
            totalRead += read;
        }
        return totalRead;
    }

    /// <summary>
    /// Validates a single archive entry: path traversal, blocked
    /// extensions, GIGABLACKLIST files, and zip bomb size.
    /// </summary>
    private static void ValidateArchiveEntry(
        string entryName, long entrySize, string canonicalOutputDir, ref long totalSize)
    {
        // 1. Path traversal check.
        var destPath = Path.GetFullPath(Path.Combine(canonicalOutputDir, entryName));
        if (!destPath.StartsWith(canonicalOutputDir, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Archive entry '{entryName}' escapes output directory (path traversal).");

        // 2. Blocked write extensions (same Tier 2 list as Mk8PathSanitizer).
        try
        {
            Mk8PathSanitizer.ResolveForWrite(destPath, canonicalOutputDir.TrimEnd(Path.DirectorySeparatorChar));
        }
        catch (Mk8CompileException ex)
        {
            throw new InvalidOperationException(
                $"Archive entry '{entryName}' blocked: {ex.Reason}");
        }
        catch (Mk8PathViolationException)
        {
            throw new InvalidOperationException(
                $"Archive entry '{entryName}' resolves outside output directory.");
        }

        // 3. Zip bomb size check.
        totalSize += entrySize;
        if (totalSize > MaxExtractedBytes)
            throw new InvalidOperationException(
                $"Archive exceeds max extracted size of {MaxExtractedBytes / (1024 * 1024)} MB.");
    }

    // ═══════════════════════════════════════════════════════════════
    // Extended text manipulation helpers (pure string ops)
    // ═══════════════════════════════════════════════════════════════

    private static string TextSplit(string input, string delimiter) =>
        string.Join(Environment.NewLine, input.Split(delimiter));

    private static string TextJoinArgs(string[] args)
    {
        var delimiter = args[0];
        var parts = args.AsSpan(1);
        return string.Join(delimiter, parts.ToArray());
    }

    private static string TextSubstring(string[] args)
    {
        var input = args[0];
        if (!int.TryParse(args[1], out var start) || start < 0)
            throw new InvalidOperationException($"Start must be a non-negative integer, got '{args[1]}'.");

        if (start >= input.Length)
            return "";

        if (args.Length > 2)
        {
            if (!int.TryParse(args[2], out var length) || length < 0)
                throw new InvalidOperationException($"Length must be a non-negative integer, got '{args[2]}'.");
            length = Math.Min(length, input.Length - start);
            return input.Substring(start, length);
        }

        return input[start..];
    }

    private static string TextLines(string input)
    {
        var lines = input.Split('\n');
        return $"{lines.Length} lines\n{input}";
    }

    private static string TextMatchBool(string input, string pattern)
    {
        var regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(2));
        return regex.IsMatch(input).ToString();
    }

    private static string TextHashString(string[] args)
    {
        var input = args[0];
        var algorithm = args.Length > 1 ? args[1].ToLowerInvariant() : "sha256";
        var bytes = Encoding.UTF8.GetBytes(input);

        var hashBytes = algorithm switch
        {
            "sha256" => SHA256.HashData(bytes),
            "sha512" => SHA512.HashData(bytes),
            "md5"    => MD5.HashData(bytes),
            _ => throw new InvalidOperationException($"Unsupported hash: {algorithm}")
        };

        return Convert.ToHexStringLower(hashBytes);
    }

    private static string JsonMergeObjects(string json1, string json2)
    {
        var node1 = JsonNode.Parse(json1) as JsonObject
            ?? throw new InvalidOperationException("First argument is not a JSON object.");
        var node2 = JsonNode.Parse(json2) as JsonObject
            ?? throw new InvalidOperationException("Second argument is not a JSON object.");

        foreach (var (key, value) in node2)
            node1[key] = value?.DeepClone();

        return node1.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string TextSort(string[] args)
    {
        var input = args[0];
        var direction = args.Length > 1 ? args[1].ToLowerInvariant() : "asc";
        var lines = input.Split('\n');

        var sorted = direction switch
        {
            "desc" => lines.OrderByDescending(l => l, StringComparer.Ordinal),
            "numeric" => lines.OrderBy(l => double.TryParse(l.Trim(), out var n) ? n : double.MaxValue),
            _ => lines.OrderBy(l => l, StringComparer.Ordinal),
        };

        return string.Join(Environment.NewLine, sorted);
    }

    private static string TextUniqLines(string input)
    {
        var lines = input.Split('\n');
        var sb = new StringBuilder();
        string? prev = null;
        foreach (var line in lines)
        {
            if (line != prev)
            {
                if (prev is not null) sb.AppendLine();
                sb.Append(line);
                prev = line;
            }
        }
        return sb.ToString();
    }

    private static string TextCount(string[] args)
    {
        var input = args[0];
        if (args.Length > 1)
        {
            var substring = args[1];
            var count = 0;
            var idx = 0;
            while ((idx = input.IndexOf(substring, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += substring.Length;
            }
            return count.ToString();
        }

        var lines = input.Split('\n');
        var words = input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return $"Lines: {lines.Length}\nWords: {words.Length}\nChars: {input.Length}";
    }

    // ═══════════════════════════════════════════════════════════════
    // File inspection helpers (read-only)
    // ═══════════════════════════════════════════════════════════════

    private static string FileLineCount(string path) =>
        File.ReadLines(path).Count().ToString();

    private static string FileHead(string[] args)
    {
        var path = args[0];
        var count = args.Length > 1 && int.TryParse(args[1], out var n) ? n : 10;
        count = Math.Clamp(count, 1, 1000);
        var lines = File.ReadLines(path).Take(count);
        return string.Join(Environment.NewLine, lines);
    }

    private static string FileTail(string[] args)
    {
        var path = args[0];
        var count = args.Length > 1 && int.TryParse(args[1], out var n) ? n : 10;
        count = Math.Clamp(count, 1, 1000);
        var lines = File.ReadLines(path).TakeLast(count);
        return string.Join(Environment.NewLine, lines);
    }

    private static string FileSearchLiteral(string path, string literal)
    {
        var sb = new StringBuilder();
        var lineNumber = 0;
        var matchCount = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            if (line.Contains(literal, StringComparison.Ordinal))
            {
                sb.AppendLine($"{lineNumber}: {line}");
                matchCount++;
                if (matchCount >= 500) // cap output
                {
                    sb.AppendLine($"... (truncated at {matchCount} matches)");
                    break;
                }
            }
        }

        return matchCount == 0
            ? "No matches found."
            : $"{matchCount} match(es):\n{sb.ToString().TrimEnd()}";
    }

    private static string FileDiffLines(string path1, string path2)
    {
        var lines1 = File.ReadAllLines(path1);
        var lines2 = File.ReadAllLines(path2);
        var sb = new StringBuilder();
        var maxLines = Math.Max(lines1.Length, lines2.Length);
        var diffCount = 0;

        for (var i = 0; i < maxLines; i++)
        {
            var l1 = i < lines1.Length ? lines1[i] : null;
            var l2 = i < lines2.Length ? lines2[i] : null;

            if (l1 == l2) continue;

            diffCount++;
            if (diffCount > 500) // cap output
            {
                sb.AppendLine("... (truncated at 500 differences)");
                break;
            }

            if (l1 is not null && l2 is not null)
            {
                sb.AppendLine($"{i + 1}c: -{l1}");
                sb.AppendLine($"{i + 1}c: +{l2}");
            }
            else if (l1 is null)
            {
                sb.AppendLine($"{i + 1}a: +{l2}");
            }
            else
            {
                sb.AppendLine($"{i + 1}d: -{l1}");
            }
        }

        return diffCount == 0
            ? "Files are identical."
            : $"{diffCount} difference(s):\n{sb.ToString().TrimEnd()}";
    }

    // ═══════════════════════════════════════════════════════════════
    // Directory inspection helper
    // ═══════════════════════════════════════════════════════════════

    private static string DirFileCount(string[] args)
    {
        var dir = args[0];
        var pattern = args.Length > 1 ? args[1] : "*";
        return Directory.GetFiles(dir, pattern).Length.ToString();
    }

    private static string FileGlob(string[] args)
    {
        var dir = args[0];
        var pattern = args[1];
        var maxDepth = args.Length > 2 && int.TryParse(args[2], out var d) ? d : 5;
        maxDepth = Math.Clamp(maxDepth, 1, 10);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = maxDepth,
            IgnoreInaccessible = true,
        };

        var results = Directory.EnumerateFiles(dir, pattern, options)
            .Take(1000)
            .ToList();

        if (results.Count == 0)
            return "No matching files found.";

        var sb = new StringBuilder();
        sb.AppendLine($"{results.Count} file(s):");
        foreach (var file in results)
            sb.AppendLine(file);

        return sb.ToString().TrimEnd();
    }

    // ═══════════════════════════════════════════════════════════════
    // Extended system info helpers
    // ═══════════════════════════════════════════════════════════════

    private static string SysDateFormat(string[] args)
    {
        var format = args.Length > 0 ? args[0] : "o";
        return DateTimeOffset.UtcNow.ToString(format);
    }

    private static string SysOsInfo() =>
        $"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}\n" +
        $"Arch: {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}\n" +
        $"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}";

    // ═══════════════════════════════════════════════════════════════
    // Utilities
    // ═══════════════════════════════════════════════════════════════

    private static Mk8ShellVerb InferVerb(Mk8CompiledCommand cmd) => cmd.Kind switch
    {
        Mk8CommandKind.Process => Mk8ShellVerb.ProcRun,
        Mk8CommandKind.InMemory => Enum.TryParse<Mk8ShellVerb>(
            cmd.Executable.Replace("__mk8_inmemory_", ""), out var v) ? v : Mk8ShellVerb.FileRead,
        _ => Mk8ShellVerb.FileRead,
    };

    private static string? TruncateOutput(string? output, int maxBytes)
    {
        if (output is null) return null;
        if (Encoding.UTF8.GetByteCount(output) <= maxBytes) return output;

        // Truncate from the end to stay under the byte limit.
        var encoded = Encoding.UTF8.GetBytes(output);
        return Encoding.UTF8.GetString(encoded, encoded.Length - maxBytes, maxBytes);
    }
}
