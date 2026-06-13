using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SharpClaw.Modules.ModuleDev.Services;

internal sealed partial class ModuleRuntimeVerificationService(
    ModuleWorkspaceService workspace,
    IModuleRuntimeCommandRunner commandRunner)
{
    private const int DefaultTimeoutSeconds = 30;
    private const int MaxTimeoutSeconds = 120;
    private const int OutputLimit = 16_000;

    public async Task<ModuleRuntimeVerificationResult> VerifyAsync(
        string moduleId,
        string runtime,
        ModuleRuntimeVerificationRequest request,
        CancellationToken ct = default)
    {
        var normalizedRuntime = runtime.Trim().ToLowerInvariant();
        if (normalizedRuntime is not ModuleScaffoldService.NodeRuntime
            and not ModuleScaffoldService.PythonRuntime)
        {
            return new ModuleRuntimeVerificationResult(
                true,
                normalizedRuntime,
                Ran: false,
                Steps: [],
                Error: null);
        }

        var moduleDir = workspace.ResolveModuleDir(moduleId);
        var entrypoint = ResolveEntrypoint(moduleId, moduleDir, normalizedRuntime);
        if (!File.Exists(entrypoint.FullPath))
        {
            return new ModuleRuntimeVerificationResult(
                false,
                normalizedRuntime,
                Ran: true,
                Steps: [],
                Error: $"Module entrypoint '{entrypoint.RelativePath}' does not exist.");
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(
            request.TimeoutSeconds ?? DefaultTimeoutSeconds,
            1,
            MaxTimeoutSeconds));
        IReadOnlyList<ModuleRuntimeVerificationStepResult> steps;
        try
        {
            steps = normalizedRuntime switch
            {
                ModuleScaffoldService.NodeRuntime => await VerifyNodeAsync(
                    moduleDir,
                    entrypoint.RelativePath,
                    request,
                    timeout,
                    ct),
                ModuleScaffoldService.PythonRuntime => await VerifyPythonAsync(
                    moduleDir,
                    entrypoint.RelativePath,
                    request,
                    timeout,
                    ct),
                _ => [],
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ModuleRuntimeVerificationResult(
                false,
                normalizedRuntime,
                Ran: true,
                Steps: [],
                Error: ex.Message);
        }

        return new ModuleRuntimeVerificationResult(
            steps.All(step => step.Success),
            normalizedRuntime,
            Ran: true,
            Steps: steps,
            Error: steps.FirstOrDefault(step => !step.Success)?.FailureSummary);
    }

    private async Task<IReadOnlyList<ModuleRuntimeVerificationStepResult>> VerifyNodeAsync(
        string moduleDir,
        string entrypoint,
        ModuleRuntimeVerificationRequest request,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var steps = new List<ModuleRuntimeVerificationStepResult>();
        var packageJsonPath = Path.Combine(moduleDir, "package.json");

        if (request.InstallDependencies && File.Exists(packageJsonPath))
        {
            var installArgs = File.Exists(Path.Combine(moduleDir, "package-lock.json"))
                ? new[] { "ci" }
                : ["install"];
            steps.Add(await RunStepAsync(
                "node_dependencies",
                "npm",
                installArgs,
                moduleDir,
                timeout,
                ct));
            if (!steps[^1].Success)
                return steps;
        }

        steps.Add(await RunStepAsync(
            "node_syntax",
            "node",
            ["--check", entrypoint],
            moduleDir,
            timeout,
            ct));
        if (!steps[^1].Success)
            return steps;

        if (request.RunDeclaredVerify && HasNodeVerifyScript(packageJsonPath))
        {
            steps.Add(await RunStepAsync(
                "node_declared_verify",
                "npm",
                ["run", "sharpclawVerify"],
                moduleDir,
                timeout,
                ct));
        }

        return steps;
    }

    private async Task<IReadOnlyList<ModuleRuntimeVerificationStepResult>> VerifyPythonAsync(
        string moduleDir,
        string entrypoint,
        ModuleRuntimeVerificationRequest request,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var steps = new List<ModuleRuntimeVerificationStepResult>();
        var pyprojectPath = Path.Combine(moduleDir, "pyproject.toml");
        var requirementsPath = Path.Combine(moduleDir, "requirements.txt");
        var python = "python";

        if (request.InstallDependencies
            && (File.Exists(requirementsPath) || File.Exists(pyprojectPath)))
        {
            var venvDir = Path.Combine(moduleDir, ".sharpclaw-venv");
            steps.Add(await RunStepAsync(
                "python_create_venv",
                python,
                ["-m", "venv", ".sharpclaw-venv"],
                moduleDir,
                timeout,
                ct));
            if (!steps[^1].Success)
                return steps;

            python = ResolveVenvPython(venvDir);
            if (File.Exists(requirementsPath))
            {
                steps.Add(await RunStepAsync(
                    "python_dependencies",
                    python,
                    ["-m", "pip", "install", "-r", "requirements.txt"],
                    moduleDir,
                    timeout,
                    ct));
            }
            else
            {
                steps.Add(await RunStepAsync(
                    "python_dependencies",
                    python,
                    ["-m", "pip", "install", "-e", "."],
                    moduleDir,
                    timeout,
                    ct));
            }

            if (!steps[^1].Success)
                return steps;
        }

        steps.Add(await RunStepAsync(
            "python_compile",
            python,
            ["-m", "py_compile", entrypoint],
            moduleDir,
            timeout,
            ct));
        if (!steps[^1].Success)
            return steps;

        if (request.RunDeclaredVerify
            && TryReadPythonVerifyCommand(pyprojectPath, python, out var verify))
        {
            steps.Add(await RunStepAsync(
                "python_declared_verify",
                verify.FileName,
                verify.Arguments,
                moduleDir,
                timeout,
                ct));
        }

        return steps;
    }

    private async Task<ModuleRuntimeVerificationStepResult> RunStepAsync(
        string name,
        string fileName,
        IReadOnlyList<string> arguments,
        string moduleDir,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var result = await commandRunner.RunAsync(
            new ModuleRuntimeCommand(
                fileName,
                arguments,
                moduleDir,
                timeout,
                OutputLimit),
            ct);
        var commandLine = FormatCommandLine(result.FileName, result.Arguments);
        var success = !result.TimedOut && result.ExitCode == 0;
        var failure = success
            ? null
            : $"{name} failed with exit code {result.ExitCode}: {commandLine}";

        return new ModuleRuntimeVerificationStepResult(
            name,
            commandLine,
            result.WorkingDirectory,
            result.ExitCode,
            result.TimedOut,
            success,
            result.Stdout,
            result.Stderr,
            result.Elapsed.TotalMilliseconds,
            failure);
    }

    private ModuleEntrypoint ResolveEntrypoint(
        string moduleId,
        string moduleDir,
        string runtime)
    {
        var relativePath = runtime == ModuleScaffoldService.PythonRuntime
            ? "module.py"
            : "module.mjs";
        var manifestPath = Path.Combine(moduleDir, "module.json");
        if (File.Exists(manifestPath))
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (manifest.RootElement.TryGetProperty("entrypoint", out var entrypoint)
                && entrypoint.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(entrypoint.GetString()))
            {
                relativePath = entrypoint.GetString()!.Trim();
            }
        }

        var fullPath = workspace.ResolveFilePath(moduleId, relativePath);
        return new ModuleEntrypoint(relativePath.Replace('\\', '/'), fullPath);
    }

    private static bool HasNodeVerifyScript(string packageJsonPath)
    {
        if (!File.Exists(packageJsonPath))
            return false;

        using var package = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
        return package.RootElement.TryGetProperty("scripts", out var scripts)
               && scripts.ValueKind == JsonValueKind.Object
               && scripts.TryGetProperty("sharpclawVerify", out var script)
               && script.ValueKind == JsonValueKind.String
               && !string.IsNullOrWhiteSpace(script.GetString());
    }

    private static bool TryReadPythonVerifyCommand(
        string pyprojectPath,
        string python,
        out ModuleRuntimeCommandSpec command)
    {
        command = default;
        if (!File.Exists(pyprojectPath))
            return false;

        var inSharpClawSection = false;
        foreach (var rawLine in File.ReadLines(pyprojectPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inSharpClawSection = line.Equals("[tool.sharpclaw]", StringComparison.Ordinal);
                continue;
            }

            var separator = line.IndexOf('=');
            if (!inSharpClawSection
                || !line.StartsWith("verify-command", StringComparison.Ordinal)
                || separator < 0)
            {
                continue;
            }

            var value = line[(separator + 1)..].Trim().Trim('"', '\'');
            var tokens = TokenizeCommand(value);
            if (tokens.Count < 3
                || !tokens[0].Equals("python", StringComparison.OrdinalIgnoreCase)
                || tokens[1] != "-m"
                || !PythonModuleNameRegex().IsMatch(tokens[2]))
            {
                throw new InvalidOperationException(
                    "Python verify-command must use the form 'python -m module [args...]'.");
            }

            command = new ModuleRuntimeCommandSpec(
                python,
                tokens.Skip(1).ToArray());
            return true;
        }

        return false;
    }

    private static IReadOnlyList<string> TokenizeCommand(string command)
    {
        if (command.IndexOfAny(['&', '|', ';', '<', '>', '`']) >= 0)
            throw new InvalidOperationException("Python verify-command contains shell metacharacters.");

        return CommandTokenRegex()
            .Matches(command)
            .Select(match => match.Value.Trim('"', '\''))
            .Where(token => token.Length > 0)
            .ToArray();
    }

    private static string ResolveVenvPython(string venvDir) =>
        OperatingSystem.IsWindows()
            ? Path.Combine(venvDir, "Scripts", "python.exe")
            : Path.Combine(venvDir, "bin", "python");

    private static string FormatCommandLine(string fileName, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { fileName }.Concat(arguments.Select(QuoteArgument)));

    private static string QuoteArgument(string argument) =>
        argument.Contains(' ') ? "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : argument;

    [GeneratedRegex("""(?:"[^"]+"|'[^']+'|\S+)""")]
    private static partial Regex CommandTokenRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$")]
    private static partial Regex PythonModuleNameRegex();

    private readonly record struct ModuleEntrypoint(string RelativePath, string FullPath);

    private readonly record struct ModuleRuntimeCommandSpec(
        string FileName,
        IReadOnlyList<string> Arguments);
}

