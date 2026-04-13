using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

using SharpClaw.Application.Services;
using SharpClaw.Utils.Security;

namespace SharpClaw.Modules.ModuleDev.Services;

/// <summary>
/// Compiles module projects via <c>dotnet build</c>.
/// Parses MSBuild output into structured diagnostics.
/// </summary>
internal sealed partial class ModuleBuildService(ModuleWorkspaceService workspace)
{
    /// <summary>
    /// Structured build result.
    /// </summary>
    internal sealed record BuildResult(
        bool Success,
        IReadOnlyList<BuildDiagnostic> Errors,
        IReadOnlyList<BuildDiagnostic> Warnings,
        string? OutputDll,
        string RawOutput);

    internal sealed record BuildDiagnostic(
        string? File,
        int? Line,
        int? Column,
        string? Code,
        string Message);

    private static readonly TimeSpan BuildTimeout = TimeSpan.FromSeconds(120);
    private static readonly HashSet<string> AllowedConfigurations =
        new(StringComparer.OrdinalIgnoreCase) { "Debug", "Release" };

    /// <summary>
    /// Build a module project. Returns structured diagnostics.
    /// </summary>
    public async Task<BuildResult> BuildAsync(
        string moduleId, string configuration = "Debug", CancellationToken ct = default)
    {
        if (!AllowedConfigurations.Contains(configuration))
            throw new ArgumentException(
                $"Invalid build configuration '{configuration}'. Allowed: {string.Join(", ", AllowedConfigurations)}.",
                nameof(configuration));

        var safeModuleId = EnsureSafeModuleId(moduleId);
        var moduleDir = PathGuard.EnsureContainedIn(
            workspace.ResolveModuleDir(safeModuleId), ModuleService.ResolveExternalModulesDir());

        if (!Directory.Exists(moduleDir))
            throw new DirectoryNotFoundException($"Module directory not found: {moduleDir}");

        // Find the .csproj
        var csprojFiles = Directory.GetFiles(moduleDir, "*.csproj");
        if (csprojFiles.Length == 0)
            throw new FileNotFoundException($"No .csproj found in '{moduleDir}'.");

        // Extract just the file name from the discovered path, validate it is a plain
        // name with no traversal, then reconstruct from the trusted moduleDir.
        // This severs the taint chain so CodeQL sees no user-controlled value reaching Process.Start.
        var csprojFileName = PathGuard.EnsureFileName(Path.GetFileName(csprojFiles[0]));
        PathGuard.EnsureExtension(csprojFileName, ".csproj");
        var csprojPath = Path.GetFullPath(Path.Combine(moduleDir, csprojFileName));

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { "build", csprojPath, "-c", configuration, "-nologo",
                "-consoleloggerparameters:NoSummary" },
            WorkingDirectory = moduleDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stderr.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(BuildTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* best effort */ }
            throw new TimeoutException(
                $"dotnet build timed out after {BuildTimeout.TotalSeconds}s for module '{moduleId}'.");
        }

        var rawOutput = stdout.ToString() + stderr.ToString();
        var success = process.ExitCode == 0;
        var errors = ParseDiagnostics(rawOutput, "error");
        var warnings = ParseDiagnostics(rawOutput, "warning");

        string? outputDll = null;
        if (success)
        {
            // Resolve the output DLL
            var binDir = Path.Combine(moduleDir, "bin", configuration, "net10.0");
            var dllName = Path.GetFileNameWithoutExtension(csprojPath) + ".dll";
            var dllPath = Path.Combine(binDir, dllName);
            if (File.Exists(dllPath))
                outputDll = dllPath;
        }

        return new BuildResult(success, errors, warnings, outputDll, rawOutput);
    }

    private static string EnsureSafeModuleId(string moduleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId, nameof(moduleId));
        if (!ModuleIdRegex().IsMatch(moduleId))
            throw new ArgumentException(
                $"Invalid module ID '{moduleId}'. Must match ^[a-z][a-z0-9_]{{0,39}}$.", nameof(moduleId));
        return moduleId;
    }

    // ── MSBuild diagnostic parsing ────────────────────────────────

    private static IReadOnlyList<BuildDiagnostic> ParseDiagnostics(string output, string severity)
    {
        var diagnostics = new List<BuildDiagnostic>();

        // Pattern: file(line,col): severity CODE: message
        foreach (var line in output.Split('\n'))
        {
            var match = MsBuildDiagnosticRegex().Match(line);
            if (!match.Success) continue;

            var matchedSeverity = match.Groups["severity"].Value;
            if (!matchedSeverity.Equals(severity, StringComparison.OrdinalIgnoreCase))
                continue;

            diagnostics.Add(new BuildDiagnostic(
                File: match.Groups["file"].Value,
                Line: int.TryParse(match.Groups["line"].Value, out var l) ? l : null,
                Column: int.TryParse(match.Groups["col"].Value, out var c) ? c : null,
                Code: match.Groups["code"].Value,
                Message: match.Groups["msg"].Value.Trim()));
        }

        return diagnostics;
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex ModuleIdRegex();

    [GeneratedRegex(@"(?<file>[^(]+)\((?<line>\d+),(?<col>\d+)\):\s+(?<severity>error|warning)\s+(?<code>\w+):\s+(?<msg>.+)")]
    private static partial Regex MsBuildDiagnosticRegex();
}
