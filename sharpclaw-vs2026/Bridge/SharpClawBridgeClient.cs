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

namespace SharpClaw.VS2026Extension;

/// <summary>
/// WebSocket client that connects to the SharpClaw <c>EditorBridgeService</c>
/// and handles editor action requests dispatched by AI agents.
/// </summary>
/// <remarks>
/// Protocol:
/// <list type="number">
/// <item>Connect to <c>ws://localhost:5163/api/editor/bridge</c></item>
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

    private static readonly Uri BridgeUri =
        new("ws://localhost:5163/api/editor/bridge");

    private readonly string? _workspacePath;
    private readonly SharpClawPackage _package;

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;

    public SharpClawBridgeClient(string? workspacePath, SharpClawPackage package)
    {
        _workspacePath = workspacePath;
        _package = package;
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
        _cts = new CancellationTokenSource();
        _socket = new ClientWebSocket();

        await _socket.ConnectAsync(BridgeUri, ct);

        // Registration: must be the first message per EditorBridgeService protocol.
        // editorType uses camelCase enum value matching the server's JsonStringEnumConverter.
        var registration = JsonSerializer.Serialize(new
        {
            type = "register",
            editorType = "visualStudio2026",
            editorVersion = "2026",
            workspacePath = _workspacePath
        }, JsonOptions);

        await SendTextAsync(registration, ct);

        // Wait for the "registered" ack (contains sessionId + connectionId).
        _ = await ReceiveTextAsync(ct);

        // Start the background receive loop.
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Disconnects from the backend and drains the receive loop.
    /// </summary>
    public async Task DisconnectAsync()
    {
        try
        {
            if (_cts is not null)
                await _cts.CancelAsync();

            if (_socket?.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "Extension closing", timeout.Token);
            }
        }
        catch { /* best-effort close */ }

        try
        {
            if (_receiveTask is not null)
                await _receiveTask;
        }
        catch { /* absorb cancellation */ }

        _socket?.Dispose();
        _cts?.Dispose();
        _socket = null;
        _cts = null;
        _receiveTask = null;
    }

    // ═══════════════════════════════════════════════════════════════
    // Receive loop
    // ═══════════════════════════════════════════════════════════════

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (_socket?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var json = await ReceiveTextAsync(ct);
                if (json is null) break;

                await HandleMessageAsync(json, ct);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (WebSocketException) { /* connection dropped */ }
    }

    private async Task HandleMessageAsync(string json, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.GetProperty("type").GetString() != "request")
                return;

            var requestId = root.GetProperty("requestId").GetGuid();
            var action = root.GetProperty("action").GetString()!;
            JsonElement? parameters = root.TryGetProperty("params", out var p) ? p : null;

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

            await SendResponseAsync(requestId, success, result, error, ct);
        }
        catch (Exception ex)
        {
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

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"File not found: {filePath}");

        var lines = await File.ReadAllLinesAsync(fullPath, ct);

        int? startLine = GetOptionalInt(parameters, "startLine");
        int? endLine = GetOptionalInt(parameters, "endLine");

        if (startLine.HasValue || endLine.HasValue)
        {
            int start = Math.Max(1, startLine ?? 1) - 1;                // 0-based
            int end = Math.Min(lines.Length, endLine ?? lines.Length);    // inclusive→exclusive

            if (start >= lines.Length)
                return string.Empty;

            return string.Join(Environment.NewLine, lines.Skip(start).Take(end - start));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private async Task<string?> WriteFileAsync(JsonElement? parameters, CancellationToken ct)
    {
        var filePath = GetRequiredString(parameters, "filePath");
        var content = GetRequiredString(parameters, "content");
        var fullPath = ResolvePath(filePath);

        var dir = Path.GetDirectoryName(fullPath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(fullPath, content, ct);
        return $"File written: {filePath}";
    }

    private async Task<string?> GetOpenFilesAsync(CancellationToken ct)
    {
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

        return JsonSerializer.Serialize(files, JsonOptions);
    }

    private async Task<string?> GetSelectionAsync(CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var dteObj = await _package.GetServiceAsync(typeof(EnvDTE.DTE));
        if (dteObj is null)
            return JsonSerializer.Serialize(new { file = (string?)null }, JsonOptions);

        var dte = (EnvDTE.DTE)dteObj;
        var activeDoc = dte.ActiveDocument;

        if (activeDoc is null)
        {
            return JsonSerializer.Serialize(
                new { file = (string?)null }, JsonOptions);
        }

        var selection = activeDoc.Selection as EnvDTE.TextSelection;
        return JsonSerializer.Serialize(new
        {
            file = GetRelativePath(activeDoc.FullName),
            line = selection?.CurrentLine ?? 0,
            column = selection?.CurrentColumn ?? 0,
            selectedText = selection?.Text ?? string.Empty
        }, JsonOptions);
    }

    private async Task<string?> CreateFileAsync(JsonElement? parameters, CancellationToken ct)
    {
        var filePath = GetRequiredString(parameters, "filePath");
        var content = GetOptionalString(parameters, "content") ?? string.Empty;
        var fullPath = ResolvePath(filePath);

        var dir = Path.GetDirectoryName(fullPath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(fullPath, content, ct);
        return $"File created: {filePath}";
    }

    private Task<string?> DeleteFileAsync(JsonElement? parameters, CancellationToken ct)
    {
        var filePath = GetRequiredString(parameters, "filePath");
        var fullPath = ResolvePath(filePath);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"File not found: {filePath}");

        File.Delete(fullPath);
        return Task.FromResult<string?>($"File deleted: {filePath}");
    }

    private async Task<string?> ApplyEditAsync(JsonElement? parameters, CancellationToken ct)
    {
        var filePath = GetRequiredString(parameters, "filePath");
        var startLine = GetRequiredInt(parameters, "startLine");
        var endLine = GetRequiredInt(parameters, "endLine");
        var newText = GetRequiredString(parameters, "newText");
        var fullPath = ResolvePath(filePath);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"File not found: {filePath}");

        var lines = (await File.ReadAllLinesAsync(fullPath, ct)).ToList();
        int start = Math.Max(1, startLine) - 1;             // 0-based
        int end = Math.Min(lines.Count, endLine);            // inclusive

        if (start > lines.Count)
            throw new ArgumentOutOfRangeException(
                nameof(startLine),
                $"Start line {startLine} exceeds file length ({lines.Count}).");

        int removeCount = Math.Max(0, end - start);
        lines.RemoveRange(start, removeCount);

        var replacement = newText.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .ToArray();
        lines.InsertRange(start, replacement);

        await File.WriteAllLinesAsync(fullPath, lines, ct);
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

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var dteObj = await _package.GetServiceAsync(typeof(EnvDTE.DTE));
        if (dteObj is not EnvDTE80.DTE2 dte)
            return "[]";

        var diagnostics = CollectDiagnostics(dte, filePath);
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
            await File.WriteAllTextAsync(leftPath, string.Empty, ct);
            leftLabel = $"{filePath} (new file)";
        }

        // Right side: temp file with the proposed content.
        var rightPath = Path.Combine(
            Path.GetTempPath(),
            $"sharpclaw_proposed_{Guid.NewGuid():N}{ext}");
        await File.WriteAllTextAsync(rightPath, proposedContent, ct);

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
            try { process.Kill(entireProcessTree: true); } catch { }
        });

        // Read stdout and stderr concurrently to avoid deadlocks.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
        var stderrTask = process.StandardError.ReadToEndAsync(token);

        await process.WaitForExitAsync(token);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        const int maxLen = 100_000;
        if (stdout.Length > maxLen)
            stdout = stdout[..maxLen] + $"\n... (truncated, {stdout.Length} total chars)";
        if (stderr.Length > maxLen)
            stderr = stderr[..maxLen] + $"\n... (truncated, {stderr.Length} total chars)";

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

            await ms.WriteAsync(buffer.AsMemory(0, result.Count), ct);
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

        try { return Path.GetRelativePath(baseDir, fullPath); }
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
}
