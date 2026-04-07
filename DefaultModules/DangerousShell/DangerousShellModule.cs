using System.Diagnostics;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.DangerousShell;

/// <summary>
/// Default module: real (unsandboxed) shell execution — Bash, PowerShell,
/// CommandPrompt, Git. The raw command is handed directly to the
/// interpreter. Safety relies entirely on the permission system's
/// clearance requirements.
/// </summary>
public sealed class DangerousShellModule : ISharpClawModule
{
    public string Id => "sharpclaw_dangerous_shell";
    public string DisplayName => "Dangerous Shell";
    public string ToolPrefix => "ds";

    // ═══════════════════════════════════════════════════════════════
    // DI Registration
    // ═══════════════════════════════════════════════════════════════

    public void ConfigureServices(IServiceCollection services) { }

    // ═══════════════════════════════════════════════════════════════
    // Contracts
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleContractExport> ExportedContracts => [];
    public IReadOnlyList<ModuleContractRequirement> RequiredContracts => [];

    // ═══════════════════════════════════════════════════════════════
    // CLI Commands
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleCliCommand>? GetCliCommands() => null;

    // ═══════════════════════════════════════════════════════════════
    // Tool Definitions
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions()
    {
        var permission = new ModuleToolPermission(
            IsPerResource: true, Check: null, DelegateTo: "UnsafeExecuteAsDangerousShellAsync");

        return
        [
            new("execute",
                "Execute a raw command in an unsandboxed shell interpreter "
                + "(Bash, PowerShell, CommandPrompt, or Git). Inherently dangerous — "
                + "bypasses all sandbox restrictions. Requires clearance.",
                BuildDangerousShellSchema(),
                permission,
                TimeoutSeconds: 300,
                Aliases: ["execute_dangerous_shell"]),
        ];
    }

    // ═══════════════════════════════════════════════════════════════
    // Tool Execution
    // ═══════════════════════════════════════════════════════════════

    public async Task<string> ExecuteToolAsync(
        string toolName, JsonElement parameters, AgentJobContext job,
        IServiceProvider sp, CancellationToken ct)
    {
        return toolName switch
        {
            "execute" or "execute_dangerous_shell"
                => await ExecuteDangerousShellAsync(parameters, ct),
            _ => throw new InvalidOperationException($"Unknown DangerousShell tool: {toolName}"),
        };
    }

    // ── Dangerous Shell Execution ─────────────────────────────────

    /// <summary>
    /// Spawns a real shell interpreter process. No sandboxing, no allowlist,
    /// no path validation. Cross-platform: Bash on Linux/macOS, PowerShell
    /// (pwsh) everywhere, CommandPrompt on Windows only, Git everywhere.
    /// Working directory comes from tool parameters only — no DB access.
    /// </summary>
    private static async Task<string> ExecuteDangerousShellAsync(
        JsonElement parameters, CancellationToken ct)
    {
        var shellTypeStr = parameters.TryGetProperty("shellType", out var stProp)
            ? stProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(shellTypeStr)
            || !Enum.TryParse<DangerousShellType>(shellTypeStr, ignoreCase: true, out var shellType))
            throw new InvalidOperationException(
                "Dangerous shell requires a valid 'shellType' (Bash, PowerShell, CommandPrompt, Git).");

        var command = parameters.TryGetProperty("command", out var cmdProp)
            ? cmdProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(command))
            throw new InvalidOperationException(
                "Dangerous shell requires a 'command' string.");

        var workingDir = parameters.TryGetProperty("workingDirectory", out var wdProp)
            ? wdProp.GetString() : null;
        workingDir ??= Directory.GetCurrentDirectory();

        // Resolve the shell executable and argument list.
        var (executable, arguments) = ResolveDangerousShell(shellType, command!);

        // Spawn the real shell — NO sandboxing, NO allowlist.
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDir,
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Dangerous shell '{executable}' exited with code {process.ExitCode}.\n" +
                $"stderr: {stderr}");

        return string.IsNullOrWhiteSpace(stdout) ? "(no output)" : stdout;
    }

    /// <summary>
    /// Maps a <see cref="DangerousShellType"/> to the OS executable and
    /// the argument list that passes the raw command to the interpreter.
    /// </summary>
    internal static (string Executable, string[] Arguments) ResolveDangerousShell(
        DangerousShellType shellType, string command) => shellType switch
    {
        // Bash — Linux/macOS (or WSL on Windows).
        DangerousShellType.Bash => ("bash", ["-c", command]),

        // PowerShell — cross-platform via pwsh.
        DangerousShellType.PowerShell => (
            OperatingSystem.IsWindows() ? "powershell" : "pwsh",
            ["-NoProfile", "-NonInteractive", "-Command", command]),

        // Command Prompt — Windows only.
        DangerousShellType.CommandPrompt => ("cmd", ["/C", command]),

        // Git — cross-platform, command is the git sub-command + args.
        DangerousShellType.Git => ("git", command.Split(' ', StringSplitOptions.RemoveEmptyEntries)),

        _ => throw new InvalidOperationException(
            $"Unsupported dangerous shell type: {shellType}.")
    };

    // ═══════════════════════════════════════════════════════════════
    // Schemas
    // ═══════════════════════════════════════════════════════════════

    private static JsonElement BuildDangerousShellSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "resourceId": {
                        "type": "string",
                        "description": "SystemUser GUID."
                    },
                    "shellType": {
                        "type": "string",
                        "enum": ["Bash", "PowerShell", "CommandPrompt", "Git"],
                        "description": "Shell interpreter."
                    },
                    "command": {
                        "type": "string",
                        "description": "Raw command string."
                    },
                    "workingDirectory": {
                        "type": "string",
                        "description": "Optional CWD override."
                    }
                },
                "required": ["resourceId", "shellType", "command"]
            }
            """);
        return doc.RootElement.Clone();
    }
}
