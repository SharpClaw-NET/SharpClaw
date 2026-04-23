using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Utils.Instances;
using SharpClaw.Utils.Logging;

namespace SharpClaw.Tests.Gateway;

[TestFixture]
[NonParallelizable]
public class InternalApiClientResolutionTests
{
    private string? _previousInstanceRoot;
    private string? _previousSharedRoot;

    [SetUp]
    public void SetUp()
    {
        _previousInstanceRoot = Environment.GetEnvironmentVariable("SHARPCLAW_INSTANCE_ROOT");
        _previousSharedRoot = Environment.GetEnvironmentVariable("SHARPCLAW_SHARED_ROOT");
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable("SHARPCLAW_INSTANCE_ROOT", _previousInstanceRoot);
        Environment.SetEnvironmentVariable("SHARPCLAW_SHARED_ROOT", _previousSharedRoot);
    }

    [Test]
    public async Task GetAsync_WhenExplicitApiKeyFilePathConfigured_UsesThatFile()
    {
        var gatewayRoot = CreateTempDirectory();
        var sharedRoot = CreateTempDirectory();
        var logsRoot = CreateTempDirectory();
        var apiKeyPath = Path.Combine(sharedRoot, "runtime", ".api-key");
        Directory.CreateDirectory(Path.GetDirectoryName(apiKeyPath)!);
        File.WriteAllText(apiKeyPath, "explicit-api-key");
        Environment.SetEnvironmentVariable("SHARPCLAW_INSTANCE_ROOT", gatewayRoot);
        Environment.SetEnvironmentVariable("SHARPCLAW_SHARED_ROOT", sharedRoot);

        try
        {
            using var sessionLogs = new SessionLogWriter("gateway-tests", logsRoot);
            var handler = new CaptureHandler();
            var httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri("http://127.0.0.1:48923")
            };

            var client = new InternalApiClient(
                httpClient,
                Options.Create(new InternalApiOptions
                {
                    BaseUrl = "http://127.0.0.1:48923",
                    ApiKeyFilePath = apiKeyPath,
                }),
                new HttpContextAccessor(),
                sessionLogs);

            _ = await client.GetAsync<object>("/ping");

            handler.LastRequest.Should().NotBeNull();
            handler.LastRequest!.Headers.GetValues("X-Api-Key").Single().Should().Be("explicit-api-key");
        }
        finally
        {
            DeleteDirectoryIfExists(gatewayRoot);
            DeleteDirectoryIfExists(sharedRoot);
            DeleteDirectoryIfExists(logsRoot);
        }
    }

    [Test]
    public async Task GetAsync_WhenDiscoveryMatchesSelectedBackend_UsesDiscoveryRuntimeFiles()
    {
        var gatewayRoot = CreateTempDirectory();
        var sharedRoot = CreateTempDirectory();
        var logsRoot = CreateTempDirectory();
        var installAnchor = Path.Combine(sharedRoot, "gateway-install");
        Directory.CreateDirectory(installAnchor);

        try
        {
            var gatewayPaths = new SharpClawInstancePaths(
                SharpClawInstanceKind.Gateway,
                gatewayRoot,
                sharedRoot,
                installAnchor);
            var manifest = gatewayPaths.Manifest;
            manifest.SelectedBackendInstanceId = "backend-a";
            manifest.SelectedBackendBaseUrl = "http://127.0.0.1:48923";
            manifest.SelectedBackendBindingKind = "discovered";
            gatewayPaths.SaveManifest(manifest);

            PublishBackendDiscovery(sharedRoot, "backend-a", "http://127.0.0.1:48923", "discovered-api-key", "discovered-gateway-token");
            Environment.SetEnvironmentVariable("SHARPCLAW_INSTANCE_ROOT", gatewayRoot);
            Environment.SetEnvironmentVariable("SHARPCLAW_SHARED_ROOT", sharedRoot);

            using var sessionLogs = new SessionLogWriter("gateway-tests", logsRoot);
            var handler = new CaptureHandler();
            var httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri("http://127.0.0.1:48923")
            };

            var client = new InternalApiClient(
                httpClient,
                Options.Create(new InternalApiOptions
                {
                    BaseUrl = "http://127.0.0.1:48923",
                }),
                new HttpContextAccessor(),
                sessionLogs);

            _ = await client.GetAsync<object>("/ping");

            handler.LastRequest.Should().NotBeNull();
            handler.LastRequest!.Headers.GetValues("X-Api-Key").Single().Should().Be("discovered-api-key");
            handler.LastRequest.Headers.GetValues("X-Gateway-Token").Single().Should().Be("discovered-gateway-token");
        }
        finally
        {
            DeleteDirectoryIfExists(gatewayRoot);
            DeleteDirectoryIfExists(sharedRoot);
            DeleteDirectoryIfExists(logsRoot);
        }
    }

    private static void PublishBackendDiscovery(
        string sharedRoot,
        string instanceId,
        string baseUrl,
        string apiKey,
        string gatewayToken)
    {
        var discoveryDir = Path.Combine(sharedRoot, "discovery", "instances");
        var instanceRoot = Path.Combine(sharedRoot, "instances", "backend", instanceId);
        var runtimeDir = Path.Combine(instanceRoot, "runtime");
        Directory.CreateDirectory(discoveryDir);
        Directory.CreateDirectory(runtimeDir);
        File.WriteAllText(Path.Combine(instanceRoot, "instance.json"), "{}");

        var apiKeyPath = Path.Combine(runtimeDir, ".api-key");
        var gatewayTokenPath = Path.Combine(runtimeDir, ".gateway-token");
        File.WriteAllText(apiKeyPath, apiKey);
        File.WriteAllText(gatewayTokenPath, gatewayToken);

        var entry = new SharpClawDiscoveryEntry
        {
            InstanceKind = SharpClawInstanceKind.Backend,
            InstanceId = instanceId,
            InstallFingerprint = "backend-fingerprint",
            InstanceRoot = instanceRoot,
            BaseUrl = baseUrl,
            RuntimeDirectory = runtimeDir,
            ApiKeyFilePath = apiKeyPath,
            GatewayTokenFilePath = gatewayTokenPath,
            ProcessId = 12345,
            StartedAtUtc = DateTimeOffset.UtcNow,
            LastSeenUtc = DateTimeOffset.UtcNow,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(entry, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        });

        File.WriteAllText(Path.Combine(discoveryDir, $"backend-{instanceId}.json"), json);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "SharpClaw.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }
}
