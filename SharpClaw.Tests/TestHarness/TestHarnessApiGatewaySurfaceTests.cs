using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SharpClaw.Application.API.Handlers;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Providers;
using SharpClaw.Gateway.Controllers;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Modules.TestHarness;
using SharpClaw.Utils.Logging;

namespace SharpClaw.Tests.TestHarness;

[TestFixture]
public sealed class TestHarnessApiGatewaySurfaceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    [Test]
    public async Task SameSpoofResponseTextMatchesCoreApiSseAndGatewaySurfaces()
    {
        const string expected = "same final text";
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.StreamingProviderKey);
        ConfigureSingleTurn(host, expected, ["same ", "final ", "text"]);

        var core = await host.Chat.SendMessageAsync(
            seeded.Channel.Id,
            new ChatRequest("core"));

        host.Harness.Reset();
        seeded = await host.SeedChatAsync(TestHarnessConstants.StreamingProviderKey);
        ConfigureSingleTurn(host, expected, ["same ", "final ", "text"]);
        var api = await ExecuteResultAsync<ChatResponse>(
            await ChatHandlers.Send(
                seeded.Channel.Id,
                new ChatRequest("api"),
                host.Chat));

        host.Harness.Reset();
        seeded = await host.SeedChatAsync(TestHarnessConstants.StreamingProviderKey);
        ConfigureSingleTurn(host, expected, ["same ", "final ", "text"]);
        var sse = await ExecuteSseAsync(host, seeded.Channel.Id, "sse");

        var gateway = await ExecuteGatewayChatAsync(
            seeded.Channel.Id,
            new ChatRequest("gateway"),
            api!);

        core.AssistantMessage.Content.Should().Be(expected);
        api!.AssistantMessage.Content.Should().Be(expected);
        sse.FinalResponse!.AssistantMessage.Content.Should().Be(expected);
        gateway.AssistantMessage.Content.Should().Be(expected);
    }

    [TestCase(HttpStatusCode.BadRequest, 400, "Invalid chat request.")]
    [TestCase(HttpStatusCode.NotFound, 404, "Channel not found.")]
    [TestCase(HttpStatusCode.ServiceUnavailable, 502, "Internal service unavailable.")]
    public async Task GatewayErrorEnvelopeShapeIsStable(
        HttpStatusCode internalStatus,
        int expectedGatewayStatus,
        string expectedError)
    {
        var controller = CreateGatewayChatController(new HttpResponseMessage(internalStatus));

        var result = await controller.Send(Guid.NewGuid(), new ChatRequest("error"), default);

        var objectResult = result.Should().BeAssignableTo<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(expectedGatewayStatus);
        JsonSerializer.Serialize(objectResult.Value).Should().Contain(expectedError);
    }

    [Test]
    public async Task MidStreamFailureErrorEnvelopeIsStableInSse()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.StreamingProviderKey);
        host.Harness.ConfigureProvider(
            TestHarnessConstants.StreamingProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        Content = null,
                        StreamingChunks = ["ok"],
                        StreamFailureAfterChunks = 1
                    }
                ]
            });

        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;

        await ChatStreamHandlers.StreamChat(
            context,
            seeded.Channel.Id,
            new ChatRequest("fail"),
            host.Chat,
            NullLoggerFactory.Instance);

        body.Position = 0;
        var sse = await new StreamReader(body).ReadToEndAsync();
        sse.Should().Contain("event: Error");
        sse.Should().Contain("\"type\":\"Error\"");
        sse.Should().Contain("test harness configured mid-stream failure");
    }

    [Test]
    public async Task GatewaySseProxy_ForwardsRealHttpSsePath()
    {
        var channelId = Guid.NewGuid();
        await using var internalApi = await StartInternalSseServerAsync(channelId);
        await using var gateway = await StartGatewayProxyAsync(internalApi.Urls.Single());

        using var client = new HttpClient
        {
            BaseAddress = new Uri(gateway.Urls.Single())
        };

        var sse = await client.GetStringAsync($"/api/channels/{channelId}/chat/stream?message=via-gateway");

        sse.Should().Contain("event: TextDelta");
        sse.Should().Contain("gateway-real-before");
        sse.Should().Contain("event: Done");
        sse.Should().Contain("gateway-real-after");
    }

    [Test]
    public async Task DeterministicTimeoutCancellationDoesNotLeaveExecutingJobsStuck()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.StreamingProviderKey);
        host.Harness.ConfigureProvider(
            TestHarnessConstants.StreamingProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        Content = null,
                        StreamingChunks = ["slow", "end"],
                        PerChunkDelayMs = 1_000
                    }
                ]
            });

        using var cts = new CancellationTokenSource(100);
        var act = async () =>
        {
            await foreach (var _ in host.Chat.SendMessageStreamAsync(
                seeded.Channel.Id,
                new ChatRequest("timeout"),
                (_, _) => Task.FromResult(true),
                ct: cts.Token))
            {
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        host.Db.AgentJobs.Any(j => j.Status == SharpClaw.Contracts.Enums.AgentJobStatus.Executing)
            .Should().BeFalse();
    }

    [Test]
    public async Task ApiJobSubmitAttachesChannelCostToJobResponse()
    {
        await using var host = ChatHarnessHost.Create();
        host.Harness.ConfigurePermissionedJobTool(new TestHarnessToolBehavior { Result = "api-job-ok" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.ToolProviderKey,
            grantHarnessPermission: true);
        await host.Chat.SendMessageAsync(
            seeded.Channel.Id,
            new ChatRequest("warm channel cost"));

        var job = await ExecuteResultAsync<AgentJobResponse>(
            await AgentJobHandlers.Submit(
                seeded.Channel.Id,
                new SubmitAgentJobRequest(
                    ActionKey: TestHarnessConstants.JobPermissionedTool,
                    ScriptJson: """{"result":"api-job-ok"}"""),
                host.Services.GetRequiredService<AgentJobService>(),
                host.Chat));

        job!.Status.Should().Be(AgentJobStatus.Completed);
        job.ResultData.Should().Be("api-job-ok");
        job.ChannelCost.Should().NotBeNull();
        job.ChannelCost!.TotalTokens.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task GatewayJobSubmitForwardsStableJobResponse()
    {
        var channelId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var response = new AgentJobResponse(
            Guid.NewGuid(),
            channelId,
            agentId,
            TestHarnessConstants.JobPermissionedTool,
            null,
            AgentJobStatus.Completed,
            PermissionClearance.Independent,
            "gateway-job-ok",
            null,
            [new AgentJobLogResponse("done", "Info", DateTimeOffset.UtcNow)],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            JobCost: new TokenUsageResponse(1, 2, 3),
            ChannelCost: new ChannelCostResponse(channelId, 1, 2, 3, []));
        using var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(response, JsonOptions), Encoding.UTF8, "application/json")
        };
        var controller = CreateGatewayJobController(httpResponse);

        var result = await controller.Submit(
            channelId,
            new SubmitAgentJobRequest(ActionKey: TestHarnessConstants.JobPermissionedTool),
            default);

        var returned = result.Should().BeAssignableTo<OkObjectResult>().Subject
            .Value.Should().BeAssignableTo<AgentJobResponse>().Subject;
        returned.Status.Should().Be(AgentJobStatus.Completed);
        returned.JobCost!.TotalTokens.Should().Be(3);
        returned.ChannelCost!.TotalTokens.Should().Be(3);
    }

    [TestCase(HttpStatusCode.BadRequest, 400, "Invalid job request.")]
    [TestCase(HttpStatusCode.ServiceUnavailable, 502, "Internal service unavailable.")]
    public async Task GatewayJobSubmitErrorEnvelopeShapeIsStable(
        HttpStatusCode internalStatus,
        int expectedGatewayStatus,
        string expectedError)
    {
        var controller = CreateGatewayJobController(new HttpResponseMessage(internalStatus));

        var result = await controller.Submit(
            Guid.NewGuid(),
            new SubmitAgentJobRequest(ActionKey: TestHarnessConstants.JobPermissionedTool),
            default);

        var objectResult = result.Should().BeAssignableTo<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(expectedGatewayStatus);
        JsonSerializer.Serialize(objectResult.Value).Should().Contain(expectedError);
    }

    [Test]
    public async Task GatewayJobDetailNotFoundEnvelopeShapeIsStable()
    {
        var controller = CreateGatewayJobController(new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await controller.GetById(Guid.NewGuid(), Guid.NewGuid(), default);

        var objectResult = result.Should().BeAssignableTo<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(404);
        JsonSerializer.Serialize(objectResult.Value).Should().Contain("Job not found.");
    }

    private static void ConfigureSingleTurn(
        ChatHarnessHost host,
        string content,
        IReadOnlyList<string> chunks)
    {
        host.Harness.ConfigureProvider(
            TestHarnessConstants.StreamingProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        Content = content,
                        StreamingChunks = chunks,
                        Usage = new TokenUsage(5, 7)
                    }
                ]
            });
    }

    private static async Task<ChatStreamEvent> ExecuteSseAsync(
        ChatHarnessHost host,
        Guid channelId,
        string message)
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;

        await ChatStreamHandlers.StreamChat(
            context,
            channelId,
            new ChatRequest(message),
            host.Chat,
            NullLoggerFactory.Instance);

        body.Position = 0;
        var text = await new StreamReader(body).ReadToEndAsync();
        return text.Split('\n')
            .Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
            .Select(line => JsonSerializer.Deserialize<ChatStreamEvent>(line["data: ".Length..], JsonOptions))
            .OfType<ChatStreamEvent>()
            .Single(e => e.Type == ChatStreamEventType.Done);
    }

    private static async Task<T?> ExecuteResultAsync<T>(IResult result)
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            })
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        await using var body = new MemoryStream();
        context.Response.Body = body;

        await result.ExecuteAsync(context);

        body.Position = 0;
        var json = await new StreamReader(body).ReadToEndAsync();
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private static async Task<ChatResponse> ExecuteGatewayChatAsync(
        Guid channelId,
        ChatRequest request,
        ChatResponse response)
    {
        var payload = JsonSerializer.Serialize(response, JsonOptions);
        using var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        var controller = CreateGatewayChatController(httpResponse);

        var result = await controller.Send(channelId, request, default);

        return result.Should().BeAssignableTo<OkObjectResult>().Subject
            .Value.Should().BeAssignableTo<ChatResponse>().Subject;
    }

    private static ChatController CreateGatewayChatController(HttpResponseMessage response) =>
        new(CreateInternalApiClient(response));

    private static AgentJobsController CreateGatewayJobController(HttpResponseMessage response) =>
        new(CreateInternalApiClient(response));

    private static InternalApiClient CreateInternalApiClient(HttpResponseMessage response)
    {
        var handler = new FixedResponseHandler(response);
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://internal.test")
        };
        var logsRoot = Path.Combine(Path.GetTempPath(), "SharpClawHarnessGateway", Guid.NewGuid().ToString("N"));
        var writer = new SessionLogWriter("gateway-test", logsRoot, TimeSpan.FromMinutes(10));
        var api = new InternalApiClient(
            client,
            Options.Create(new InternalApiOptions { ApiKey = "test-key", GatewayToken = "gateway-token" }),
            new HttpContextAccessor(),
            writer);
        return api;
    }

    private static async Task<WebApplication> StartInternalSseServerAsync(Guid expectedChannelId)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{GetFreeTcpPort()}");
        var app = builder.Build();
        app.MapGet("/channels/{channelId:guid}/chat/stream", async (Guid channelId, HttpContext context) =>
        {
            channelId.Should().Be(expectedChannelId);
            context.Request.Query["message"].ToString().Should().Be("via-gateway");
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("event: TextDelta\n");
            await context.Response.WriteAsync("""data: {"type":"TextDelta","delta":"gateway-real-before"}""" + "\n\n");
            await context.Response.Body.FlushAsync();
            await context.Response.WriteAsync("event: Done\n");
            await context.Response.WriteAsync("""data: {"type":"Done","finalResponse":{"assistantMessage":{"role":"assistant","content":"gateway-real-after","createdAt":"2026-01-01T00:00:00Z"}}}""" + "\n\n");
        });
        await app.StartAsync();
        return app;
    }

    private static async Task<WebApplication> StartGatewayProxyAsync(string internalBaseUrl)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{GetFreeTcpPort()}");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InternalApi:BaseUrl"] = internalBaseUrl
        });
        var app = builder.Build();
        app.MapChatStreamProxy();
        await app.StartAsync();
        return app;
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

    private sealed class FixedResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }
}
