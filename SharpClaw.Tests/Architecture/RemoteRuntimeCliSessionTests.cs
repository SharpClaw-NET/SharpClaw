using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Runtime.Host.Api;
using SharpClaw.Runtime.Host.Cli;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.RemoteRuntimeBridge;

namespace SharpClaw.Tests.Architecture;

[TestFixture]
[NonParallelizable]
public sealed class RemoteRuntimeCliSessionTests
{
    [Test]
    public async Task WebSocket_session_rejects_unsupported_frames_without_command_services()
    {
        var port = GetFreePort();
        var instanceRoot = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "remote-cli-session-" + Guid.NewGuid().ToString("N"));
        var instancePaths = new SharpClawInstancePaths(
            SharpClawInstanceKind.Backend,
            instanceRoot);
        instancePaths.EnsureDirectories();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddSingleton(instancePaths);
        builder.Services.AddSingleton<ApiKeyProvider>();
        builder.Services.AddScoped<SessionService>();
        var app = builder.Build();
        var apiKeys = app.Services.GetRequiredService<ApiKeyProvider>();
        app.UseMiddleware<ApiKeyMiddleware>();
        app.UseMiddleware<JwtSessionMiddleware>();
        app.UseWebSockets();
        app.Map(
            RemoteRuntimeBridgePaths.CliControl,
            async context =>
            {
                var socket = await context.WebSockets.AcceptWebSocketAsync();
                await RemoteRuntimeCliSession.RunAsync(
                    socket,
                    context.RequestServices,
                    context.RequestAborted);
            });

            await app.StartAsync();
        try
        {
            using var httpClient = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            };
            using var unauthorized = await httpClient.GetAsync(
                RemoteRuntimeBridgePaths.CliControl,
                TestContext.CurrentContext.CancellationToken);
            unauthorized.StatusCode.Should().Be(HttpStatusCode.Locked);

            using var client = new ClientWebSocket();
            client.Options.SetRequestHeader("X-Api-Key", apiKeys.ApiKey);
            client.Options.SetRequestHeader("X-Gateway-Token", apiKeys.GatewayToken);
            await client.ConnectAsync(
                new Uri($"ws://127.0.0.1:{port}{RemoteRuntimeBridgePaths.CliControl}"),
                TestContext.CurrentContext.CancellationToken);

            var invalidFrame = JsonSerializer.SerializeToUtf8Bytes(
                new RemoteRuntimeCliFrame("unsupported"));
            await client.SendAsync(
                invalidFrame,
                WebSocketMessageType.Text,
                endOfMessage: true,
                TestContext.CurrentContext.CancellationToken);

            var response = await ReceiveFrameAsync(
                client,
                TestContext.CurrentContext.CancellationToken);
            response.Type.Should().Be(RemoteRuntimeCliFrameTypes.Error);
            response.Text.Should().Be("The CLI frame type is not supported.");

            var commandFrame = JsonSerializer.SerializeToUtf8Bytes(
                new RemoteRuntimeCliFrame(
                    RemoteRuntimeCliFrameTypes.Command,
                    "help"));
            await client.SendAsync(
                commandFrame,
                WebSocketMessageType.Text,
                endOfMessage: true,
                TestContext.CurrentContext.CancellationToken);

            var commandOutput = new List<RemoteRuntimeCliFrame>();
            RemoteRuntimeCliFrame commandResult;
            do
            {
                var frame = await ReceiveFrameAsync(
                    client,
                    TestContext.CurrentContext.CancellationToken);
                commandOutput.Add(frame);
                commandResult = frame;
            }
            while (!commandResult.Type.Equals(
                RemoteRuntimeCliFrameTypes.Result,
                StringComparison.OrdinalIgnoreCase));

            commandResult.Handled.Should().BeTrue();
            commandOutput
                .Where(frame => frame.Type == RemoteRuntimeCliFrameTypes.Output)
                .Select(frame => frame.Text)
                .Should()
                .Contain(text => text!.Contains("SharpClaw - Shell Agent", StringComparison.Ordinal));

            await client.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "done",
                TestContext.CurrentContext.CancellationToken);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            apiKeys.Cleanup();
            if (Directory.Exists(instanceRoot))
                Directory.Delete(instanceRoot, recursive: true);
        }
    }

    private static async Task<RemoteRuntimeCliFrame> ReceiveFrameAsync(
        ClientWebSocket client,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];
        using var message = new MemoryStream();
        ValueWebSocketReceiveResult result;
        do
        {
            result = await client.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            await message.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
        }
        while (!result.EndOfMessage);

        return JsonSerializer.Deserialize<RemoteRuntimeCliFrame>(
            message.GetBuffer().AsSpan(0, checked((int)message.Length)))!;
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
