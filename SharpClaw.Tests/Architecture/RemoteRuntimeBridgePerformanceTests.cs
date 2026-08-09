using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.RemoteRuntimeBridge;
using SharpClaw.Runtime.Host;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.RemoteRuntimeBridge;

namespace SharpClaw.Tests.Architecture;

[TestFixture]
[Category("PerformanceGate")]
[NonParallelizable]
public sealed class RemoteRuntimeBridgePerformanceTests
{
    private const int PayloadBytes = 16 * 1024 * 1024;
    private const int ChunkBytes = 64 * 1024;

    [Test]
    public async Task Streamed_payload_reports_direct_and_two_hop_measurements()
    {
        var root = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "bridge-performance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var certificatePath = Path.Combine(root, "bridge.pfx");
        using var serverCertificate = CreateServerCertificate();
        File.WriteAllBytes(
            certificatePath,
            serverCertificate.Export(X509ContentType.Pfx));

        var upstreamUrl = $"http://127.0.0.1:{GetFreePort()}";
        await using var upstream = await UpstreamHarness.StartAsync(upstreamUrl);
        var target = new RemoteRuntimeBridgeTarget(
            "gateway-performance",
            "runtime-performance",
            "runtime-install-performance",
            upstreamUrl,
            "authoritative-api-key",
            "authoritative-gateway-token");
        await using var registryClient = new InMemoryRemoteRuntimePairingRegistryClient(
            target,
            active: true);
        using var clientCertificate = registryClient.ClientCertificate;
        var gatewayOptions = new RemoteRuntimeBridgeOptions
        {
            Enabled = true,
            ListenUrl = $"https://127.0.0.1:{GetFreePort()}",
            ServerCertificatePath = certificatePath,
        };
        await using var gateway = await RemoteRuntimeBridgeHost.BuildAsync(
            [],
            gatewayOptions,
            registryClient,
            target);

        var instancePaths = new SharpClawInstancePaths(
            SharpClawInstanceKind.Backend,
            Path.Combine(root, "proxy"),
            Path.Combine(root, "shared"));
        var localUrl = $"http://127.0.0.1:{GetFreePort()}";
        RemoteRuntimeProxyConnection? connection = null;
        WebApplication? proxy = null;
        try
        {
            await gateway.StartAsync();
            connection = RemoteRuntimeProxyConnection.Create(
                instancePaths,
                localUrl,
                gatewayOptions.ListenUrl,
                RemoteRuntimeCertificateHash.Compute(serverCertificate),
                "proxy-1",
                clientCertificate,
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(60));
            proxy = RemoteProxyHost.Build([], connection);
            await proxy.StartAsync();

            using var directClient = new HttpClient
            {
                BaseAddress = new Uri(upstreamUrl),
                Timeout = TimeSpan.FromSeconds(60),
            };
            using var bridgeClient = new HttpClient
            {
                BaseAddress = new Uri(localUrl),
                Timeout = TimeSpan.FromSeconds(60),
            };
            bridgeClient.DefaultRequestHeaders.Add("X-Api-Key", connection.LocalApiKey);

            var direct = await MeasureAsync(directClient);
            var bridge = await MeasureAsync(bridgeClient);

            direct.Bytes.Should().Be(PayloadBytes);
            bridge.Bytes.Should().Be(PayloadBytes);
            bridge.PeakWorkingSetDeltaBytes.Should().BeLessThan(
                PayloadBytes / 2,
                "the transport must not retain the streamed payload in memory");
            bridge.ThroughputBytesPerSecond.Should().BeGreaterThan(100_000);

            TestContext.Progress.WriteLine(
                $"bridge payload={PayloadBytes} direct-first-byte-ms={direct.FirstByte.TotalMilliseconds:0.0} "
                + $"bridge-first-byte-ms={bridge.FirstByte.TotalMilliseconds:0.0} "
                + $"direct-throughput-bytes-per-second={direct.ThroughputBytesPerSecond:0} "
                + $"bridge-throughput-bytes-per-second={bridge.ThroughputBytesPerSecond:0} "
                + $"bridge-peak-working-set-delta-bytes={bridge.PeakWorkingSetDeltaBytes}");
        }
        finally
        {
            if (proxy is not null)
                await proxy.StopAsync();
            connection?.Dispose();
            await gateway.StopAsync();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<StreamMeasurement> MeasureAsync(HttpClient client)
    {
        var process = Process.GetCurrentProcess();
        var baseline = process.WorkingSet64;
        var peak = baseline;
        using var samplingCancellation = new CancellationTokenSource();
        var sampling = SamplePeakAsync(
            process,
            samplingCancellation.Token,
            () => Interlocked.Read(ref peak),
            value => Interlocked.Exchange(ref peak, value));
        var stopwatch = Stopwatch.StartNew();
        long bytes = 0;
        TimeSpan? firstByte = null;
        try
        {
            using var response = await client.GetAsync(
                $"/api/large-stream?bytes={PayloadBytes}",
                HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            var buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
            try
            {
                while (true)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, ChunkBytes));
                    if (read == 0)
                        break;

                    firstByte ??= stopwatch.Elapsed;
                    bytes += read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        finally
        {
            stopwatch.Stop();
            samplingCancellation.Cancel();
            await sampling;
        }

        var elapsedSeconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        return new StreamMeasurement(
            bytes,
            firstByte ?? stopwatch.Elapsed,
            bytes / elapsedSeconds,
            Math.Max(0, peak - baseline));
    }

    private static async Task SamplePeakAsync(
        Process process,
        CancellationToken cancellationToken,
        Func<long> readPeak,
        Action<long> writePeak)
    {
        try
        {
            while (true)
            {
                writePeak(Math.Max(readPeak(), process.WorkingSet64));
                await Task.Delay(5, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            writePeak(Math.Max(readPeak(), process.WorkingSet64));
        }
    }

    private static X509Certificate2 CreateServerCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=bridge-performance-test", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(10));
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed record StreamMeasurement(
        long Bytes,
        TimeSpan FirstByte,
        double ThroughputBytesPerSecond,
        long PeakWorkingSetDeltaBytes);

    private sealed class UpstreamHarness : IAsyncDisposable
    {
        private UpstreamHarness(WebApplication app)
        {
            App = app;
        }

        private WebApplication App { get; }

        public static async Task<UpstreamHarness> StartAsync(string url)
        {
            var builder = WebApplication.CreateSlimBuilder([]);
            builder.WebHost.UseUrls(url);
            var app = builder.Build();
            app.MapGet("/api/large-stream", async context =>
            {
                if (!int.TryParse(context.Request.Query["bytes"], out var bytes)
                    || bytes != PayloadBytes)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()
                    ?.DisableBuffering();
                context.Response.ContentType = "application/octet-stream";
                context.Response.ContentLength = bytes;
                var buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
                Array.Clear(buffer, 0, ChunkBytes);
                try
                {
                    var remaining = bytes;
                    while (remaining > 0)
                    {
                        var count = Math.Min(remaining, ChunkBytes);
                        await context.Response.Body.WriteAsync(
                            buffer.AsMemory(0, count),
                            context.RequestAborted);
                        await context.Response.Body.FlushAsync(context.RequestAborted);
                        remaining -= count;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                }
            });
            await app.StartAsync();
            return new UpstreamHarness(app);
        }

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }
}
