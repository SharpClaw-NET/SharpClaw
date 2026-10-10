using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Tests.Gateway;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and constructs this internal fixture through reflection; its tests are executed by the maintained test suite.")]
[TestFixture]
[NonParallelizable]
internal sealed class InternalApiClientResolutionTests
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
    public async Task GetAsync_WhenExplicitApiKeyFilePathConfigured_UsesThatFileAsync()
    {
        var gatewayRoot = CreateTempDirectory();
        var sharedRoot = CreateTempDirectory();
        var apiKeyPath = Path.Combine(sharedRoot, "runtime", ".api-key");
        Directory.CreateDirectory(Path.GetDirectoryName(apiKeyPath)!);
        await File.WriteAllTextAsync(apiKeyPath, "explicit-api-key", TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        Environment.SetEnvironmentVariable("SHARPCLAW_INSTANCE_ROOT", gatewayRoot);
        Environment.SetEnvironmentVariable("SHARPCLAW_SHARED_ROOT", sharedRoot);

        try
        {
            using var handler = new CaptureHandler();
            using var httpClient = new HttpClient(handler)
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
                NullLogger<InternalApiClient>.Instance);

            _ = await client.GetAsync<object>("/ping").ConfigureAwait(false);

            handler.LastRequest.Should().NotBeNull();
            handler.LastRequest!.Headers.GetValues("X-Api-Key").Should().ContainSingle().Which.Should().Be("explicit-api-key");
        }
        finally
        {
            DeleteDirectoryIfExists(gatewayRoot);
            DeleteDirectoryIfExists(sharedRoot);
        }
    }

    [Test]
    public async Task GetAsync_WhenDiscoveryMatchesSelectedBackend_UsesDiscoveryRuntimeFilesAsync()
    {
        var gatewayRoot = CreateTempDirectory();
        var sharedRoot = CreateTempDirectory();
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

            using var handler = new CaptureHandler();
            using var httpClient = new HttpClient(handler)
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
                NullLogger<InternalApiClient>.Instance);

            _ = await client.GetAsync<object>("/ping").ConfigureAwait(false);

            handler.LastRequest.Should().NotBeNull();
            handler.LastRequest!.Headers.GetValues("X-Api-Key").Should().ContainSingle().Which.Should().Be("discovered-api-key");
            handler.LastRequest.Headers.GetValues("X-Gateway-Token").Should().ContainSingle().Which.Should().Be("discovered-gateway-token");
        }
        finally
        {
            DeleteDirectoryIfExists(gatewayRoot);
            DeleteDirectoryIfExists(sharedRoot);
        }
    }

    [TestCase(false), TestCase(true)]
    public async Task RemoteTargetCannotBorrowLocalDiscoveryCredentialsAsync(bool selectedInstance)
    {
        var gatewayRoot = CreateTempDirectory();
        var sharedRoot = CreateTempDirectory();
        try
        {
            ConfigureMismatchedDiscovery(gatewayRoot, sharedRoot, selectedInstance);
            using var handler = new CaptureHandler();
            using var http = new HttpClient(handler) { BaseAddress = new Uri("https://remote.example/") };
            var client = CreateRemoteClient(http, apiKey: null);

            Func<Task> send = async () =>
                _ = await client.GetAsync<object>("/ping", TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            await send.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
            handler.LastRequest.Should().BeNull("an unmatched local key must never reach the remote transport");
        }
        finally
        {
            DeleteDirectoryIfExists(gatewayRoot);
            DeleteDirectoryIfExists(sharedRoot);
        }
    }

    [TestCase(false), TestCase(true)]
    public async Task ExplicitRemoteApiKeyDoesNotBorrowLocalGatewayTokenAsync(bool selectedInstance)
    {
        var gatewayRoot = CreateTempDirectory();
        var sharedRoot = CreateTempDirectory();
        try
        {
            ConfigureMismatchedDiscovery(gatewayRoot, sharedRoot, selectedInstance);
            using var handler = new CaptureHandler();
            using var http = new HttpClient(handler) { BaseAddress = new Uri("https://remote.example/") };
            var client = CreateRemoteClient(http, "remote-api-key");

            _ = await client.GetAsync<object>("/ping", TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            handler.LastRequest.Should().NotBeNull();
            var request = handler.LastRequest!;
            request.RequestUri.Should().Be(new Uri("https://remote.example/ping"));
            request.Headers.GetValues("X-Api-Key").Should().Equal("remote-api-key");
            request.Headers.Contains("X-Gateway-Token").Should().BeFalse();
        }
        finally
        {
            DeleteDirectoryIfExists(gatewayRoot);
            DeleteDirectoryIfExists(sharedRoot);
        }
    }

    private static InternalApiClient CreateRemoteClient(HttpClient http, string? apiKey) => new(
        http, Options.Create(new InternalApiOptions { BaseUrl = "https://remote.example/", ApiKey = apiKey }),
        new HttpContextAccessor(), NullLogger<InternalApiClient>.Instance);

    [TestCase("https://other.example/steal")]
    [TestCase("//other.example/steal")]
    public async Task ForeignRequestAuthorityCannotReceiveTheConfiguredCredentialAsync(string target)
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://remote.example/") };
        var client = CreateRemoteClient(http, "remote-api-key");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(target, UriKind.RelativeOrAbsolute));
        Func<Task> send = async () =>
        {
            using var response = await client.SendRawAsync(request, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        };

        await send.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
        handler.LastRequest.Should().BeNull();
        request.Headers.Contains("X-Api-Key").Should().BeFalse();
    }

    [Test]
    public async Task RetargetedHttpClientCannotMoveTheClientsBoundCredentialAsync()
    {
        var gatewayRoot = CreateTempDirectory();
        var sharedRoot = CreateTempDirectory();
        try
        {
            ConfigureMismatchedDiscovery(gatewayRoot, sharedRoot, selectedInstance: false);
            using var handler = new CaptureHandler();
            using var http = new HttpClient(handler) { BaseAddress = new Uri("https://remote.example/") };
            var client = CreateRemoteClient(http, "remote-api-key");
            http.BaseAddress = new Uri("https://other.example/");

            _ = await client.GetAsync<object>("/ping", TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            handler.LastRequest.Should().NotBeNull();
            handler.LastRequest!.RequestUri.Should().Be(new Uri("https://remote.example/ping"));
            handler.LastRequest.Headers.GetValues("X-Api-Key").Should().Equal("remote-api-key");
        }
        finally
        {
            DeleteDirectoryIfExists(gatewayRoot);
            DeleteDirectoryIfExists(sharedRoot);
        }
    }

    private static void ConfigureMismatchedDiscovery(string gatewayRoot, string sharedRoot, bool selectedInstance)
    {
        Environment.SetEnvironmentVariable("SHARPCLAW_INSTANCE_ROOT", gatewayRoot);
        Environment.SetEnvironmentVariable("SHARPCLAW_SHARED_ROOT", sharedRoot);
        PublishBackendDiscovery(sharedRoot, "local-backend", "http://127.0.0.1:48923/", "local-api-key", "local-gateway-token");
        if (!selectedInstance) return;
        var paths = new SharpClawInstancePaths(SharpClawInstanceKind.Gateway, gatewayRoot, sharedRoot);
        var manifest = paths.Manifest;
        manifest.SelectedBackendInstanceId = "local-backend";
        manifest.SelectedBackendBaseUrl = "http://127.0.0.1:48923/";
        manifest.SelectedBackendBindingKind = "discovered";
        paths.SaveManifest(manifest);
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

        var json = System.Text.Json.JsonSerializer.Serialize(entry, TestSerializationOptions.CamelCaseIndentedEnums);

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
        catch (IOException exception)
        {
            TestContext.Progress.WriteLine($"Temporary directory cleanup failed: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            TestContext.Progress.WriteLine($"Temporary directory cleanup failed: {exception.Message}");
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
