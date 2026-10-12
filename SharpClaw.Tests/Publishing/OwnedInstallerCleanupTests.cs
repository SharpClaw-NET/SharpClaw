using System.Diagnostics;
using System.Text.Json;

namespace SharpClaw.Tests.Publishing;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "NUnit constructs this fixture through reflection.")]
[TestFixture]
internal sealed class OwnedInstallerCleanupTests
{
    [TestCase("snapshot"), TestCase("stop"), TestCase("wait"), TestCase("state"), TestCase("trust")]
    public async Task IndependentOwnedTrustRetirementIsAttemptedAfterOtherCleanupFailuresAsync(string phase)
    {
        var root = Path.Combine(Path.GetTempPath(), "owned-cleanup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sourceRoot = Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT") ?? FindSourceRoot();
            var source = await File.ReadAllTextAsync(Path.Combine(sourceRoot, "scripts", "test-installed-msix.ps1"),
                TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            var start = source.IndexOf("function Invoke-TestOwnedRetirement", StringComparison.Ordinal);
            var end = source.IndexOf("\nSet-StrictMode", start, StringComparison.Ordinal);
            start.Should().BeGreaterThan(0); end.Should().BeGreaterThan(start);
            // Execute only the pure delegate cleanup owner, never the installer
            // entrypoint, certificate types, process probes or live cleanup bodies.
            var harness = Path.Combine(root, "controlled-cleanup.ps1");
            await File.WriteAllTextAsync(harness, source[start..end] + Harness,
                TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            var result = await RunHarnessAsync(harness, phase).ConfigureAwait(false);
            using var data = JsonDocument.Parse(result);
            data.RootElement.GetProperty("Trust").GetInt32().Should().Be(1);
            data.RootElement.GetProperty("State").GetInt32().Should().Be(string.Equals(phase, "snapshot", StringComparison.Ordinal) || string.Equals(phase, "wait", StringComparison.Ordinal) ? 0 : 1);
            data.RootElement.GetProperty("Stop").GetInt32().Should().Be(string.Equals(phase, "snapshot", StringComparison.Ordinal) ? 0 : 1);
            data.RootElement.GetProperty("Failures").GetArrayLength().Should().BeGreaterThan(0);
            data.RootElement.GetProperty("Failures").EnumerateArray().Select(item => item.GetString())
                .Should().Contain(item => item != null && item.Contains("controlled-" + phase, StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<string> RunHarnessAsync(string harness, string phase)
    {
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File", harness, phase }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start owned pure cleanup fixture.");
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        process.ExitCode.Should().Be(0, await stderr.ConfigureAwait(false));
        return await stdout.ConfigureAwait(false);
    }

    private static string FindSourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "scripts", "test-installed-msix.ps1"))) return directory.FullName;
        throw new DirectoryNotFoundException("No maintained installed-test source.");
    }

    private const string Harness =
        "\n" +
        "$ErrorActionPreference = 'Stop'\n" +
        "$phase = $args[0]\n" +
        "$script:trust = 0; $script:state = 0; $script:stop = 0\n" +
        "$failures = [Collections.Generic.List[string]]::new()\n" +
        "Invoke-TestOwnedRetirement -Snapshot {\n" +
        "    if ($phase -eq 'snapshot') { throw 'controlled-snapshot' }\n" +
        "    [pscustomobject]@{ Id = 'exact-owned-fixture' }\n" +
        "} -StopOwned {\n" +
        "    param($owned)\n" +
        "    if ($owned.Id -ne 'exact-owned-fixture') { throw 'foreign-process-refused' }\n" +
        "    $script:stop++\n" +
        "    if ($phase -eq 'stop') { throw 'controlled-stop' }\n" +
        "} -VerifyStopped {\n" +
        "    if ($phase -eq 'wait') { throw 'controlled-wait' }\n" +
        "} -RetireState {\n" +
        "    $script:state++\n" +
        "    if ($phase -eq 'state') { throw 'controlled-state' }\n" +
        "} -RetireTrust {\n" +
        "    $script:trust++\n" +
        "    if ($phase -eq 'trust') { throw 'controlled-trust' }\n" +
        "} -Failures $failures\n" +
        "[ordered]@{ Trust=$script:trust; State=$script:state; Stop=$script:stop; Failures=@($failures) } | ConvertTo-Json -Compress\n";
}
