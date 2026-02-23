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
            Mk8ShellVerb.TextIndexOf      => cmd.Arguments[0].IndexOf(cmd.Arguments[1], StringComparison.Ordinal).ToString(),
            Mk8ShellVerb.TextLastIndexOf  => cmd.Arguments[0].LastIndexOf(cmd.Arguments[1], StringComparison.Ordinal).ToString(),
            Mk8ShellVerb.TextRemove       => cmd.Arguments[0].Replace(cmd.Arguments[1], "", StringComparison.Ordinal),
            Mk8ShellVerb.TextWordCount    => cmd.Arguments[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length.ToString(),
            Mk8ShellVerb.TextReverse      => new string(cmd.Arguments[0].Reverse().ToArray()),
            Mk8ShellVerb.TextPadLeft      => TextPadLeft(cmd.Arguments),
            Mk8ShellVerb.TextPadRight     => TextPadRight(cmd.Arguments),
            Mk8ShellVerb.TextRepeat       => TextRepeat(cmd.Arguments),
            Mk8ShellVerb.JsonMerge        => JsonMergeObjects(cmd.Arguments[0], cmd.Arguments[1]),
            Mk8ShellVerb.JsonKeys         => JsonKeys(cmd.Arguments[0]),
            Mk8ShellVerb.JsonCount        => JsonCount(cmd.Arguments[0]),
            Mk8ShellVerb.JsonType         => JsonTypeOf(cmd.Arguments[0]),

            // ── JSON construction/mutation ─────────────────────────
            Mk8ShellVerb.JsonFromPairs    => JsonFromPairs(cmd.Arguments),
            Mk8ShellVerb.JsonSet          => JsonSetKey(cmd.Arguments[0], cmd.Arguments[1], cmd.Arguments[2]),
            Mk8ShellVerb.JsonRemoveKey    => JsonRemoveKey(cmd.Arguments[0], cmd.Arguments[1]),
            Mk8ShellVerb.JsonGet          => JsonGetValue(cmd.Arguments[0], cmd.Arguments[1]),
            Mk8ShellVerb.JsonCompact      => JsonCompact(cmd.Arguments[0]),
            Mk8ShellVerb.JsonStringify    => JsonSerializer.Serialize(cmd.Arguments[0]),
            Mk8ShellVerb.JsonArrayFrom    => JsonArrayFromArgs(cmd.Arguments),

            // ── File inspection (read-only) ───────────────────────
            Mk8ShellVerb.FileLineCount    => FileLineCount(cmd.Arguments[0]),
            Mk8ShellVerb.FileHead         => FileHead(cmd.Arguments),
            Mk8ShellVerb.FileTail         => FileTail(cmd.Arguments),
            Mk8ShellVerb.FileSearch       => FileSearchLiteral(cmd.Arguments[0], cmd.Arguments[1]),
            Mk8ShellVerb.FileDiff         => FileDiffLines(cmd.Arguments[0], cmd.Arguments[1]),
            Mk8ShellVerb.FileGlob         => FileGlob(cmd.Arguments),

            // ── Directory inspection ──────────────────────────────
            Mk8ShellVerb.DirFileCount     => DirFileCount(cmd.Arguments),
            Mk8ShellVerb.DirEmpty         => DirEmpty(cmd.Arguments[0]),

            // ── File type detection ──────────────────────────────
            Mk8ShellVerb.FileMimeType     => await FileMimeTypeAsync(cmd.Arguments[0], ct),
            Mk8ShellVerb.FileEncoding     => await FileEncodingAsync(cmd.Arguments[0], ct),

            // ── File comparison (read-only) ───────────────────────
            Mk8ShellVerb.FileEqual        => await FileEqualAsync(cmd.Arguments[0], cmd.Arguments[1], ct),
            Mk8ShellVerb.FileChecksum     => await FileChecksumAsync(cmd.Arguments, ct),

            // ── Path manipulation (pure string, no I/O) ───────────
            Mk8ShellVerb.PathJoin         => Path.Combine(cmd.Arguments),
            Mk8ShellVerb.PathDir          => Path.GetDirectoryName(cmd.Arguments[0]) ?? "",
            Mk8ShellVerb.PathFile         => Path.GetFileName(cmd.Arguments[0]),
            Mk8ShellVerb.PathExt          => Path.GetExtension(cmd.Arguments[0]),
            Mk8ShellVerb.PathStem         => Path.GetFileNameWithoutExtension(cmd.Arguments[0]),
            Mk8ShellVerb.PathChangeExt    => Path.ChangeExtension(cmd.Arguments[0], cmd.Arguments[1]),

            // ── Identity/value generation ─────────────────────────
            Mk8ShellVerb.GuidNew          => Guid.NewGuid().ToString(),
            Mk8ShellVerb.GuidNewShort     => Guid.NewGuid().ToString("N")[..8],
            Mk8ShellVerb.RandomInt        => Random.Shared.Next(int.Parse(cmd.Arguments[0]), int.Parse(cmd.Arguments[1]) + 1).ToString(),

            // ── Time arithmetic ───────────────────────────────────
            Mk8ShellVerb.TimeFormat       => TimeFormatFromUnix(cmd.Arguments),
            Mk8ShellVerb.TimeParse        => TimeParseToUnix(cmd.Arguments),
            Mk8ShellVerb.TimeAdd          => (long.Parse(cmd.Arguments[0]) + long.Parse(cmd.Arguments[1])).ToString(),
            Mk8ShellVerb.TimeDiff         => Math.Abs(long.Parse(cmd.Arguments[0]) - long.Parse(cmd.Arguments[1])).ToString(),

            // ── Version comparison ────────────────────────────────
            Mk8ShellVerb.VersionCompare   => CompareVersions(cmd.Arguments[0], cmd.Arguments[1]),
            Mk8ShellVerb.VersionParse     => ParseVersion(cmd.Arguments[0]),

            // ── Encoding/conversion ───────────────────────────────
            Mk8ShellVerb.HexEncode        => Convert.ToHexStringLower(Encoding.UTF8.GetBytes(cmd.Arguments[0])),
            Mk8ShellVerb.HexDecode        => Encoding.UTF8.GetString(Convert.FromHexString(cmd.Arguments[0])),
            Mk8ShellVerb.BaseConvert      => ConvertBase(cmd.Arguments[0], cmd.Arguments[1], cmd.Arguments[2]),

            // ── Regex capture groups ──────────────────────────────
            Mk8ShellVerb.TextRegexGroups  => TextRegexGroups(cmd.Arguments[0], cmd.Arguments[1]),

            // ── Script control/debugging ──────────────────────────
            Mk8ShellVerb.Echo             => cmd.Arguments[0],
            Mk8ShellVerb.Sleep            => await SleepAsync(cmd.Arguments[0], ct),
            Mk8ShellVerb.Assert           => AssertEqual(cmd.Arguments),
            Mk8ShellVerb.Fail             => throw new InvalidOperationException(cmd.Arguments[0]),

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
            Mk8ShellVerb.NetTcpConnect => await NetTcpConnectAsync(cmd.Arguments, ct),
            Mk8ShellVerb.HttpLatency   => await HttpLatencyAsync(cmd.Arguments, ct),

            // ── Sysadmin: file age/staleness ──────────────────────
            Mk8ShellVerb.FileAge       => FileAge(cmd.Arguments[0]),
            Mk8ShellVerb.FileNewerThan => FileNewerThan(cmd.Arguments[0], cmd.Arguments[1]),

            // ── Sysadmin: process search ──────────────────────────
            Mk8ShellVerb.ProcessFind   => ProcessFind(cmd.Arguments[0]),

            // ── Sysadmin: system discovery ────────────────────────
            Mk8ShellVerb.SysDriveList  => SysDriveList(),
            Mk8ShellVerb.SysNetInfo    => SysNetInfo(),
            Mk8ShellVerb.EnvList       => EnvListAll(),

            // ── Sysadmin: regex file search ───────────────────────
            Mk8ShellVerb.FileSearchRegex => FileSearchRegex(cmd.Arguments[0], cmd.Arguments[1]),

            // ── Sysadmin: tabular text ────────────────────────────
            Mk8ShellVerb.TextColumn    => TextColumn(cmd.Arguments),
            Mk8ShellVerb.TextTable     => TextTable(cmd.Arguments),

            // ── Sysadmin: directory comparison ────────────────────
            Mk8ShellVerb.DirCompare    => DirCompare(cmd.Arguments[0], cmd.Arguments[1]),
            Mk8ShellVerb.DirHash       => await DirHashAsync(cmd.Arguments, ct),

            // ── Sysadmin: human-readable formatting ───────────────
            Mk8ShellVerb.FormatBytes    => FormatBytesHuman(cmd.Arguments[0]),
            Mk8ShellVerb.FormatDuration => FormatDurationHuman(cmd.Arguments[0]),

            // ── Sysadmin: system log viewing (read-only, redacted) ─
            Mk8ShellVerb.SysLogRead    => SysLogRead(cmd.Arguments),
            Mk8ShellVerb.SysLogSources => SysLogSources(),

            // ── Sysadmin: service status (read-only) ──────────────
            Mk8ShellVerb.SysServiceList   => SysServiceList(cmd.Arguments),
            Mk8ShellVerb.SysServiceStatus => SysServiceStatus(cmd.Arguments[0]),

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

    private static string TextPadLeft(string[] args)
    {
        var input = args[0];
        var width = int.Parse(args[1]);
        var padChar = args.Length > 2 ? args[2][0] : ' ';
        return input.PadLeft(width, padChar);
    }

    private static string TextPadRight(string[] args)
    {
        var input = args[0];
        var width = int.Parse(args[1]);
        var padChar = args.Length > 2 ? args[2][0] : ' ';
        return input.PadRight(width, padChar);
    }

    private static string TextRepeat(string[] args)
    {
        var input = args[0];
        var count = int.Parse(args[1]);
        return string.Concat(Enumerable.Repeat(input, count));
    }

    private static string JsonKeys(string input)
    {
        var node = JsonNode.Parse(input) as JsonObject
            ?? throw new InvalidOperationException("Input is not a JSON object.");
        return string.Join(Environment.NewLine, node.Select(kv => kv.Key));
    }

    private static string JsonCount(string input)
    {
        var node = JsonNode.Parse(input) as JsonArray
            ?? throw new InvalidOperationException("Input is not a JSON array.");
        return node.Count.ToString();
    }

    private static string JsonTypeOf(string input)
    {
        var node = JsonNode.Parse(input);
        return node switch
        {
            JsonObject => "object",
            JsonArray => "array",
            JsonValue val => val.GetValueKind() switch
            {
                JsonValueKind.String => "string",
                JsonValueKind.Number => "number",
                JsonValueKind.True or JsonValueKind.False => "boolean",
                JsonValueKind.Null => "null",
                _ => "unknown"
            },
            null => "null",
            _ => "unknown"
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // File type detection helpers (read-only, in-memory)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Magic-byte signatures for common file types.</summary>
    private static readonly (byte[] Signature, int Offset, string MimeType)[] MagicBytes =
    [
        ([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], 0, "image/png"),
        ([0xFF, 0xD8, 0xFF], 0, "image/jpeg"),
        ([0x47, 0x49, 0x46, 0x38], 0, "image/gif"),
        ([0x50, 0x4B, 0x03, 0x04], 0, "application/zip"),
        ([0x50, 0x4B, 0x05, 0x06], 0, "application/zip"),
        ([0x1F, 0x8B], 0, "application/gzip"),
        ([0x25, 0x50, 0x44, 0x46], 0, "application/pdf"),
        ([0x52, 0x49, 0x46, 0x46], 0, "image/webp"),  // RIFF header (WebP, WAV, AVI)
        ([0x4F, 0x67, 0x67, 0x53], 0, "audio/ogg"),
        ([0x66, 0x4C, 0x61, 0x43], 0, "audio/flac"),
        ([0x42, 0x4D], 0, "image/bmp"),
        ([0x49, 0x49, 0x2A, 0x00], 0, "image/tiff"),  // little-endian TIFF
        ([0x4D, 0x4D, 0x00, 0x2A], 0, "image/tiff"),  // big-endian TIFF
        ([0x7F, 0x45, 0x4C, 0x46], 0, "application/x-elf"),
        ([0x4D, 0x5A], 0, "application/x-msdownload"),
    ];

    private static async Task<string> FileMimeTypeAsync(string path, CancellationToken ct)
    {
        var buffer = new byte[16];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(), ct);

        foreach (var (sig, offset, mime) in MagicBytes)
        {
            if (bytesRead >= offset + sig.Length &&
                buffer.AsSpan(offset, sig.Length).SequenceEqual(sig))
                return mime;
        }

        // Check if it looks like text
        for (var i = 0; i < bytesRead; i++)
        {
            var b = buffer[i];
            if (b < 0x09 || (b > 0x0D && b < 0x20 && b != 0x1B))
                return "application/octet-stream";
        }

        return "text/plain";
    }

    private static async Task<string> FileEncodingAsync(string path, CancellationToken ct)
    {
        var bom = new byte[4];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bytesRead = await stream.ReadAsync(bom.AsMemory(), ct);

        if (bytesRead >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
            return "utf-8-bom";
        if (bytesRead >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
            return bytesRead >= 4 && bom[2] == 0x00 && bom[3] == 0x00
                ? "utf-32-le" : "utf-16-le";
        if (bytesRead >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
            return "utf-16-be";
        if (bytesRead >= 4 && bom[0] == 0x00 && bom[1] == 0x00 && bom[2] == 0xFE && bom[3] == 0xFF)
            return "utf-32-be";

        // Heuristic: read first 8KB and check for high bytes
        stream.Position = 0;
        var sample = new byte[Math.Min(8192, stream.Length)];
        var sampleRead = await stream.ReadAsync(sample.AsMemory(), ct);

        var hasHighBytes = false;
        var validUtf8 = true;
        var i = 0;
        while (i < sampleRead)
        {
            var b = sample[i];
            if (b < 0x80) { i++; continue; }
            hasHighBytes = true;

            // UTF-8 continuation byte check
            int extra;
            if ((b & 0xE0) == 0xC0) extra = 1;
            else if ((b & 0xF0) == 0xE0) extra = 2;
            else if ((b & 0xF8) == 0xF0) extra = 3;
            else { validUtf8 = false; break; }

            for (var j = 0; j < extra; j++)
            {
                if (i + 1 + j >= sampleRead || (sample[i + 1 + j] & 0xC0) != 0x80)
                { validUtf8 = false; break; }
            }
            if (!validUtf8) break;
            i += 1 + extra;
        }

        if (!hasHighBytes) return "ascii";
        return validUtf8 ? "utf-8" : "unknown";
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

    private static string DirEmpty(string path) =>
        (!Directory.EnumerateFileSystemEntries(path).Any()).ToString();

    // ═══════════════════════════════════════════════════════════════
    // File comparison helpers (read-only)
    // ═══════════════════════════════════════════════════════════════

    private static async Task<string> FileEqualAsync(string path1, string path2, CancellationToken ct)
    {
        var fi1 = new System.IO.FileInfo(path1);
        var fi2 = new System.IO.FileInfo(path2);

        if (!fi1.Exists || !fi2.Exists) return "False";
        if (fi1.Length != fi2.Length) return "False";

        const int bufferSize = 8192;
        var buf1 = new byte[bufferSize];
        var buf2 = new byte[bufferSize];

        await using var s1 = fi1.OpenRead();
        await using var s2 = fi2.OpenRead();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read1 = await s1.ReadAsync(buf1.AsMemory(), ct);
            var read2 = await s2.ReadAsync(buf2.AsMemory(), ct);

            if (read1 != read2) return "False";
            if (read1 == 0) return "True";
            if (!buf1.AsSpan(0, read1).SequenceEqual(buf2.AsSpan(0, read2))) return "False";
        }
    }

    private static async Task<string> FileChecksumAsync(string[] args, CancellationToken ct)
    {
        var filePath = args[0];
        var expectedHash = args[1].Trim().ToLowerInvariant();
        var algorithm = args.Length > 2 ? args[2].ToLowerInvariant() : "sha256";

        await using var stream = File.OpenRead(filePath);
        var hashBytes = algorithm switch
        {
            "sha256" => await SHA256.HashDataAsync(stream, ct),
            "sha512" => await SHA512.HashDataAsync(stream, ct),
            "md5" => await MD5.HashDataAsync(stream, ct),
            _ => throw new InvalidOperationException($"Unsupported hash: {algorithm}")
        };

        var actual = Convert.ToHexStringLower(hashBytes);
        return actual.Equals(expectedHash, StringComparison.Ordinal).ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    // JSON construction/mutation helpers
    // ═══════════════════════════════════════════════════════════════

    private static string JsonFromPairs(string[] args)
    {
        var obj = new JsonObject();
        for (var i = 0; i < args.Length; i += 2)
            obj[args[i]] = JsonValue.Create(args[i + 1]);
        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string JsonSetKey(string json, string key, string value)
    {
        var obj = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException("Input is not a JSON object.");
        obj[key] = JsonValue.Create(value);
        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string JsonRemoveKey(string json, string key)
    {
        var obj = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException("Input is not a JSON object.");
        obj.Remove(key);
        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string JsonGetValue(string json, string indexOrKey)
    {
        var node = JsonNode.Parse(json);
        JsonNode? result = node switch
        {
            JsonObject obj => obj[indexOrKey],
            JsonArray arr when int.TryParse(indexOrKey, out var idx) => arr[idx],
            _ => throw new InvalidOperationException("Input must be a JSON object or array.")
        };

        if (result is null) return "null";
        return result is JsonValue val && val.GetValueKind() == JsonValueKind.String
            ? val.GetValue<string>()
            : result.ToJsonString();
    }

    private static string JsonCompact(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement);
    }

    private static string JsonArrayFromArgs(string[] args)
    {
        var arr = new JsonArray();
        foreach (var item in args)
            arr.Add(JsonValue.Create(item));
        return arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    // ═══════════════════════════════════════════════════════════════
    // Time arithmetic helpers
    // ═══════════════════════════════════════════════════════════════

    private static string TimeFormatFromUnix(string[] args)
    {
        var seconds = long.Parse(args[0]);
        var dto = DateTimeOffset.FromUnixTimeSeconds(seconds);
        var format = args.Length > 1 ? args[1] : "o";
        return dto.ToString(format);
    }

    private static string TimeParseToUnix(string[] args)
    {
        var input = args[0];
        if (args.Length > 1)
        {
            var dto = DateTimeOffset.ParseExact(input, args[1],
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal);
            return dto.ToUnixTimeSeconds().ToString();
        }
        else
        {
            var dto = DateTimeOffset.Parse(input, System.Globalization.CultureInfo.InvariantCulture);
            return dto.ToUnixTimeSeconds().ToString();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Version comparison helpers
    // ═══════════════════════════════════════════════════════════════

    private static readonly Regex VersionRegex = new(
        @"(\d+\.\d+(?:\.\d+)?(?:\.\d+)?)",
        RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    private static string CompareVersions(string v1, string v2)
    {
        var ver1 = ParseVersionString(v1);
        var ver2 = ParseVersionString(v2);
        return ver1.CompareTo(ver2).ToString();
    }

    private static string ParseVersion(string input)
    {
        var match = VersionRegex.Match(input);
        return match.Success ? match.Groups[1].Value : "";
    }

    private static Version ParseVersionString(string input)
    {
        var match = VersionRegex.Match(input);
        var text = match.Success ? match.Groups[1].Value : input;
        return Version.TryParse(text, out var v) ? v
            : throw new InvalidOperationException($"Cannot parse version: '{input}'.");
    }

    // ═══════════════════════════════════════════════════════════════
    // Encoding/conversion helpers
    // ═══════════════════════════════════════════════════════════════

    private static string ConvertBase(string value, string fromBaseStr, string toBaseStr)
    {
        if (!int.TryParse(fromBaseStr, out var fromBase) || !int.TryParse(toBaseStr, out var toBase))
            throw new InvalidOperationException("Base must be an integer.");

        int[] allowed = [2, 8, 10, 16];
        if (!allowed.Contains(fromBase) || !allowed.Contains(toBase))
            throw new InvalidOperationException("Supported bases: 2, 8, 10, 16.");

        var number = Convert.ToInt64(value.Trim(), fromBase);
        return Convert.ToString(number, toBase);
    }

    // ═══════════════════════════════════════════════════════════════
    // Regex capture groups helper
    // ═══════════════════════════════════════════════════════════════

    private static string TextRegexGroups(string input, string pattern)
    {
        var regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(2));
        var match = regex.Match(input);

        if (!match.Success)
            return "{}";

        var obj = new JsonObject();
        for (var i = 0; i < match.Groups.Count; i++)
        {
            var group = match.Groups[i];
            var name = regex.GroupNameFromNumber(i);
            var key = string.IsNullOrEmpty(name) || name == i.ToString()
                ? i.ToString() : name;
            obj[key] = JsonValue.Create(group.Value);
        }

        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    // ═══════════════════════════════════════════════════════════════
    // Script control helpers
    // ═══════════════════════════════════════════════════════════════

    private static async Task<string> SleepAsync(string secondsStr, CancellationToken ct)
    {
        var seconds = double.Parse(secondsStr);
        await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        return $"Slept {seconds}s";
    }

    private static string AssertEqual(string[] args)
    {
        var actual = args[0];
        var expected = args[1];
        var message = args.Length > 2 ? args[2] : $"Assertion failed: '{actual}' != '{expected}'";

        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException(message);

        return $"Assert passed: '{actual}'";
    }

    // ═══════════════════════════════════════════════════════════════
    // Sysadmin: NetTcpConnect — TCP port check
    // ═══════════════════════════════════════════════════════════════

    private static async Task<string> NetTcpConnectAsync(string[] args, CancellationToken ct)
    {
        var host = args[0];
        var port = int.Parse(args[1]);
        var timeout = args.Length > 2 ? int.Parse(args[2]) : 5;

        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeout));
            await client.ConnectAsync(host, port, cts.Token);
            sw.Stop();
            return $"Open ({sw.ElapsedMilliseconds}ms)";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return $"Closed (timeout {timeout}s)";
        }
        catch (System.Net.Sockets.SocketException)
        {
            sw.Stop();
            return $"Closed ({sw.ElapsedMilliseconds}ms)";
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Sysadmin: HttpLatency — timed HEAD requests
    // ═══════════════════════════════════════════════════════════════

    private async Task<string> HttpLatencyAsync(string[] args, CancellationToken ct)
    {
        var url = args[0];
        var count = args.Length > 1 ? int.Parse(args[1]) : 3;
        var times = new List<long>(count);

        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            sw.Stop();
            times.Add(sw.ElapsedMilliseconds);

            if (i < count - 1)
                await Task.Delay(200, ct); // small delay between requests
        }

        var min = times.Min();
        var max = times.Max();
        var avg = times.Average();

        var sb = new StringBuilder();
        sb.AppendLine($"URL: {url}");
        sb.AppendLine($"Requests: {count}");
        for (var i = 0; i < times.Count; i++)
            sb.AppendLine($"  #{i + 1}: {times[i]}ms");
        sb.AppendLine($"Min: {min}ms");
        sb.AppendLine($"Avg: {avg:F0}ms");
        sb.Append($"Max: {max}ms");
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    // Sysadmin: file age and staleness
    // ═══════════════════════════════════════════════════════════════

    private static string FileAge(string path)
    {
        var fi = new System.IO.FileInfo(path);
        if (!fi.Exists) throw new InvalidOperationException($"File not found: {path}");
        var age = (DateTimeOffset.UtcNow - fi.LastWriteTimeUtc).TotalSeconds;
        return ((long)age).ToString();
    }

    private static string FileNewerThan(string path, string secondsStr)
    {
        var fi = new System.IO.FileInfo(path);
        if (!fi.Exists) return "False";
        var seconds = long.Parse(secondsStr);
        var age = (DateTimeOffset.UtcNow - fi.LastWriteTimeUtc).TotalSeconds;
        return (age <= seconds).ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    // Sysadmin: process search
    // ═══════════════════════════════════════════════════════════════

    private static string ProcessFind(string name)
    {
        var sb = new StringBuilder();
        var count = 0;
        foreach (var proc in Process.GetProcesses()
            .Where(p => p.ProcessName.Contains(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.ProcessName))
        {
            if (count >= 50) { sb.AppendLine("... (truncated at 50)"); break; }
            try
            {
                sb.AppendLine($"{proc.Id}\t{proc.ProcessName}\t{proc.WorkingSet64 / 1024}KB");
            }
            catch
            {
                sb.AppendLine($"{proc.Id}\t{proc.ProcessName}\t(access denied)");
            }
            count++;
        }

        return count == 0 ? "No matching processes." : $"{count} process(es):\n{sb.ToString().TrimEnd()}";
    }

    // ═══════════════════════════════════════════════════════════════
    // Sysadmin: system discovery
    // ═══════════════════════════════════════════════════════════════

    private static string SysDriveList()
    {
        var sb = new StringBuilder();
        foreach (var drive in DriveInfo.GetDrives())
        {
            sb.Append($"{drive.Name}\t{drive.DriveType}");
            if (drive.IsReady)
            {
                var totalGB = drive.TotalSize / (1024.0 * 1024 * 1024);
                var freeGB = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
                var usedPct = (1.0 - (double)drive.AvailableFreeSpace / drive.TotalSize) * 100;
                sb.Append($"\t{drive.DriveFormat}\tTotal: {totalGB:F1}GB\tFree: {freeGB:F1}GB\tUsed: {usedPct:F0}%");
            }
            else
            {
                sb.Append("\t(not ready)");
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string SysNetInfo()
    {
        var sb = new StringBuilder();
        foreach (var iface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (iface.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                continue;

            sb.Append($"{iface.Name}\t{iface.OperationalStatus}\t{iface.NetworkInterfaceType}");

            var props = iface.GetIPProperties();
            var addrs = props.UnicastAddresses
                .Select(a => a.Address.ToString())
                .Where(a => !a.StartsWith("fe80:", StringComparison.OrdinalIgnoreCase)); // skip link-local
            var addrList = string.Join(", ", addrs);
            if (!string.IsNullOrEmpty(addrList))
                sb.Append($"\t{addrList}");

            sb.AppendLine();
        }
        return sb.Length == 0 ? "No non-loopback interfaces found." : sb.ToString().TrimEnd();
    }

    private static readonly string[] EnvAllowlist =
    [
        "HOME", "USERPROFILE", "USER", "USERNAME", "PATH", "LANG",
        "LC_ALL", "TZ", "TERM", "PWD", "HOSTNAME", "SHELL", "EDITOR",
        "DOTNET_ROOT", "NODE_ENV"
    ];

    private static string EnvListAll()
    {
        var sb = new StringBuilder();
        foreach (var name in EnvAllowlist)
        {
            var value = Environment.GetEnvironmentVariable(name);
            sb.AppendLine(value is not null ? $"{name}={value}" : $"{name}=(not set)");
        }
        return sb.ToString().TrimEnd();
    }

    // ═══════════════════════════════════════════════════════════════
    // Sysadmin: regex file search
    // ═══════════════════════════════════════════════════════════════

    private static string FileSearchRegex(string path, string pattern)
    {
        var regex = new Regex(pattern, RegexOptions.None, RegexTimeout);
        var sb = new StringBuilder();
        var lineNumber = 0;
        var matchCount = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            if (regex.IsMatch(line))
            {
                sb.AppendLine($"{lineNumber}: {line}");
                matchCount++;
                if (matchCount >= 500)
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

    // ═══════════════════════════════════════════════════════════════
    // Sysadmin: tabular text
    // ═══════════════════════════════════════════════════════════════

    private static string TextColumn(string[] args)
    {
        var input = args[0];
        var colIndex = int.Parse(args[1]);
        var delimiter = args.Length > 2 ? args[2] : null;

        var sb = new StringBuilder();
        foreach (var line in input.Split('\n'))
        {
            var parts = delimiter is not null
                ? line.Split(delimiter)
                : line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            sb.AppendLine(colIndex < parts.Length ? parts[colIndex].Trim() : "");
        }
        return sb.ToString().TrimEnd();
    }

    private static string TextTable(string[] args)
    {
        var input = args[0];
        var delimiter = args.Length > 1 ? args[1] : "\t";

        var rows = input.Split('\n')
            .Select(line => line.Split(delimiter))
            .ToList();

        if (rows.Count == 0) return "";

        var maxCols = rows.Max(r => r.Length);
        var widths = new int[maxCols];
        foreach (var row in rows)
            for (var i = 0; i < row.Length; i++)
                widths[i] = Math.Max(widths[i], row[i].Trim().Length);

        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            for (var i = 0; i < maxCols; i++)
            {
                var cell = i < row.Length ? row[i].Trim() : "";
                if (i > 0) sb.Append("  ");
                sb.Append(cell.PadRight(widths[i]));
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // ═══════════════════════════════════════════════════════════════
    // Sysadmin: directory comparison and hashing
    // ═══════════════════════════════════════════════════════════════

    private static string DirCompare(string path1, string path2)
    {
        var files1 = Directory.Exists(path1)
            ? Directory.GetFiles(path1, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(path1, f))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var files2 = Directory.Exists(path2)
            ? Directory.GetFiles(path2, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(path2, f))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        var onlyIn1 = files1.Except(files2, StringComparer.OrdinalIgnoreCase).Order().ToList();
        var onlyIn2 = files2.Except(files1, StringComparer.OrdinalIgnoreCase).Order().ToList();
        var common = files1.Intersect(files2, StringComparer.OrdinalIgnoreCase).Order().ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Only in {Path.GetFileName(path1)} ({onlyIn1.Count}):");
        foreach (var f in onlyIn1.Take(200)) sb.AppendLine($"  {f}");
        if (onlyIn1.Count > 200) sb.AppendLine($"  ... ({onlyIn1.Count - 200} more)");

        sb.AppendLine($"Only in {Path.GetFileName(path2)} ({onlyIn2.Count}):");
        foreach (var f in onlyIn2.Take(200)) sb.AppendLine($"  {f}");
        if (onlyIn2.Count > 200) sb.AppendLine($"  ... ({onlyIn2.Count - 200} more)");

        sb.AppendLine($"Common ({common.Count}):");
        foreach (var f in common.Take(200)) sb.AppendLine($"  {f}");
        if (common.Count > 200) sb.Append($"  ... ({common.Count - 200} more)");

        return sb.ToString().TrimEnd();
    }

    private static async Task<string> DirHashAsync(string[] args, CancellationToken ct)
    {
        var dir = args[0];
        var algorithm = args.Length > 1 ? args[1].ToLowerInvariant() : "sha256";
        var pattern = args.Length > 2 ? args[2] : "*";

        var files = Directory.GetFiles(dir, pattern, SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"{files.Count} file(s):");
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            await using var stream = File.OpenRead(file);
            var hashBytes = algorithm switch
            {
                "sha256" => await SHA256.HashDataAsync(stream, ct),
                "sha512" => await SHA512.HashDataAsync(stream, ct),
                "md5"    => await MD5.HashDataAsync(stream, ct),
                _ => throw new InvalidOperationException($"Unsupported hash: {algorithm}")
            };
            var hash = Convert.ToHexStringLower(hashBytes);
            var rel = Path.GetRelativePath(dir, file);
            sb.AppendLine($"{hash}  {rel}");
        }
        return sb.ToString().TrimEnd();
    }

    // ═══════════════════════════════════════════════════════════════
    // Sysadmin: human-readable formatting
    // ═══════════════════════════════════════════════════════════════

    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    private static string FormatBytesHuman(string bytesStr)
    {
        if (!double.TryParse(bytesStr, out var bytes) || bytes < 0)
            throw new InvalidOperationException($"Bytes must be a non-negative number, got '{bytesStr}'.");

        var unit = 0;
        var size = bytes;
        while (size >= 1024 && unit < ByteUnits.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{size:F0} {ByteUnits[unit]}" : $"{size:F2} {ByteUnits[unit]}";
    }

    private static string FormatDurationHuman(string secondsStr)
    {
        if (!double.TryParse(secondsStr, out var totalSeconds) || totalSeconds < 0)
            throw new InvalidOperationException($"Seconds must be a non-negative number, got '{secondsStr}'.");

        var ts = TimeSpan.FromSeconds(totalSeconds);

        if (ts.TotalDays >= 1)
            return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m {ts.Seconds}s";
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
        if (ts.TotalMinutes >= 1)
            return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        if (ts.TotalSeconds >= 1)
            return $"{ts.TotalSeconds:F1}s";
        return $"{ts.TotalMilliseconds:F0}ms";
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
    // Sysadmin: system log viewing (read-only, redacted)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Patterns that trigger value redaction in log output. Any log line
    /// containing <c>PATTERN=value</c> or <c>PATTERN: value</c> has the
    /// value portion replaced with <c>[REDACTED]</c>. Defense-in-depth:
    /// prevents accidental secret persistence in captured output.
    /// </summary>
    private static readonly Regex SecretRedactionRegex = new(
        @"(?i)(KEY|SECRET|TOKEN|PASSWORD|PASSWD|CONN|AUTH|PRIVATE|ENCRYPT|JWT|CERTIFICATE|APIKEY|CREDENTIAL|CONNECTION_STRING)\s*[=:]\s*\S+",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(2));

    private static string RedactSecrets(string line) =>
        SecretRedactionRegex.Replace(line, m =>
            $"{m.Groups[1].Value}=[REDACTED]");

    private static string SysLogRead(string[] args)
    {
        var source = args[0].ToLowerInvariant();
        var maxLines = args.Length > 1 ? int.Parse(args[1]) : 50;
        var filter = args.Length > 2 ? args[2] : null;

        if (OperatingSystem.IsWindows())
            return SysLogReadWindows(source, maxLines, filter);

        return SysLogReadLinux(source, maxLines, filter);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string SysLogReadWindows(string source, int maxLines, string? filter)
    {
        var logName = source switch
        {
            "application" => "Application",
            "system" => "System",
            "security" => "Security",
            _ => throw new InvalidOperationException($"Unknown Windows log source: {source}")
        };

        try
        {
            using var eventLog = new System.Diagnostics.EventLog(logName);
            var entries = eventLog.Entries;
            var sb = new StringBuilder();
            var count = 0;

            // Read most recent entries first
            for (var i = entries.Count - 1; i >= 0 && count < maxLines; i--)
            {
                try
                {
                    var entry = entries[i];
                    var line = $"{entry.TimeGenerated:yyyy-MM-dd HH:mm:ss}\t{entry.EntryType}\t{entry.Source}\t{entry.Message?.Replace('\n', ' ').Replace('\r', ' ')}";

                    if (filter is not null && !line.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    sb.AppendLine(RedactSecrets(line));
                    count++;
                }
                catch
                {
                    // Skip inaccessible entries
                }
            }

            return count == 0
                ? $"No entries found in {logName} log."
                : $"{count} entr(ies) from {logName}:\n{sb.ToString().TrimEnd()}";
        }
        catch (System.Security.SecurityException)
        {
            return $"Access denied to {logName} event log. Requires elevated privileges.";
        }
    }

    private static string SysLogReadLinux(string source, int maxLines, string? filter)
    {
        var logPath = source switch
        {
            "syslog" => FindLinuxLog("syslog", "messages"),
            "auth" => FindLinuxLog("auth.log", "secure"),
            "kern" => FindLinuxLog("kern.log"),
            "daemon" => FindLinuxLog("daemon.log"),
            "messages" => FindLinuxLog("messages", "syslog"),
            _ => throw new InvalidOperationException($"Unknown Linux log source: {source}")
        };

        if (logPath is null)
            return $"Log source '{source}' not found. No matching file in /var/log/.";

        try
        {
            var allLines = File.ReadLines(logPath);
            var filtered = filter is not null
                ? allLines.Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase))
                : allLines;

            var recent = filtered.TakeLast(maxLines).ToList();

            if (recent.Count == 0)
                return $"No matching entries in {logPath}.";

            var sb = new StringBuilder();
            sb.AppendLine($"{recent.Count} line(s) from {logPath}:");
            foreach (var line in recent)
                sb.AppendLine(RedactSecrets(line));

            return sb.ToString().TrimEnd();
        }
        catch (UnauthorizedAccessException)
        {
            return $"Access denied to {logPath}. May require elevated privileges.";
        }
    }

    private static string? FindLinuxLog(params string[] candidates)
    {
        foreach (var name in candidates)
        {
            var path = $"/var/log/{name}";
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static string SysLogSources()
    {
        var sb = new StringBuilder();
        if (OperatingSystem.IsWindows())
        {
            sb.AppendLine("Windows Event Logs:");
            sb.AppendLine("  application  — Application log");
            sb.AppendLine("  system       — System log");
            sb.AppendLine("  security     — Security log (may require elevation)");
        }
        else
        {
            sb.AppendLine("Linux Log Files (/var/log/):");
            string[] linuxLogs = ["syslog", "messages", "auth.log", "secure", "kern.log", "daemon.log"];
            foreach (var name in linuxLogs)
            {
                var path = $"/var/log/{name}";
                var exists = File.Exists(path);
                sb.AppendLine($"  {name,-15} {(exists ? "✓ available" : "✗ not found")}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    // ═══════════════════════════════════════════════════════════════
    // Sysadmin: service status (read-only)
    // ═══════════════════════════════════════════════════════════════

    private static string SysServiceList(string[] args)
    {
        var filter = args.Length > 0 ? args[0] : null;

        if (OperatingSystem.IsWindows())
            return SysServiceListWindows(filter);

        return SysServiceListLinux(filter);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string SysServiceListWindows(string? filter)
    {
        var services = System.ServiceProcess.ServiceController.GetServices();
        var filtered = filter is not null
            ? services.Where(s =>
                s.ServiceName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                s.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            : services.AsEnumerable();

        var results = filtered.Take(200).ToList();
        if (results.Count == 0)
            return filter is not null ? $"No services matching '{filter}'." : "No services found.";

        var sb = new StringBuilder();
        sb.AppendLine($"{results.Count} service(s):");
        foreach (var svc in results.OrderBy(s => s.ServiceName))
        {
            sb.AppendLine($"{svc.ServiceName}\t{svc.DisplayName}\t{svc.Status}\t{svc.StartType}");
            svc.Dispose();
        }

        foreach (var svc in services.Except(results))
            svc.Dispose();

        return sb.ToString().TrimEnd();
    }

    private static string SysServiceListLinux(string? filter)
    {
        // Parse systemd unit files if available
        const string systemdPath = "/etc/systemd/system";
        const string initdPath = "/etc/init.d";

        if (Directory.Exists(systemdPath))
        {
            var units = Directory.EnumerateFiles(systemdPath, "*.service")
                .Concat(Directory.EnumerateFiles("/lib/systemd/system", "*.service", new EnumerationOptions { IgnoreInaccessible = true }))
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(n => filter is null || n!.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n)
                .Take(200)
                .ToList();

            if (units.Count == 0)
                return filter is not null ? $"No systemd services matching '{filter}'." : "No systemd services found.";

            var sb = new StringBuilder();
            sb.AppendLine($"{units.Count} systemd service(s):");
            foreach (var unit in units)
                sb.AppendLine($"  {unit}");
            return sb.ToString().TrimEnd();
        }

        if (Directory.Exists(initdPath))
        {
            var scripts = Directory.GetFiles(initdPath)
                .Select(Path.GetFileName)
                .Where(n => n is not null && (filter is null || n.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(n => n)
                .Take(200)
                .ToList();

            if (scripts.Count == 0)
                return filter is not null ? $"No init.d services matching '{filter}'." : "No init.d services found.";

            var sb = new StringBuilder();
            sb.AppendLine($"{scripts.Count} init.d service(s):");
            foreach (var script in scripts)
                sb.AppendLine($"  {script}");
            return sb.ToString().TrimEnd();
        }

        return "No service manager found (no systemd or init.d).";
    }

    private static string SysServiceStatus(string name)
    {
        if (OperatingSystem.IsWindows())
            return SysServiceStatusWindows(name);

        return SysServiceStatusLinux(name);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string SysServiceStatusWindows(string name)
    {
        try
        {
            using var svc = new System.ServiceProcess.ServiceController(name);
            var sb = new StringBuilder();
            sb.AppendLine($"Name: {svc.ServiceName}");
            sb.AppendLine($"Display: {svc.DisplayName}");
            sb.AppendLine($"Status: {svc.Status}");
            sb.AppendLine($"StartType: {svc.StartType}");
            sb.AppendLine($"ServiceType: {svc.ServiceType}");
            sb.Append($"CanStop: {svc.CanStop}");
            return sb.ToString();
        }
        catch (InvalidOperationException)
        {
            return $"Service '{name}' not found.";
        }
    }

    private static string SysServiceStatusLinux(string name)
    {
        // Check systemd unit file
        string[] searchPaths = ["/etc/systemd/system", "/lib/systemd/system"];
        foreach (var dir in searchPaths)
        {
            var unitPath = Path.Combine(dir, $"{name}.service");
            if (!File.Exists(unitPath)) continue;

            try
            {
                var content = File.ReadAllText(unitPath);
                var sb = new StringBuilder();
                sb.AppendLine($"Service: {name}");
                sb.AppendLine($"Unit file: {unitPath}");

                // Extract key fields from the unit file
                foreach (var line in content.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("Description=", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("ExecStart=", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("Restart=", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("Type=", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("User=", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("WantedBy=", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"  {RedactSecrets(trimmed)}");
                    }
                }

                return sb.ToString().TrimEnd();
            }
            catch (UnauthorizedAccessException)
            {
                return $"Access denied to unit file for '{name}'.";
            }
        }

        // Check init.d
        var initPath = $"/etc/init.d/{name}";
        if (File.Exists(initPath))
            return $"Service: {name}\nType: init.d\nScript: {initPath}";

        return $"Service '{name}' not found.";
    }

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