internal interface IModuleRuntimeCommandRunner
{
    Task<ModuleRuntimeCommandResult> RunAsync(
        ModuleRuntimeCommand command,
        CancellationToken ct = default);
}

internal sealed class ModuleRuntimeCommandRunner : IModuleRuntimeCommandRunner
{
    public async Task<ModuleRuntimeCommandResult> RunAsync(
        ModuleRuntimeCommand command,
        CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var timedOut = false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command.FileName,
                WorkingDirectory = command.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in command.Arguments)
                psi.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => AppendLine(stdout, e.Data, command.OutputLimit);
            process.ErrorDataReceived += (_, e) => AppendLine(stderr, e.Data, command.OutputLimit);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(command.Timeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                timedOut = true;
                try { process.Kill(entireProcessTree: true); }
                catch { /* best effort */ }
            }

            return new ModuleRuntimeCommandResult(
                command.FileName,
                command.Arguments,
                command.WorkingDirectory,
                timedOut ? -1 : process.ExitCode,
                timedOut,
                stdout.ToString(),
                stderr.ToString(),
                Stopwatch.GetElapsedTime(started));
        }
        catch (Win32Exception ex)
        {
            return new ModuleRuntimeCommandResult(
                command.FileName,
                command.Arguments,
                command.WorkingDirectory,
                -1,
                false,
                stdout.ToString(),
                ex.Message,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private static void AppendLine(StringBuilder builder, string? line, int outputLimit)
    {
        if (line is null || builder.Length >= outputLimit)
            return;

        var remaining = outputLimit - builder.Length;
        if (line.Length + Environment.NewLine.Length <= remaining)
        {
            builder.AppendLine(line);
            return;
        }

        builder.AppendLine(line[..Math.Max(0, remaining - Environment.NewLine.Length)]);
    }
}

internal sealed record ModuleRuntimeVerificationRequest(
    [property: JsonPropertyName("install_dependencies")] bool InstallDependencies = false,
    [property: JsonPropertyName("run_declared_verify")] bool RunDeclaredVerify = true,
    [property: JsonPropertyName("timeout_seconds")] int? TimeoutSeconds = null);

internal sealed record ModuleRuntimeVerificationResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("runtime")] string Runtime,
    [property: JsonPropertyName("ran")] bool Ran,
    [property: JsonPropertyName("steps")] IReadOnlyList<ModuleRuntimeVerificationStepResult> Steps,
    [property: JsonPropertyName("error")] string? Error);

internal sealed record ModuleRuntimeVerificationStepResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("working_directory")] string WorkingDirectory,
    [property: JsonPropertyName("exit_code")] int ExitCode,
    [property: JsonPropertyName("timed_out")] bool TimedOut,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("stdout")] string Stdout,
    [property: JsonPropertyName("stderr")] string Stderr,
    [property: JsonPropertyName("elapsed_milliseconds")] double ElapsedMilliseconds,
    [property: JsonPropertyName("failure_summary")] string? FailureSummary);

internal sealed record ModuleRuntimeCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan Timeout,
    int OutputLimit);

internal sealed record ModuleRuntimeCommandResult(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    int ExitCode,
    bool TimedOut,
    string Stdout,
    string Stderr,
    TimeSpan Elapsed);
