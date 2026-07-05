using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace SharpClaw.Tests.Modules;

/// <summary>
/// End-to-end startup smoke test: spawns the real <c>SharpClaw.Runtime.Host</c>
/// executable in headless mode and waits for it to reach the "API listening on"
/// log line, which is emitted from <c>Program.cs</c> PHASE 23 immediately after
/// <c>app.StartAsync()</c> returns successfully — i.e. the login prompt would
/// be available next.
/// <para>
/// This complements <see cref="BundledModuleOutputTests"/>: that one only
/// confirms module DLLs are present on disk and contain an
/// <c>ISharpClawCoreModule</c> implementation, but it cannot detect runtime
/// startup regressions such as duplicate trigger-attribute ownership in
/// <c>TaskScriptParser.RegisterModule</c>, parser-extension primitives
/// collisions, missing infrastructure dependencies, or any other crash that
/// happens between <c>builder.Build()</c> and <c>app.StartAsync()</c>.
/// </para>
/// </summary>
[TestFixture]
[Category("Integration")]
public class ApiHostStartupTests
{
    private const string ListeningMarker = "SharpClaw API listening on";
    private static readonly TimeSpan StartupTimeout  = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(15);

    private static string ResolveApiExecutable()
    {
        // Test assembly lives at: …/SharpClaw.Tests/bin/<config>/net10.0/
        // API output lives at:    …/SharpClaw.Runtime.Host/bin/<config>/net10.0/
        var testBinDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var solutionRoot = Path.GetFullPath(Path.Combine(testBinDir, "..", "..", "..", ".."));
        var config = new DirectoryInfo(testBinDir).Parent!.Name;
        var tfm = new DirectoryInfo(testBinDir).Name;

        var apiOutputDir = Path.Combine(solutionRoot, "SharpClaw.Runtime.Host", "bin", config, tfm);

        // Prefer the platform-native launcher; fall back to `dotnet exec` on the dll.
        var exeName = OperatingSystem.IsWindows()
            ? "SharpClaw.Runtime.Host.exe"
            : "SharpClaw.Runtime.Host";
        var exePath = Path.Combine(apiOutputDir, exeName);
        if (File.Exists(exePath))
            return exePath;

        var dllPath = Path.Combine(apiOutputDir, "SharpClaw.Runtime.Host.dll");
        return File.Exists(dllPath) ? dllPath : exePath;
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Test]
    public async Task ApiHostBootsToLoginReadyState()
    {
        var apiTarget = ResolveApiExecutable();
        File.Exists(apiTarget).Should().BeTrue(
            $"API host binary must be built before this test runs: '{apiTarget}'");

        // Isolated per-test instance root so we never touch the developer's
        // real SharpClaw data directory.
        var instanceRoot = Path.Combine(
            Path.GetTempPath(),
            "SharpClawStartupTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(instanceRoot);

        var port = GetFreeTcpPort();
        var listenUrl = $"http://127.0.0.1:{port}";

        var psi = new ProcessStartInfo
        {
            FileName = apiTarget.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : apiTarget,
            WorkingDirectory = Path.GetDirectoryName(apiTarget)!,
            RedirectStandardInput  = true,  // closing stdin → REPL is skipped, host stays alive
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute = false,
            CreateNoWindow  = true,
        };
        if (apiTarget.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(apiTarget);

        psi.Environment["SHARPCLAW_INSTANCE_ROOT"] = instanceRoot;
        psi.Environment["ASPNETCORE_URLS"] = listenUrl;
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        // Make sure REPL is NOT forced — we want the headless wait branch so
        // closing stdin doesn't immediately terminate the process.
        psi.Environment.Remove("SHARPCLAW_FORCE_REPL");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var listeningSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => CaptureLine(e.Data, stdout, listeningSeen);
        process.ErrorDataReceived  += (_, e) => CaptureLine(e.Data, stderr, listeningSeen);

        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult(process.ExitCode);

        process.Start().Should().BeTrue("API host process must launch");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        // Close stdin so RunInteractiveAsync detects redirected input and
        // takes the headless wait branch instead of trying to read commands.
        try { process.StandardInput.Close(); } catch { /* best effort */ }

        try
        {
            using var timeoutCts = new CancellationTokenSource(StartupTimeout);
            var winner = await Task.WhenAny(
                listeningSeen.Task,
                exited.Task,
                Task.Delay(Timeout.Infinite, timeoutCts.Token));

            if (winner == exited.Task)
            {
                Assert.Fail(BuildFailureMessage(
                    $"API host exited (code {exited.Task.Result}) before reaching '{ListeningMarker}'.",
                    stdout, stderr));
            }
            else if (winner != listeningSeen.Task)
            {
                Assert.Fail(BuildFailureMessage(
                    $"API host did not log '{ListeningMarker}' within {StartupTimeout.TotalSeconds:0}s.",
                    stdout, stderr));
            }

            // Sanity check: the listening URL line should reference the port we asked for.
            stdout.ToString().Should().Contain(listenUrl,
                because: "the host should bind to the URL we requested via ASPNETCORE_URLS");
        }
        finally
        {
            await ShutdownAsync(process, exited.Task);
            TryDeleteDirectory(instanceRoot);
        }
    }

    private static void CaptureLine(string? line, StringBuilder sink, TaskCompletionSource<bool> listeningSeen)
    {
        if (line is null) return;
        lock (sink) sink.AppendLine(line);
        if (line.Contains(ListeningMarker, StringComparison.Ordinal))
            listeningSeen.TrySetResult(true);
    }

    private static async Task ShutdownAsync(Process process, Task<int> exited)
    {
        if (process.HasExited) return;
        try
        {
            // .NET process shutdown — use Kill rather than CloseMainWindow because
            // the API runs with no window. The host's finally block will still
            // dispose the discovery lease and flush logs because Kill triggers
            // process-exit handlers in our code path? No — Kill is hard. That's
            // acceptable for a smoke test: we already proved the host reached
            // ApplicationStarted, which is the assertion under test.
            process.Kill(entireProcessTree: true);
        }
        catch { /* already exiting */ }

        using var cts = new CancellationTokenSource(ShutdownTimeout);
        await Task.WhenAny(exited, Task.Delay(Timeout.Infinite, cts.Token));
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* leave temp dir behind on failure */ }
    }

    private static string BuildFailureMessage(string headline, StringBuilder stdout, StringBuilder stderr)
    {
        string Snapshot(StringBuilder sb) { lock (sb) return sb.ToString(); }
        return headline + Environment.NewLine +
            "──── stdout ────" + Environment.NewLine + Snapshot(stdout) +
            "──── stderr ────" + Environment.NewLine + Snapshot(stderr);
    }
}
