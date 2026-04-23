using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SharpClaw.Utils.Logging;

namespace SharpClaw.VS2026Extension;

/// <summary>
/// WebSocket client that connects to the SharpClaw <c>EditorBridgeService</c>
/// and handles editor action requests dispatched by AI agents.
/// </summary>
/// <remarks>
/// Protocol:
/// <list type="number">
/// <item>Connect to <c>ws://127.0.0.1:48923/editor/ws</c> with <c>X-Api-Key</c> header</item>
/// <item>Send registration: <c>{ type, editorType, editorVersion, workspacePath }</c></item>
/// <item>Receive ack: <c>{ type: "registered", sessionId, connectionId }</c></item>
/// <item>Receive loop: handle <c>{ type: "request", requestId, action, params }</c>
///       and reply <c>{ type: "response", requestId, success, data, error }</c></item>
/// </list>
/// </remarks>
internal sealed class SharpClawBridgeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string? _workspacePath;
    private readonly SharpClawPackage _package;
    private readonly BridgeClientConfig _config;

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;

    public SharpClawBridgeClient(
        string? workspacePath,
        SharpClawPackage package,
        BridgeClientConfig config)
    {
        _workspacePath = workspacePath;
        _package = package;
        _config = config;
    }

    /// <summary>
    /// <see langword="true"/> while the WebSocket is open.
    /// </summary>
    public bool IsConnected => _socket?.State == WebSocketState.Open;

    // ═══════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Connects to the SharpClaw backend, sends a registration message,
    /// waits for the acknowledgement, and starts the receive loop.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var bridgeUri = _config.BridgeUri;
        var apiKeyPath = ResolveApiKeyPath();

        await LogAsync("Connection sequence starting...");
        await LogAsync($"  Workspace: {_workspacePath ?? "(none)"}");
        await LogAsync($"  Target:    {bridgeUri}");
        await LogAsync($"  Backend instance id: {(_config.BackendInstanceId.Length > 0 ? _config.BackendInstanceId : "(none)")}");

        _cts = new CancellationTokenSource();
        _socket = new ClientWebSocket();

        // The ApiKeyMiddleware on the backend requires X-Api-Key on every
        // request except /echo.  The key is written to a well-known file
        // by ApiKeyProvider each time the backend starts.
        await LogAsync($"  Reading API key from: {apiKeyPath}");
        string apiKey;
        try
        {
            apiKey = ReadApiKey(apiKeyPath);
            await LogAsync($"  API key read OK ({apiKey.Length} chars)");
        }
        catch (Exception ex)
        {
            await LogAsync($"  API key read FAILED: {ex.Message}");
            throw;
        }

        _socket.Options.SetRequestHeader("X-Api-Key", apiKey);
        await LogAsync("  X-Api-Key header set.");

        await LogAsync($"  Opening WebSocket to {bridgeUri}...");
        try
        {
            await _socket.ConnectAsync(bridgeUri, ct);
            await LogAsync($"  WebSocket connected (state: {_socket.State}).");
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException?.Message;
            await LogAsync($"  WebSocket connect FAILED: {ex.Message}"
                + (inner != null ? $" -> {inner}" : ""));
            throw;
        }

        // Registration: must be the first message per EditorBridgeService protocol.
        // editorType uses camelCase enum value matching the server's JsonStringEnumConverter.
        var registration = JsonSerializer.Serialize(new
        {
            type = "register",
            editorType = "visualStudio2026",
            editorVersion = "2026",
            workspacePath = _workspacePath
        }, JsonOptions);

        await LogAsync("  Sending registration...");
        await SendTextAsync(registration, ct);
        await LogAsync("  Registration sent, waiting for ack...");

        // Wait for the "registered" ack (contains sessionId + connectionId).
        var ack = await ReceiveTextAsync(ct);
        await LogAsync($"  Ack received: {Truncate(ack, 200)}");

        // Start the background receive loop.
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        await LogAsync("  Receive loop started. Connection ready.");
    }

    /// <summary>
    /// Disconnects from the backend and drains the receive loop.
    /// </summary>
    public async Task DisconnectAsync()
    {
        await LogAsync("Disconnect starting...");

        try
        {
            if (_cts is not null)
            {
                _cts.Cancel();
                await LogAsync("  Cancellation signalled.");
            }

            if (_socket?.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await LogAsync($"  Sending WebSocket close (state: {_socket.State})...");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "Extension closing", timeout.Token);
                await LogAsync("  WebSocket close handshake complete.");
            }
        }
        catch (Exception ex)
        {
            await LogAsync($"  Close error (best-effort): {ex.Message}");
        }

        try
        {
            if (_receiveTask is not null)
            {
                await LogAsync("  Draining receive loop...");
                await _receiveTask;
            }
        }
        catch (Exception ex)
        {
            await LogAsync($"  Receive loop drain: {ex.GetType().Name}");
        }

        _socket?.Dispose();
        _cts?.Dispose();
        _socket = null;
        _cts = null;
        _receiveTask = null;

        await LogAsync("Disconnected.");
    }

    // ═══════════════════════════════════════════════════════════════
    // Receive loop
    // ═══════════════════════════════════════════════════════════════

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        await LogAsync("Receive loop running.");
        try
        {
            while (_socket?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var json = await ReceiveTextAsync(ct);
                if (json is null)
                {
                    await LogAsync("Receive loop: server closed connection.");
                    break;
                }

                await HandleMessageAsync(json, ct);
            }

            await LogAsync($"Receive loop exited (socket: {_socket?.State}, cancelled: {ct.IsCancellationRequested}).");
        }
        catch (OperationCanceledException)
        {
            await LogAsync("Receive loop: cancelled (shutdown).");
        }
        catch (WebSocketException ex)
        {
            await LogAsync($"Receive loop: WebSocket error — {ex.Message}");
        }
    }

    private async Task HandleMessageAsync(string json, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var msgType = root.GetProperty("type").GetString();
            if (msgType != "request")
            {
                await LogAsync($"Received non-request message: type={msgType}");
                return;
            }

            var requestId = root.GetProperty("requestId").GetGuid();
            var action = root.GetProperty("action").GetString()!;
            JsonElement? parameters = root.TryGetProperty("params", out var p) ? p : null;

            await LogAsync($"Request {requestId:N}: action={action}");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            string? result = null;
            string? error = null;
            bool success;

            try
            {
                result = await HandleActionAsync(action, parameters, ct);
                success = true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                success = false;
            }

            sw.Stop();
            if (success)
                await LogAsync($"Request {requestId:N}: {action} OK ({sw.ElapsedMilliseconds}ms)");
            else
                await LogAsync($"Request {requestId:N}: {action} FAILED ({sw.ElapsedMilliseconds}ms) — {error}");

            await SendResponseAsync(requestId, success, result, error, ct);
        }
        catch (Exception ex)
        {
            await LogAsync($"Error handling message: {ex.Message}");
            System.Diagnostics.Debug.WriteLine(
                $"[SharpClaw] Error handling message: {ex.Message}",
                "SharpClaw.VS2026");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Action routing
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Routes an incoming action to the appropriate handler.
    /// Action names match the VS2026EditorModule tool names with the
    /// <c>vs26_</c> prefix stripped (done server-side by ExecuteToolAsync).
    /// </summary>
    private Task<string?> HandleActionAsync(
        string action, JsonElement? parameters, CancellationToken ct)
    {
        return action switch
        {
            "read_file"       => ReadFileAsync(parameters, ct),
            "write_file"      => WriteFileAsync(parameters, ct),
            "get_open_files"  => GetOpenFilesAsync(ct),
            "get_selection"   => GetSelectionAsync(ct),
            "create_file"     => CreateFileAsync(parameters, ct),
            "delete_file"     => DeleteFileAsync(parameters, ct),
            "apply_edit"      => ApplyEditAsync(parameters, ct),
            "get_diagnostics" => GetDiagnosticsAsync(parameters, ct),
            "show_diff"       => ShowDiffAsync(parameters, ct),
            "run_build"       => RunBuildAsync(ct),
            "run_terminal"    => RunTerminalAsync(parameters, ct),
            _ => throw new NotSupportedException($"Unknown action: {action}")
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // Implemented actions
    // ═══════════════════════════════════════════════════════════════

    private async Task<string?> ReadFileAsync(JsonElement? parameters, CancellationToken ct)
    {
        var filePath = GetRequiredString(parameters, "filePath");
        var fullPath = ResolvePath(filePath);

        int? startLine = GetOptionalInt(parameters, "startLine");
        int? endLine = GetOptionalInt(parameters, "endLine");
        await LogAsync($"  read_file: path={filePath}, resolved={fullPath}"
            + (startLine.HasValue || endLine.HasValue
                ? $", lines {startLine ?? 1}-{endLine?.ToString() ?? "EOF"}"
                : ", full file"));

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"File not found: {filePath}");

        var lines = File.ReadAllLines(fullPath);
        await LogAsync($"  read_file: {lines.Length} total lines");

        if (startLine.HasValue || endLine.HasValue)
        {
            int start = Math.Max(1, startLine ?? 1) - 1;                // 0-based
            int end = Math.Min(lines.Length, endLine ?? lines.Length);    // inclusive→exclusive

            if (start >= lines.Length)
                return string.Empty;

            var slice = lines.Skip(start).Take(end - start).ToArray();
            await LogAsync($"  read_file: returning {slice.Length} lines (range {start + 1}-{start + slice.Length})");
            return string.Join(Environment.NewLine, slice);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private async Task<string?> WriteFileAsync(JsonElement? parameters, CancellationToken ct)
    {
        var filePath = GetRequiredString(parameters, "filePath");
        var content = GetRequiredString(parameters, "content");
        var fullPath = ResolvePath(filePath);

        bool existed = File.Exists(fullPath);
        await LogAsync($"  write_file: path={filePath}, resolved={fullPath}");
        await LogAsync($"  write_file: existed={existed}, content length={content.Length} chars");
        await LogAsync($"  write_file: preview={Truncate(content, 200)}");

        var dir = Path.GetDirectoryName(fullPath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        File.WriteAllText(fullPath, content);
        return $"File written: {filePath}";
    }

    private async Task<string?> GetOpenFilesAsync(CancellationToken ct)
    {
        await LogAsync("  get_open_files: querying DTE...");
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var dteObj = await _package.GetServiceAsync(typeof(EnvDTE.DTE));
        if (dteObj is not EnvDTE.DTE dte || dte.Documents is null)
            return "[]";

        var files = new List<object>();
        foreach (EnvDTE.Document doc in dte.Documents)
        {
            files.Add(new
            {
                path = GetRelativePath(doc.FullName),
                name = doc.Name,
                saved = doc.Saved
            });
        }

        await LogAsync($"  get_open_files: {files.Count} documents open");
        return JsonSerializer.Serialize(files, JsonOptions);
    }

    private async Task<string?> GetSelectionAsync(CancellationToken ct)
    {
        await LogAsync("  get_selection: querying DTE...");
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var dteObj = await _package.GetServiceAsync(typeof(EnvDTE.DTE));
        if (dteObj is null)
            return JsonSerializer.Serialize(new { file = (string?)null }, JsonOptions);

        var dte = (EnvDTE.DTE)dteObj;
        var activeDoc = dte.ActiveDocument;

        if (activeDoc is null)
        {
            await LogAsync("  get_selection: no active document");
            return JsonSerializer.Serialize(
                new { file = (string?)null }, JsonOptions);
        }

        var selection = activeDoc.Selection as EnvDTE.TextSelection;
        var relPath = GetRelativePath(activeDoc.FullName);
        var selText = selection?.Text ?? string.Empty;
        await LogAsync($"  get_selection: file={relPath}, line={selection?.CurrentLine ?? 0}, col={selection?.CurrentColumn ?? 0}, selected={selText.Length} chars");

        return JsonSerializer.Serialize(new
        {
            file = relPath,
            line = selection?.CurrentLine ?? 0,
            column = selection?.CurrentColumn ?? 0,
            selectedText = selText
        }, JsonOptions);
    }

    private async Task<string?> CreateFileAsync(JsonElement? parameters, CancellationToken ct)
    {
        var filePath = GetRequiredString(parameters, "filePath");
        var content = GetOptionalString(parameters, "content") ?? string.Empty;
        var fullPath = ResolvePath(filePath);

        await LogAsync($"  create_file: path={filePath}, resolved={fullPath}");
        await LogAsync($"  create_file: content length={content.Length} chars");
        if (content.Length > 0)
            await LogAsync($"  create_file: preview={Truncate(content, 200)}");

        var dir = Path.GetDirectoryName(fullPath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        File.WriteAllText(fullPath, content);
        return $"File created: {filePath}";
    }

    private async Task<string?> DeleteFileAsync(JsonElement? parameters, CancellationToken ct)
    {
        var filePath = GetRequiredString(parameters, "filePath");
        var fullPath = ResolvePath(filePath);

        await LogAsync($"  delete_file: path={filePath}, resolved={fullPath}");

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"File not found: {filePath}");

        File.Delete(fullPath);
        await LogAsync($"  delete_file: deleted successfully");
        return $"File deleted: {filePath}";
    }

    private async Task<string?> ApplyEditAsync(JsonElement? parameters, CancellationToken ct)
    {
        var filePath = GetRequiredString(parameters, "filePath");
        var startLine = GetRequiredInt(parameters, "startLine");
        var endLine = GetRequiredInt(parameters, "endLine");
        var newText = GetRequiredString(parameters, "newText");
        var fullPath = ResolvePath(filePath);

        await LogAsync($"  apply_edit: path={filePath}, resolved={fullPath}");
        await LogAsync($"  apply_edit: lines {startLine}-{endLine}, newText length={newText.Length} chars");

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"File not found: {filePath}");

        var lines = File.ReadAllLines(fullPath).ToList();
        int start = Math.Max(1, startLine) - 1;             // 0-based
        int end = Math.Min(lines.Count, endLine);            // inclusive

        if (start > lines.Count)
            throw new ArgumentOutOfRangeException(
                nameof(startLine),
                $"Start line {startLine} exceeds file length ({lines.Count}).");

        int removeCount = Math.Max(0, end - start);
        var removedLines = lines.GetRange(start, removeCount);
        await LogAsync($"  apply_edit: removing {removeCount} lines (was: {Truncate(string.Join("\n", removedLines), 300)})");
        lines.RemoveRange(start, removeCount);

        var replacement = newText.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .ToArray();
        await LogAsync($"  apply_edit: inserting {replacement.Length} lines at position {start + 1}");
        await LogAsync($"  apply_edit: new content={Truncate(newText, 300)}");
        lines.InsertRange(start, replacement);

        File.WriteAllLines(fullPath, lines);
        await LogAsync($"  apply_edit: file now {lines.Count} total lines");
        return $"Applied edit to {filePath} (lines {startLine}-{endLine}).";
    }

    // ═══════════════════════════════════════════════════════════════
    // Diagnostics
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Retrieves errors, warnings, and messages from the VS Error List.
    /// Severity is determined by toggling the Error List filter categories
    /// and reading items for each; the original filter state is restored.
    /// </summary>
    private async Task<string?> GetDiagnosticsAsync(
        JsonElement? parameters, CancellationToken ct)
    {
        var filePath = GetOptionalString(parameters, "filePath");
        await LogAsync($"  get_diagnostics: filter={filePath ?? "(all files)"}");

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var dteObj = await _package.GetServiceAsync(typeof(EnvDTE.DTE));
        if (dteObj is not EnvDTE80.DTE2 dte)
            return "[]";

        var diagnostics = CollectDiagnostics(dte, filePath);
        await LogAsync($"  get_diagnostics: {diagnostics.Count} items returned");
        return JsonSerializer.Serialize(diagnostics, JsonOptions);
    }

    // ═══════════════════════════════════════════════════════════════
    // Diff view
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Opens the VS built-in comparison window between the original file
    /// on disk and a temporary file containing the proposed content.
    /// </summary>
    private async Task<string?> ShowDiffAsync(
        JsonElement? parameters, CancellationToken ct)
    {
        var filePath = GetRequiredString(parameters, "filePath");
        var proposedContent = GetRequiredString(parameters, "proposedContent");
        var diffTitle = GetOptionalString(parameters, "diffTitle")
                        ?? $"Proposed changes: {filePath}";
        var fullPath = ResolvePath(filePath);

        await LogAsync($"  show_diff: path={filePath}, resolved={fullPath}");
        await LogAsync($"  show_diff: title={diffTitle}, proposed length={proposedContent.Length} chars");
        await LogAsync($"  show_diff: proposed preview={Truncate(proposedContent, 300)}");

        // Left side: original file or empty temp when the file is new.
        var ext = Path.GetExtension(filePath);
        string leftPath;
        string leftLabel;

        if (File.Exists(fullPath))
        {
            leftPath = fullPath;
            leftLabel = filePath;
        }
        else
        {
            leftPath = Path.Combine(
                Path.GetTempPath(),
                $"sharpclaw_orig_{Guid.NewGuid():N}{ext}");
            File.WriteAllText(leftPath, string.Empty);
            leftLabel = $"{filePath} (new file)";
        }

        // Right side: temp file with the proposed content.
        var rightPath = Path.Combine(
            Path.GetTempPath(),
            $"sharpclaw_proposed_{Guid.NewGuid():N}{ext}");
        File.WriteAllText(rightPath, proposedContent);

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var diffService = await _package.GetServiceAsync(typeof(SVsDifferenceService))
            as IVsDifferenceService;

        if (diffService is null)
            throw new InvalidOperationException(
                "VS difference service is not available.");

        diffService.OpenComparisonWindow2(
            leftPath,
            rightPath,
            diffTitle,
            null,                        // tooltip
            leftLabel,
            $"{filePath} (proposed)",
            null,                        // inline label
            null,                        // roles
            0u);                         // default options

        return $"Diff view opened for {filePath}.";
    }

    // ═══════════════════════════════════════════════════════════════
    // Build
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Triggers a solution build through the DTE automation model,
    /// waits for completion via the <c>OnBuildDone</c> event (without
    /// blocking the UI thread), and returns the build result along
    /// with diagnostics from the Error List.
    /// </summary>
    private async Task<string?> RunBuildAsync(CancellationToken ct)
    {
        await LogAsync("  run_build: starting solution build...");
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var dteObj = await _package.GetServiceAsync(typeof(EnvDTE.DTE));
        if (dteObj is not EnvDTE80.DTE2 dte)
            throw new InvalidOperationException("Could not access DTE.");

        if (dte.Solution is null)
            throw new InvalidOperationException("No solution is loaded.");

        var solutionBuild = dte.Solution.SolutionBuild;
        if (solutionBuild.BuildState == EnvDTE.vsBuildState.vsBuildStateInProgress)
            throw new InvalidOperationException("A build is already in progress.");

        var tcs = new TaskCompletionSource<bool>();
        var buildEvents = dte.Events.BuildEvents;

        void OnBuildDone(EnvDTE.vsBuildScope scope, EnvDTE.vsBuildAction action)
        {
            buildEvents.OnBuildDone -= OnBuildDone;
            tcs.TrySetResult(true);
        }

        buildEvents.OnBuildDone += OnBuildDone;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromMinutes(10));
        using var reg = linked.Token.Register(() => tcs.TrySetCanceled());

        solutionBuild.Build(WaitForBuildToFinish: false);

        // Yield the main thread so the build can proceed and events fire.
        await tcs.Task.ConfigureAwait(false);

        // Switch back to the main thread to read results.
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        int failedProjects = solutionBuild.LastBuildInfo;
        var diagnostics = CollectDiagnostics(dte, filePathFilter: null, maxPerCategory: 50);

        await LogAsync($"  run_build: {(failedProjects == 0 ? "succeeded" : $"FAILED ({failedProjects} projects)")}, {diagnostics.Count} diagnostics");

        return JsonSerializer.Serialize(new
        {
            succeeded = failedProjects == 0,
            failedProjects,
            diagnosticCount = diagnostics.Count,
            diagnostics
        }, JsonOptions);
    }

    // ═══════════════════════════════════════════════════════════════
    // Terminal command
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Runs a shell command via <c>cmd.exe /c</c>, captures stdout and
    /// stderr, and returns the result as JSON. A 60-second timeout and
    /// 100 KB output cap are enforced.
    /// </summary>
    private async Task<string?> RunTerminalAsync(
        JsonElement? parameters, CancellationToken ct)
    {
        var command = GetRequiredString(parameters, "command");
        var workingDirectory = GetOptionalString(parameters, "workingDirectory");
        await LogAsync($"  run_terminal: command={Truncate(command, 300)}");
        await LogAsync($"  run_terminal: workingDirectory={workingDirectory ?? "(default)"}");

        string workDir;
        if (workingDirectory is not null)
        {
            workDir = ResolvePath(workingDirectory);
        }
        else if (_workspacePath is not null)
        {
            workDir = File.Exists(_workspacePath)
                ? Path.GetDirectoryName(_workspacePath)!
                : _workspacePath;
        }
        else
        {
            workDir = Environment.CurrentDirectory;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(60));
        var token = linked.Token;

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {command}",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        // Kill the process tree on cancellation / timeout.
        using var killReg = token.Register(() =>
        {
            try { process.Kill(); } catch { }
        });

        // Read stdout and stderr concurrently to avoid deadlocks.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await Task.Run(() => process.WaitForExit(), token);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        const int maxLen = 100_000;
        if (stdout.Length > maxLen)
            stdout = stdout.Substring(0, maxLen) + $"\n... (truncated, {stdout.Length} total chars)";
        if (stderr.Length > maxLen)
            stderr = stderr.Substring(0, maxLen) + $"\n... (truncated, {stderr.Length} total chars)";

        await LogAsync($"  run_terminal: exitCode={process.ExitCode}, stdout={stdout.Length} chars, stderr={stderr.Length} chars");
        if (process.ExitCode != 0 && stderr.Length > 0)
            await LogAsync($"  run_terminal: stderr preview={Truncate(stderr, 300)}");

        return JsonSerializer.Serialize(new
        {
            exitCode = process.ExitCode,
            stdout,
            stderr
        }, JsonOptions);
    }

    // ═══════════════════════════════════════════════════════════════
    // Diagnostics collection helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads diagnostics from the VS Error List with severity by toggling
    /// the category filters and restoring the original state afterwards.
    /// </summary>
    private List<object> CollectDiagnostics(
        EnvDTE80.DTE2 dte, string? filePathFilter, int maxPerCategory = 100)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var el = dte.ToolWindows.ErrorList;
        bool origErrors = el.ShowErrors;
        bool origWarnings = el.ShowWarnings;
        bool origMessages = el.ShowMessages;

        var result = new List<object>();

        try
        {
            el.ShowErrors = true;
            el.ShowWarnings = false;
            el.ShowMessages = false;
            AddDiagnosticItems(el.ErrorItems, "error", filePathFilter, result, maxPerCategory);

            el.ShowErrors = false;
            el.ShowWarnings = true;
            el.ShowMessages = false;
            AddDiagnosticItems(el.ErrorItems, "warning", filePathFilter, result, maxPerCategory);

            el.ShowErrors = false;
            el.ShowWarnings = false;
            el.ShowMessages = true;
            AddDiagnosticItems(el.ErrorItems, "info", filePathFilter, result, maxPerCategory);
        }
        finally
        {
            el.ShowErrors = origErrors;
            el.ShowWarnings = origWarnings;
            el.ShowMessages = origMessages;
        }

        return result;
    }

    private void AddDiagnosticItems(
        EnvDTE80.ErrorItems items, string severity, string? filePathFilter,
        List<object> result, int max)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        int count = Math.Min(items.Count, max);
        for (int i = 1; i <= count; i++)
        {
            var item = items.Item(i);

            if (filePathFilter is not null)
            {
                var rel = GetRelativePath(item.FileName);
                if (!rel.Equals(filePathFilter, StringComparison.OrdinalIgnoreCase)
                    && !item.FileName.EndsWith(filePathFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            result.Add(new
            {
                severity,
                message = item.Description,
                file = GetRelativePath(item.FileName),
                line = item.Line,
                column = item.Column,
                project = item.Project
            });
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Response / WebSocket helpers
    // ═══════════════════════════════════════════════════════════════

    private async Task SendResponseAsync(
        Guid requestId, bool success, string? data, string? error,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new
        {
            type = "response",
            requestId,
            success,
            data,
            error
        }, JsonOptions);

        await SendTextAsync(json, ct);
    }

    private async Task SendTextAsync(string text, CancellationToken ct)
    {
        if (_socket?.State != WebSocketState.Open) return;

        var bytes = Encoding.UTF8.GetBytes(text);
        await _socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            ct);
    }

    private async Task<string?> ReceiveTextAsync(CancellationToken ct)
    {
        if (_socket is null) return null;

        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;

        do
        {
            result = await _socket.ReceiveAsync(
                new ArraySegment<byte>(buffer), ct);

            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            await ms.WriteAsync(buffer, 0, result.Count, ct);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    // ═══════════════════════════════════════════════════════════════
    // Path & parameter helpers
    // ═══════════════════════════════════════════════════════════════

    private string ResolvePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            return relativePath;

        if (_workspacePath is null)
            throw new InvalidOperationException("No workspace path configured.");

        // _workspacePath may be a .sln path; use its directory.
        var baseDir = File.Exists(_workspacePath)
            ? Path.GetDirectoryName(_workspacePath)!
            : _workspacePath;

        return Path.GetFullPath(Path.Combine(baseDir, relativePath));
    }

    private string GetRelativePath(string fullPath)
    {
        if (_workspacePath is null)
            return fullPath;

        var baseDir = File.Exists(_workspacePath)
            ? Path.GetDirectoryName(_workspacePath)!
            : _workspacePath;

        try
        {
            var baseUri = new Uri(baseDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            var fullUri = new Uri(fullPath);
            return Uri.UnescapeDataString(baseUri.MakeRelativeUri(fullUri).ToString())
                      .Replace('/', Path.DirectorySeparatorChar);
        }
        catch { return fullPath; }
    }

    private static string GetRequiredString(JsonElement? parameters, string name)
    {
        if (parameters is null || !parameters.Value.TryGetProperty(name, out var prop))
            throw new ArgumentException($"Missing required parameter: {name}");
        return prop.GetString()
            ?? throw new ArgumentException($"Parameter '{name}' is null.");
    }

    private static string? GetOptionalString(JsonElement? parameters, string name)
    {
        if (parameters is null || !parameters.Value.TryGetProperty(name, out var prop))
            return null;
        return prop.GetString();
    }

    private static int GetRequiredInt(JsonElement? parameters, string name)
    {
        if (parameters is null || !parameters.Value.TryGetProperty(name, out var prop))
            throw new ArgumentException($"Missing required parameter: {name}");
        return prop.GetInt32();
    }

    private static int? GetOptionalInt(JsonElement? parameters, string name)
    {
        if (parameters is null || !parameters.Value.TryGetProperty(name, out var prop))
            return null;
        return prop.ValueKind == JsonValueKind.Null ? null : prop.GetInt32();
    }

    // ═══════════════════════════════════════════════════════════════
    // API key
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads the API key written by <c>ApiKeyProvider</c> on backend startup.
    /// </summary>
    private static string ReadApiKey(string apiKeyFilePath)
    {
        if (!File.Exists(apiKeyFilePath))
            throw new InvalidOperationException(
                $"API key file not found at '{apiKeyFilePath}'. " +
                "Ensure the SharpClaw backend is running.");

        return File.ReadAllText(apiKeyFilePath).Trim();
    }

    private string ResolveApiKeyPath()
    {
        if (!string.IsNullOrWhiteSpace(_config.ApiKeyFilePath))
            return _config.ApiKeyFilePath;

        var entries = EnumerateBackendDiscoveryEntries().ToList();
        if (!string.IsNullOrWhiteSpace(_config.BackendInstanceId))
        {
            var byInstanceId = entries.FirstOrDefault(e => string.Equals(e.InstanceId, _config.BackendInstanceId, StringComparison.Ordinal));
            if (byInstanceId is not null)
                return byInstanceId.ApiKeyFilePath;
        }

        var targetUrl = $"http://{_config.BridgeUri.Host}:{_config.BridgeUri.Port}";
        var byUrl = entries.FirstOrDefault(e => string.Equals(e.BaseUrl?.TrimEnd('/'), targetUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        if (byUrl is not null)
            return byUrl.ApiKeyFilePath;

        if (entries.Count == 1)
            return entries[0].ApiKeyFilePath;

        throw new InvalidOperationException(
            "API key file could not be resolved from backend discovery. " +
            "Configure API Key File Path explicitly or provide a Backend Instance Id.");
    }

    private static IEnumerable<BackendDiscoveryEntry> EnumerateBackendDiscoveryEntries()
    {
        var discoveryDirectory = Path.Combine(
            SharpClawAppDataPaths.GetSharpClawRootDirectory(),
            "discovery",
            "instances");

        if (!Directory.Exists(discoveryDirectory))
            yield break;

        foreach (var filePath in Directory.EnumerateFiles(discoveryDirectory, "backend-*.json"))
        {
            BackendDiscoveryEntry? entry;
            try
            {
                var json = File.ReadAllText(filePath);
                entry = JsonSerializer.Deserialize<BackendDiscoveryEntry>(json, JsonOptions);
            }
            catch
            {
                continue;
            }

            if (entry is not null && !string.IsNullOrWhiteSpace(entry.ApiKeyFilePath))
                yield return entry;
        }
    }

    private sealed class BackendDiscoveryEntry
    {
        public string? InstanceId { get; set; }
        public string? BaseUrl { get; set; }
        public string ApiKeyFilePath { get; set; } = string.Empty;
    }

    // ═══════════════════════════════════════════════════════════════
    // Logging helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Writes a message to the SharpClaw Output Window pane via the
    /// owning package. Fire-and-forget safe — swallows exceptions so
    /// a logging failure never breaks the connection flow.
    /// </summary>
    private async Task LogAsync(string message)
    {
        try
        {
            await _package.WriteOutputAsync(message);
        }
        catch
        {
            System.Diagnostics.Debug.WriteLine(
                $"[SharpClaw] {message}", "SharpClaw.VS2026");
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null) return null;
        return value.Length <= maxLength
            ? value
            : value.Substring(0, maxLength) + "...";
    }
}
