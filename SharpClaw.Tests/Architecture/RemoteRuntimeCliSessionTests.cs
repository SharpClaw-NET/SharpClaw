using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Runtime.Host.Api;
using SharpClaw.Runtime.Host.Cli;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.RemoteRuntimeBridge;
using Supprocom.Secrets;

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
        var environmentDirectory = Path.Combine(instanceRoot, "Environment");
        Directory.CreateDirectory(environmentDirectory);
        var secretOptions = new SupprocomSecretsOptions
        {
            EnvironmentName = "Production",
            FileOverridesProcessEnvironment = true,
            File =
            {
                Directory = environmentDirectory,
                ActiveName = ".env",
                DevelopmentName = ".dev.env",
                TemplateName = ".env.template",
                DevelopmentTemplateName = ".dev.env.template",
                Import = SecretFileImport.JsonWithCommentsOnce,
                DevelopmentComposition = SecretFileComposition.Overlay,
                Recovery = SecretFileRecovery.QuarantineAndRestoreTemplate,
                Protection = SecretFileProtection.InstallationBoundAesGcm,
                InstallationKeyPath = instancePaths.GetSecretFilePath("encryption-key")
            }
        };
        var secretStore = new SupprocomSecretFileStore(secretOptions);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddSingleton(instancePaths);
        builder.Services.AddSingleton<ApiKeyProvider>();
        builder.Services.AddDbContext<SharpClawDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
        builder.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EnvEditor:AllowNonAdmin"] = "true"
            })
            .Build());
        builder.Services.AddScoped<SessionService>();
        builder.Services.AddSingleton<ISecretDocumentStore>(secretStore);
        builder.Services.AddSingleton<ISecretFileProtectionManager>(secretStore);
        builder.Services.AddScoped(_ => new EnvFileService(
            _.GetRequiredService<SharpClawDbContext>(),
            _.GetRequiredService<SessionService>(),
            _.GetRequiredService<IConfiguration>(),
            secretStore));
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
        var baseUri = new Uri(app.Urls.First());
        var websocketUri = new UriBuilder(baseUri)
        {
            Scheme = Uri.UriSchemeWs,
            Path = RemoteRuntimeBridgePaths.CliControl
        }.Uri;
        try
        {
            using var httpClient = new HttpClient
            {
                BaseAddress = baseUri,
            };
            using var unauthorized = await httpClient.GetAsync(
                RemoteRuntimeBridgePaths.CliControl,
                TestContext.CurrentContext.CancellationToken);
            unauthorized.StatusCode.Should().Be(HttpStatusCode.Locked);

            using var client = new ClientWebSocket();
            client.Options.SetRequestHeader("X-Api-Key", apiKeys.ApiKey);
            client.Options.SetRequestHeader("X-Gateway-Token", apiKeys.GatewayToken);
            await client.ConnectAsync(
                websocketUri,
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
                RemoteRuntimeCliFrame.CommandFrame(["help"]));
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
                RemoteRuntimeCliFrameTypes.Exit,
                StringComparison.OrdinalIgnoreCase));

            commandResult.Handled.Should().BeTrue();
            commandOutput
                .Where(frame => frame.Type == RemoteRuntimeCliFrameTypes.Output)
                .Select(frame => frame.Text)
                .Should()
                .Contain(text => text!.Contains("SharpClaw - Shell Agent", StringComparison.Ordinal));

            await SendFrameAsync(
                client,
                RemoteRuntimeCliFrame.CommandFrame(["env", "set"]),
                TestContext.CurrentContext.CancellationToken);
            var firstPrompt = await ReceiveUntilAsync(
                client,
                RemoteRuntimeCliFrameTypes.Prompt,
                TestContext.CurrentContext.CancellationToken);
            firstPrompt.Text.Should().Contain("Paste .env dotenv content");
            await SendFrameAsync(
                client,
                RemoteRuntimeCliFrame.InputFrame(firstPrompt.PromptId!, "Api__Url=http://127.0.0.1:48924"),
                TestContext.CurrentContext.CancellationToken);
            var secondPrompt = await ReceiveUntilAsync(
                client,
                RemoteRuntimeCliFrameTypes.Prompt,
                TestContext.CurrentContext.CancellationToken);
            secondPrompt.Text.Should().BeEmpty();
            await SendFrameAsync(
                client,
                RemoteRuntimeCliFrame.InputFrame(secondPrompt.PromptId!, string.Empty),
                TestContext.CurrentContext.CancellationToken);
            var completedEnvSet = await ReceiveUntilAsync(
                client,
                RemoteRuntimeCliFrameTypes.Exit,
                TestContext.CurrentContext.CancellationToken);
            completedEnvSet.ExitCode.Should().Be(0);

            await SendFrameAsync(
                client,
                RemoteRuntimeCliFrame.CommandFrame(["env", "set"]),
                TestContext.CurrentContext.CancellationToken);
            _ = await ReceiveUntilAsync(
                client,
                RemoteRuntimeCliFrameTypes.Prompt,
                TestContext.CurrentContext.CancellationToken);
            await SendFrameAsync(
                client,
                RemoteRuntimeCliFrame.CancelFrame(),
                TestContext.CurrentContext.CancellationToken);
            var cancelledEnvSet = await ReceiveUntilAsync(
                client,
                RemoteRuntimeCliFrameTypes.Exit,
                TestContext.CurrentContext.CancellationToken,
                allowErrors: true);
            cancelledEnvSet.ExitCode.Should().Be(130);

            await client.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "done",
                TestContext.CurrentContext.CancellationToken);

            await using var typedClient = await RemoteRuntimeCliClient.ConnectAsync(
                websocketUri,
                options =>
                {
                    options.SetRequestHeader("X-Api-Key", apiKeys.ApiKey);
                    options.SetRequestHeader("X-Gateway-Token", apiKeys.GatewayToken);
                },
                TestContext.CurrentContext.CancellationToken);
            var typedOutput = new List<string>();
            var typedStatus = await typedClient.RunAsync(
                ["help"],
                output: typedOutput.Add,
                cancellationToken: TestContext.CurrentContext.CancellationToken);
            typedStatus.ExitCode.Should().Be(0);
            typedStatus.Handled.Should().BeTrue();
            typedOutput.Should().Contain(text => text.Contains("SharpClaw - Shell Agent", StringComparison.Ordinal));

            var typedPrompts = new List<string>();
            var typedEnvStatus = await typedClient.RunAsync(
                ["env", "set"],
                prompt: prompt =>
                {
                    typedPrompts.Add(prompt.Text);
                    return ValueTask.FromResult<string?>(
                        typedPrompts.Count == 1 ? "Api__Typed=http://127.0.0.1:48925" : string.Empty);
                },
                cancellationToken: TestContext.CurrentContext.CancellationToken);
            typedEnvStatus.ExitCode.Should().Be(0);
            typedEnvStatus.Handled.Should().BeTrue();
            typedPrompts.Should().HaveCount(2);
            typedPrompts[0].Should().Contain("Paste .env dotenv content");
            typedPrompts[1].Should().BeEmpty();
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

    private static async Task SendFrameAsync(
        ClientWebSocket client,
        RemoteRuntimeCliFrame frame,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame);
        await client.SendAsync(
            bytes,
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    private static async Task<RemoteRuntimeCliFrame> ReceiveUntilAsync(
        ClientWebSocket client,
        string frameType,
        CancellationToken cancellationToken,
        bool allowErrors = false)
    {
        while (true)
        {
            RemoteRuntimeCliFrame frame;
            using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            receiveTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                frame = await ReceiveFrameAsync(client, receiveTimeout.Token);
            }
            catch (OperationCanceledException exception) when (
                receiveTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for CLI frame '{frameType}'.", exception);
            }

            if (frame.Type.Equals(RemoteRuntimeCliFrameTypes.Error, StringComparison.OrdinalIgnoreCase)
                && !allowErrors)
                throw new InvalidOperationException(frame.Text ?? "The CLI session returned an error.");
            if (frame.Type.Equals(RemoteRuntimeCliFrameTypes.Exit, StringComparison.OrdinalIgnoreCase)
                && !frameType.Equals(RemoteRuntimeCliFrameTypes.Exit, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The CLI session exited with status {frame.ExitCode} before '{frameType}'.");
            }
            if (frame.Type.Equals(frameType, StringComparison.OrdinalIgnoreCase))
                return frame;
        }
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
