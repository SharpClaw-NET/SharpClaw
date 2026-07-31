using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Runtime.Host.Cli;
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
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
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
            using var client = new ClientWebSocket();
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

            await client.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "done",
                TestContext.CurrentContext.CancellationToken);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
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
