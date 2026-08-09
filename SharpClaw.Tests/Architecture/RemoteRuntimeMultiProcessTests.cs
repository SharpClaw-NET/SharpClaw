using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.RemoteRuntimeBridge;

namespace SharpClaw.Tests.Architecture;

[TestFixture]
[NonParallelizable]
public sealed class RemoteRuntimeMultiProcessTests
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(
        int.TryParse(
            Environment.GetEnvironmentVariable("SHARPCLAW_TEST_STARTUP_TIMEOUT_SECONDS"),
            out var seconds)
            ? seconds
            : 90);

    [Test]
    [Category("Integration")]
    public async Task Runtime_gateway_and_proxy_complete_pairing_and_forward_liveness()
    {
        var root = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "remote-runtime-process-" + Guid.NewGuid().ToString("N"));
        var sharedRoot = Path.Combine(root, "shared");
        var runtimeRoot = Path.Combine(root, "runtime");
        var gatewayRoot = Path.Combine(root, "gateway");
        var proxyRoot = Path.Combine(root, "proxy");
        Directory.CreateDirectory(root);

        var runtimePort = GetFreePort();
        var gatewayPort = GetFreePort();
        var bridgePort = GetFreePort();
        var proxyPort = GetFreePort();
        var runtimeUrl = $"http://127.0.0.1:{runtimePort}";
        var gatewayUrl = $"http://127.0.0.1:{gatewayPort}";
        var bridgeUrl = $"https://127.0.0.1:{bridgePort}";
        var proxyUrl = $"http://127.0.0.1:{proxyPort}";
        var certificatePath = Path.Combine(root, "gateway-bridge.pfx");
        using var gatewayCertificate = CreateServerCertificate();
        File.WriteAllBytes(
            certificatePath,
            gatewayCertificate.Export(X509ContentType.Pfx));

        var runtimeBinary = PrepareHostBinary(
            ResolveBinary("SharpClaw.Runtime\\Host", "SharpClaw.Runtime.Host"),
            Path.Combine(root, "process-binaries", "runtime"),
            disableBundledModules: true,
            includeInProcessTestHarness: true);
        var proxyBinary = PrepareHostBinary(
            ResolveBinary("SharpClaw.Runtime\\Host", "SharpClaw.Runtime.Host"),
            Path.Combine(root, "process-binaries", "proxy"),
            disableBundledModules: true);
        var gatewayBinary = PrepareHostBinary(
            ResolveBinary("SharpClaw.Gateway", "SharpClaw.Gateway"),
            Path.Combine(root, "process-binaries", "gateway"),
            disableBundledModules: false);

        var gatewayPaths = new SharpClawInstancePaths(
            SharpClawInstanceKind.Gateway,
            gatewayRoot,
            sharedRoot);
        gatewayPaths.EnsureDirectories();
        var gatewayInstanceId = gatewayPaths.Manifest.InstanceId;

        var proxyPaths = new SharpClawInstancePaths(
            SharpClawInstanceKind.Backend,
            proxyRoot,
            sharedRoot);
        proxyPaths.EnsureDirectories();
        var proxyInstanceId = proxyPaths.Manifest.InstanceId;

        ChildProcess? runtime = null;
        ChildProcess? gateway = null;
        ChildProcess? proxy = null;
        try
        {
            runtime = StartProcess(
                runtimeBinary,
                new Dictionary<string, string?>
                {
                    ["ASPNETCORE_URLS"] = runtimeUrl,
                    ["ASPNETCORE_ENVIRONMENT"] = "Production",
                    ["DOTNET_ENVIRONMENT"] = "Production",
                    ["SHARPCLAW_INSTANCE_ROOT"] = runtimeRoot,
                    ["SHARPCLAW_SHARED_ROOT"] = sharedRoot,
                });
            var runtimeEntry = await WaitForDiscoveryAsync(
                sharedRoot,
                "backend-",
                runtime,
                StartupTimeout);
            runtimeEntry.BaseUrl.Should().Be(runtimeUrl);
            runtimeEntry.InstanceId.Should().NotBe(proxyInstanceId);
            var runtimeApiKey = ReadRequired(runtimeEntry.ApiKeyFilePath);
            var runtimeFingerprint = runtimeEntry.InstallFingerprint;

            using (var runtimeReadinessClient = new HttpClient
            {
                BaseAddress = new Uri(runtimeUrl),
                Timeout = TimeSpan.FromSeconds(10),
            })
            using (var runtimeReadinessResponse = await WaitForAsync(
                cancellationToken => runtimeReadinessClient.GetAsync(
                    "/echo",
                    cancellationToken),
                StartupTimeout,
                runtime))
            {
                runtimeReadinessResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            }

            gateway = StartProcess(
                gatewayBinary,
                new Dictionary<string, string?>
                {
                    ["ASPNETCORE_URLS"] = gatewayUrl,
                    ["ASPNETCORE_ENVIRONMENT"] = "Production",
                    ["DOTNET_ENVIRONMENT"] = "Production",
                    ["SHARPCLAW_INSTANCE_ROOT"] = gatewayRoot,
                    ["SHARPCLAW_SHARED_ROOT"] = sharedRoot,
                    ["SharpClawInstance__SelectedBackendInstanceId"] = runtimeEntry.InstanceId,
                    ["SharpClawInstance__SelectedBackendBaseUrl"] = runtimeUrl,
                    ["Gateway__RemoteRuntimeBridge__Enabled"] = "true",
                    ["Gateway__RemoteRuntimeBridge__ListenUrl"] = bridgeUrl,
                    ["Gateway__RemoteRuntimeBridge__ServerCertificatePath"] = certificatePath,
                    ["Gateway__RemoteRuntimeBridge__AdministrationKey"] = "process-admin-key",
                    ["Gateway__RemoteRuntimeBridge__MaxConcurrentRequestsPerPair"] = "4",
                    ["Gateway__RemoteRuntimeBridge__MaxConcurrentStreamsPerPair"] = "2",
                    ["Gateway__RemoteRuntimeBridge__MaxConcurrentWebSocketsPerPair"] = "2",
                });
            await WaitForTcpPortAsync(bridgePort, gateway, StartupTimeout);

            using var bridgeClient = CreateCertificateClient(gatewayCertificate);
            bridgeClient.BaseAddress = new Uri(bridgeUrl);
            bridgeClient.DefaultRequestHeaders.Add(
                RemoteRuntimeBridgePaths.AdministrationKeyHeader,
                "process-admin-key");
            using (var registryReadinessResponse = await WaitForSuccessfulResponseAsync(
                cancellationToken => bridgeClient.GetAsync(
                    RemoteRuntimeBridgePaths.AdminPairings + "?take=1",
                    cancellationToken),
                StartupTimeout,
                gateway))
            {
                registryReadinessResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            }
            using var invitationResponse = await WaitForAsync(
                async cancellationToken =>
                {
                    using var content = new StringContent(
                        JsonSerializer.Serialize(new RemoteRuntimePairingAdminInvitationRequest(120)),
                        Encoding.UTF8,
                        "application/json");
                    return await bridgeClient.PostAsync(
                        RemoteRuntimeBridgePaths.AdminInvitation,
                        content,
                        cancellationToken);
                },
                StartupTimeout,
                gateway);
            var invitationFailureDiagnostics = invitationResponse.IsSuccessStatusCode
                ? string.Empty
                : "\nGateway diagnostics:\n" + gateway.Diagnostics;
            invitationResponse.StatusCode.Should().Be(
                HttpStatusCode.OK,
                because: "the bridge invitation endpoint must be available"
                    + invitationFailureDiagnostics);
            var invitation = await invitationResponse.Content
                .ReadFromJsonAsync<RemoteRuntimePairingInvitation>();
            invitation.Should().NotBeNull();

            proxy = StartProcess(
                proxyBinary,
                new Dictionary<string, string?>
                {
                    ["ASPNETCORE_URLS"] = proxyUrl,
                    ["ASPNETCORE_ENVIRONMENT"] = "Production",
                    ["DOTNET_ENVIRONMENT"] = "Production",
                    ["SHARPCLAW_INSTANCE_ROOT"] = proxyRoot,
                    ["SHARPCLAW_SHARED_ROOT"] = sharedRoot,
                    ["Runtime__RemoteProxy__Enabled"] = "true",
                    ["Runtime__RemoteProxy__LocalUrl"] = proxyUrl,
                    ["Runtime__RemoteProxy__GatewayUrl"] = bridgeUrl,
                    ["Runtime__RemoteProxy__GatewayInstanceId"] = gatewayInstanceId,
                    ["Runtime__RemoteProxy__AuthoritativeRuntimeInstanceId"] = runtimeEntry.InstanceId,
                    ["Runtime__RemoteProxy__ProxyRuntimeInstanceId"] = proxyInstanceId,
                    ["Runtime__RemoteProxy__InvitationSecret"] = invitation!.Secret,
                    ["Runtime__RemoteProxy__PrivateKeySecret"] = "proxy-private-key",
                    ["Runtime__RemoteProxy__ClientCertificateSecret"] = "proxy-client-certificate",
                    ["Runtime__RemoteProxy__ConnectTimeoutSeconds"] = "10",
                    ["Runtime__RemoteProxy__ActivityTimeoutSeconds"] = "60",
                    ["Runtime__RemoteProxy__InvitationPairId"] = invitation.PairId.ToString("D"),
                    ["Runtime__RemoteProxy__GatewayServerPublicKeyHash"] = invitation.GatewayServerPublicKeyHash,
                    ["Runtime__RemoteProxy__AuthoritativeRuntimeInstallFingerprint"] = runtimeFingerprint,
                    ["Runtime__RemoteProxy__InvitationExpiresAtUtc"] = invitation.ExpiresAtUtc.ToString("O"),
                    ["Runtime__RemoteProxy__BridgeProtocolMajor"] = invitation.BridgeProtocolMajor.ToString(),
                });

            var pendingPair = await WaitForPairAsync(
                bridgeClient,
                invitation.PairId,
                RemoteRuntimePairStatus.ClaimPending,
                proxy,
                gateway,
                StartupTimeout);
            pendingPair.ProxyRuntimeInstanceId.Should().Be(proxyInstanceId);

            using var approvalResponse = await bridgeClient.PostAsJsonAsync(
                RemoteRuntimeBridgePaths.AdminApprove,
                new RemoteRuntimePairingAdminApprovalRequest(
                    invitation.PairId,
                    proxyInstanceId,
                    runtimeEntry.InstanceId));
            var approvalBody = await approvalResponse.Content.ReadAsStringAsync();
            approvalResponse.StatusCode.Should().Be(HttpStatusCode.OK, approvalBody);

            var approvedPair = await WaitForPairAsync(
                bridgeClient,
                invitation.PairId,
                RemoteRuntimePairStatus.Active,
                gateway,
                gateway,
                StartupTimeout);
            approvedPair.GatewayInstanceId.Should().Be(gatewayInstanceId);
            approvedPair.AuthoritativeRuntimeInstanceId.Should().Be(runtimeEntry.InstanceId);
            approvedPair.ExpiresAtUtc.Should().BeAfter(DateTimeOffset.UtcNow);

            var runtimePaths = new SharpClawInstancePaths(
                SharpClawInstanceKind.Backend,
                runtimeRoot,
                sharedRoot);
            using var runtimeProbe = new HttpClient
            {
                BaseAddress = new Uri(runtimeUrl),
            };
            runtimeProbe.DefaultRequestHeaders.Add("X-Api-Key", runtimeApiKey);
            runtimeProbe.DefaultRequestHeaders.Add(
                "X-Gateway-Token",
                ReadRequired(runtimePaths.GatewayTokenFilePath));
            using var activeProbe = await runtimeProbe.GetAsync(
                RemoteRuntimeBridgePaths.RegistryActive
                + "?gatewayInstanceId="
                + Uri.EscapeDataString(gatewayInstanceId)
                + "&authoritativeRuntimeInstanceId="
                + Uri.EscapeDataString(runtimeEntry.InstanceId));
            var activeProbeBody = await activeProbe.Content.ReadAsStringAsync();
            activeProbe.StatusCode.Should().Be(HttpStatusCode.OK, activeProbeBody);
            activeProbeBody.Should().NotBeNullOrWhiteSpace();

            var proxyEntry = await WaitForDiscoveryAsync(
                sharedRoot,
                "backend-",
                proxy,
                StartupTimeout,
                runtimeEntry.InstanceId,
                gateway,
                runtime);
            var localApiKey = ReadRequired(proxyEntry.ApiKeyFilePath);
            using var proxyClient = new HttpClient
            {
                BaseAddress = new Uri(proxyUrl),
                Timeout = TimeSpan.FromSeconds(30),
            };
            proxyClient.DefaultRequestHeaders.Add("X-Api-Key", localApiKey);
            using var livenessResponse = await proxyClient.GetAsync("/echo");
            var livenessBody = await livenessResponse.Content.ReadAsStringAsync();
            livenessResponse.StatusCode.Should().Be(HttpStatusCode.OK, livenessBody);

            TestContext.Progress.WriteLine(
                $"multi-process runtime={runtimeEntry.InstanceId} gateway={gatewayInstanceId} proxy={proxyInstanceId} "
                + $"pair={invitation.PairId} liveness={(int)livenessResponse.StatusCode}");
        }
        finally
        {
            if (proxy is not null)
                await proxy.DisposeAsync();
            if (gateway is not null)
                await gateway.DisposeAsync();
            if (runtime is not null)
                await runtime.DisposeAsync();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static ChildProcess StartProcess(
        string assemblyPath,
        IReadOnlyDictionary<string, string?> environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(assemblyPath)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(assemblyPath);
        foreach (var pair in environment)
        {
            if (pair.Value is null)
                startInfo.Environment.Remove(pair.Key);
            else
                startInfo.Environment[pair.Key] = pair.Value;
        }

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var exited = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
                lock (stdout)
                    stdout.AppendLine(args.Data);
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
                lock (stderr)
                    stderr.AppendLine(args.Data);
        };
        process.Exited += (_, _) =>
        {
            try
            {
                exited.TrySetResult(process.ExitCode);
            }
            catch (InvalidOperationException)
            {
                exited.TrySetResult(-1);
            }
        };
        process.Start().Should().BeTrue();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close();
        return new ChildProcess(process, stdout, stderr, exited.Task);
    }

    private static async Task<SharpClawDiscoveryEntry> WaitForDiscoveryAsync(
        string sharedRoot,
        string filePrefix,
        ChildProcess process,
        TimeSpan timeout,
        string? excludedInstanceId = null,
        ChildProcess? gatewayProcess = null,
        ChildProcess? runtimeProcess = null)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                process.ThrowIfExited();
            }
            catch (AssertionException exception)
            {
                var relatedDiagnostics = gatewayProcess is null && runtimeProcess is null
                    ? string.Empty
                    : "\nGateway diagnostics:\n"
                        + (gatewayProcess?.Diagnostics ?? "<not started>")
                        + "\nRuntime diagnostics:\n"
                        + (runtimeProcess?.Diagnostics ?? "<not started>");
                throw new AssertionException(exception.Message + relatedDiagnostics, exception);
            }
            var directory = Path.Combine(sharedRoot, "discovery", "instances");
            if (Directory.Exists(directory))
            {
                foreach (var path in Directory.EnumerateFiles(directory, filePrefix + "*.json"))
                {
                    try
                    {
                        var entry = JsonSerializer.Deserialize<SharpClawDiscoveryEntry>(
                            await File.ReadAllTextAsync(path),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (entry is not null
                            && (excludedInstanceId is null
                                || !string.Equals(entry.InstanceId, excludedInstanceId, StringComparison.Ordinal)))
                        {
                            return entry;
                        }
                    }
                    catch (JsonException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                }
            }

            await Task.Delay(100);
        }

        throw new AssertionException(
            $"Process did not publish a discovery entry.\n{process.Diagnostics}"
            + (gatewayProcess is null && runtimeProcess is null
                ? string.Empty
                : "\nGateway diagnostics:\n"
                    + (gatewayProcess?.Diagnostics ?? "<not started>")
                    + "\nRuntime diagnostics:\n"
                    + (runtimeProcess?.Diagnostics ?? "<not started>")));
    }

    private static async Task WaitForTcpPortAsync(
        int port,
        ChildProcess process,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            process.ThrowIfExited();
            try
            {
                using var client = new TcpClient();
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await client.ConnectAsync(IPAddress.Loopback, port, cancellation.Token);
                return;
            }
            catch (SocketException)
            {
            }
            catch (OperationCanceledException)
            {
            }

            await Task.Delay(100);
        }

        throw new AssertionException($"Port {port} did not open.\n{process.Diagnostics}");
    }

    private static async Task<HttpResponseMessage> WaitForAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> operation,
        TimeSpan timeout,
        ChildProcess process)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            process.ThrowIfExited();
            var remaining = deadline - DateTimeOffset.UtcNow;
            using var attemptCancellation = new CancellationTokenSource(
                remaining < TimeSpan.FromSeconds(2)
                    ? remaining
                    : TimeSpan.FromSeconds(2));
            try
            {
                return await operation(attemptCancellation.Token);
            }
            catch (HttpRequestException exception)
            {
                last = exception;
            }
            catch (OperationCanceledException exception)
            {
                last = exception;
            }

            await Task.Delay(250);
        }

        throw new AssertionException(
            $"HTTP operation did not become available. {last?.Message}\n{process.Diagnostics}");
    }

    private static async Task<HttpResponseMessage> WaitForSuccessfulResponseAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> operation,
        TimeSpan timeout,
        ChildProcess process)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            process.ThrowIfExited();
            var remaining = deadline - DateTimeOffset.UtcNow;
            using var attemptCancellation = new CancellationTokenSource(
                remaining < TimeSpan.FromSeconds(2)
                    ? remaining
                    : TimeSpan.FromSeconds(2));
            try
            {
                var response = await operation(attemptCancellation.Token);
                if (response.IsSuccessStatusCode)
                    return response;

                last = new HttpRequestException(
                    $"HTTP {(int)response.StatusCode} ({response.StatusCode}).");
                response.Dispose();
            }
            catch (HttpRequestException exception)
            {
                last = exception;
            }
            catch (OperationCanceledException exception)
            {
                last = exception;
            }

            await Task.Delay(250);
        }

        throw new AssertionException(
            $"HTTP operation did not return success. {last?.Message}\n{process.Diagnostics}");
    }

    private static async Task<RemoteRuntimeRegistryPageResponse> ReadPairingsAsync(
        HttpClient client,
        ChildProcess process,
        ChildProcess? gatewayProcess = null)
    {
        using var response = await WaitForAsync(
            cancellationToken => client.GetAsync(
                RemoteRuntimeBridgePaths.AdminPairings + "?take=20",
                cancellationToken),
            TimeSpan.FromSeconds(10),
            process);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            because: $"pairing registry response was {body}. "
                + $"Gateway diagnostics: {gatewayProcess?.Diagnostics}");
        return await response.Content.ReadFromJsonAsync<RemoteRuntimeRegistryPageResponse>()
            ?? throw new AssertionException("The pairing page response was empty.");
    }

    private static async Task<RemoteRuntimePairingRegistrySnapshot> WaitForPairAsync(
        HttpClient client,
        Guid pairId,
        RemoteRuntimePairStatus status,
        ChildProcess process,
        ChildProcess gatewayProcess,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var page = await ReadPairingsAsync(client, process, gatewayProcess);
            var entry = page.Items.SingleOrDefault(item => item.PairId == pairId);
            if (entry?.Status == status)
                return entry;

            process.ThrowIfExited();
            await Task.Delay(250);
        }

        throw new AssertionException(
            $"Pair {pairId} did not reach {status}.\n{process.Diagnostics}");
    }

    private static HttpClient CreateCertificateClient(X509Certificate2 certificate)
    {
        var expectedHash = RemoteRuntimeCertificateHash.Compute(certificate);
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, presented, _, _) =>
                presented is not null
                && RemoteRuntimeCertificateHash.Compute(presented) == expectedHash,
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    private static string ReadRequired(string path)
    {
        path.Should().NotBeNullOrWhiteSpace();
        File.Exists(path).Should().BeTrue();
        var value = File.ReadAllText(path).Trim();
        value.Should().NotBeNullOrWhiteSpace();
        return value;
    }

    private static string ResolveBinary(string project, string assemblyName)
    {
        var testDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var solutionRoot = Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT")
            ?? Path.GetFullPath(Path.Combine(testDirectory, "..", "..", "..", ".."));
        var outputDirectory = new DirectoryInfo(testDirectory);
        var parentDirectory = outputDirectory.Parent;
        var configuration = parentDirectory?.Name is "Debug" or "Release"
            ? parentDirectory.Name
            : outputDirectory.Name;
        var targetFramework = parentDirectory?.Name is "Debug" or "Release"
            ? outputDirectory.Name
            : null;
        var artifactRoot = Environment.GetEnvironmentVariable("SHARPCLAW_ARTIFACTS_PATH");
        var candidates = new[]
        {
            string.IsNullOrWhiteSpace(artifactRoot)
                ? null
                : Path.Combine(
                    artifactRoot,
                    "bin",
                    assemblyName,
                    configuration.ToLowerInvariant(),
                    assemblyName + ".dll"),
            targetFramework is null
                ? null
                : Path.Combine(solutionRoot, project, "bin", configuration, targetFramework, assemblyName + ".dll"),
            Path.Combine(solutionRoot, project, "bin", configuration, assemblyName + ".dll"),
            Path.Combine(testDirectory, assemblyName + ".dll"),
        };

        var path = candidates.FirstOrDefault(File.Exists);
        path.Should().NotBeNull($"the test requires a built process named '{assemblyName}.dll'");
        return path!;
    }

    private static string PrepareHostBinary(
        string sourceBinary,
        string destinationDirectory,
        bool disableBundledModules,
        bool includeInProcessTestHarness = false)
    {
        CopyDirectory(
            Path.GetDirectoryName(sourceBinary)!,
            destinationDirectory,
            skipMutableDirectories: true);

        var environmentDirectory = Path.Combine(destinationDirectory, "Environment");
        var templatePath = Path.Combine(environmentDirectory, ".env.template");
        var activePath = Path.Combine(environmentDirectory, ".env");
        if (File.Exists(templatePath))
            File.Copy(templatePath, activePath, overwrite: true);

        var developmentTemplatePath = Path.Combine(environmentDirectory, ".dev.env.template");
        var developmentPath = Path.Combine(environmentDirectory, ".dev.env");
        if (File.Exists(developmentTemplatePath))
            File.Copy(developmentTemplatePath, developmentPath, overwrite: true);

        if (disableBundledModules)
        {
            var moduleIds = new[]
            {
                "sharpclaw_agent_orchestration",
                "sharpclaw_editor_common",
                "sharpclaw_metrics",
                "sharpclaw_module_dev",
                "sharpclaw_providers_anthropic",
                "sharpclaw_providers_google",
                "sharpclaw_providers_llamasharp",
                "sharpclaw_providers_ollama",
                "sharpclaw_providers_openai_compat",
                "sharpclaw_test_harness_out_of_process",
                "sharpclaw_test_harness_in_process",
                "sharpclaw_vs2026_editor",
                "sharpclaw_vscode_editor",
            };

            DisableModules(activePath, moduleIds);
            DisableModules(developmentPath, moduleIds);
            var modulesDirectory = Path.Combine(destinationDirectory, "modules");
            if (Directory.Exists(modulesDirectory))
                Directory.Delete(modulesDirectory, recursive: true);
        }

        if (includeInProcessTestHarness)
        {
            var testDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var sourceModuleDirectory = Path.Combine(
                testDirectory,
                "test-modules",
                "sharpclaw_test_harness_in_process");
            Directory.Exists(sourceModuleDirectory).Should().BeTrue(
                $"the test requires the packaged in-process module at '{sourceModuleDirectory}'");

            var destinationModuleDirectory = Path.Combine(
                destinationDirectory,
                "modules",
                "sharpclaw_test_harness_in_process");
            CopyDirectory(
                sourceModuleDirectory,
                destinationModuleDirectory,
                skipMutableDirectories: false);
            SetEnvironmentValue(activePath, "Modules__sharpclaw_test_harness_in_process", "true");
            SetEnvironmentValue(activePath, "Provider__Key", "sharpclaw-test");
            SetEnvironmentValue(activePath, "Provider__Model", "test-harness-model");
        }

        return Path.Combine(destinationDirectory, Path.GetFileName(sourceBinary));
    }

    private static void SetEnvironmentValue(string path, string key, string value)
    {
        var lines = File.Exists(path)
            ? File.ReadAllLines(path).ToList()
            : [];
        var prefix = key + "=";
        var index = lines.FindIndex(line =>
            line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var line = prefix + "\"" + value + "\"";
        if (index >= 0)
            lines[index] = line;
        else
            lines.Add(line);
        File.WriteAllLines(path, lines);
    }

    private static void DisableModules(string path, IReadOnlyList<string> moduleIds)
    {
        if (!File.Exists(path))
            return;

        var lines = File.ReadAllLines(path).ToList();
        foreach (var moduleId in moduleIds)
        {
            var key = $"Modules__{moduleId}=";
            var index = lines.FindIndex(line =>
                line.StartsWith(key, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                lines[index] = key + "\"false\"";
            else
                lines.Add(key + "\"false\"");
        }

        File.WriteAllLines(path, lines);
    }

    private static void CopyDirectory(
        string sourceDirectory,
        string destinationDirectory,
        bool skipMutableDirectories)
    {
        var destinationRoot = Path.GetFullPath(destinationDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var destinationPrefix = destinationRoot + Path.DirectorySeparatorChar;

        Directory.CreateDirectory(destinationDirectory);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            var destination = Path.Combine(destinationDirectory, Path.GetFileName(file));
            File.Copy(file, destination, overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            var name = Path.GetFileName(directory);
            var fullDirectory = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(fullDirectory, destinationRoot, StringComparison.OrdinalIgnoreCase)
                || fullDirectory.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase)
                || (skipMutableDirectories
                    && name is "Data" or "config" or "durable" or "logs" or "runtime" or "process-binaries"))
                continue;

            CopyDirectory(
                directory,
                Path.Combine(destinationDirectory, name),
                skipMutableDirectories);
        }
    }

    private static X509Certificate2 CreateServerCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=remote-runtime-process-test", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(10));
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class ChildProcess(
        Process process,
        StringBuilder stdout,
        StringBuilder stderr,
        Task<int> exited) : IAsyncDisposable
    {
        public string Diagnostics
        {
            get
            {
                string Read(StringBuilder value)
                {
                    lock (value)
                        return value.ToString();
                }

                return "stdout:\n" + Read(stdout) + "\nstderr:\n" + Read(stderr);
            }
        }

        public void ThrowIfExited()
        {
            if (process.HasExited)
                throw new AssertionException(
                    $"Child process exited with code {process.ExitCode}.\n{Diagnostics}");
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }

            await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(2)));
            process.Dispose();
        }
    }
}
