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
    public async Task ApiJobListSummariesDetailAndLifecycleActionsUseStableContracts()
    {
        await using var host = ChatHarnessHost.Create();
        host.Harness.ConfigurePermissionedJobTool(new TestHarnessToolBehavior { Result = "api-lifecycle" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.ToolProviderKey,
            grantHarnessPermission: true);
        await host.Chat.SendMessageAsync(
            seeded.Channel.Id,
            new ChatRequest("warm channel cost"));
        var svc = host.Services.GetRequiredService<AgentJobService>();

        var executing = await ExecuteResultAsync<AgentJobResponse>(
            await AgentJobHandlers.Submit(
                seeded.Channel.Id,
                new SubmitAgentJobRequest(
                    ActionKey: TestHarnessConstants.JobPermissionedTool,
                    ScriptJson: """{"result":"api-lifecycle","remainExecuting":true}"""),
                svc,
                host.Chat));
        var cancellable = await ExecuteResultAsync<AgentJobResponse>(
            await AgentJobHandlers.Submit(
                seeded.Channel.Id,
                new SubmitAgentJobRequest(
                    ActionKey: TestHarnessConstants.JobPermissionedTool,
                    ScriptJson: """{"result":"api-cancel","remainExecuting":true}"""),
                svc,
                host.Chat));

        var list = await ExecuteResultAsync<IReadOnlyList<AgentJobResponse>>(
            await AgentJobHandlers.List(seeded.Channel.Id, svc));
        var summaries = await ExecuteResultAsync<IReadOnlyList<AgentJobSummaryResponse>>(
            await AgentJobHandlers.ListSummaries(seeded.Channel.Id, svc));
        var detail = await ExecuteResultAsync<AgentJobResponse>(
            await AgentJobHandlers.GetById(seeded.Channel.Id, executing!.Id, svc, host.Chat));
        var paused = await ExecuteResultAsync<AgentJobResponse>(
            await AgentJobHandlers.Pause(seeded.Channel.Id, executing.Id, svc, host.Chat));
        var resumed = await ExecuteResultAsync<AgentJobResponse>(
            await AgentJobHandlers.Resume(seeded.Channel.Id, executing.Id, svc, host.Chat));
        var stopped = await ExecuteResultAsync<AgentJobResponse>(
            await AgentJobHandlers.Stop(seeded.Channel.Id, executing.Id, svc, host.Chat));
        var cancelled = await ExecuteResultAsync<AgentJobResponse>(
            await AgentJobHandlers.Cancel(seeded.Channel.Id, cancellable!.Id, svc, host.Chat));

        list!.Select(j => j.Id).Should().Contain([executing.Id, cancellable.Id]);
        summaries!.Select(j => j.Id).Should().Contain([executing.Id, cancellable.Id]);
        detail!.ChannelCost.Should().NotBeNull();
        detail.ChannelCost!.TotalTokens.Should().BeGreaterThan(0);
        paused!.Status.Should().Be(AgentJobStatus.Paused);
        paused.ChannelCost.Should().NotBeNull();
        resumed!.Status.Should().Be(AgentJobStatus.Executing);
        stopped!.Status.Should().Be(AgentJobStatus.Completed);
        cancelled!.Status.Should().Be(AgentJobStatus.Cancelled);
        cancelled.CompletedAt.Should().NotBeNull();
    }

    [Test]
    public async Task ApiJobApproveExecutesPendingJobAndAttachesChannelCost()
    {
        await using var host = ChatHarnessHost.Create();
        host.Harness.ConfigurePermissionedJobTool(new TestHarnessToolBehavior { Result = "approved-api" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.ToolProviderKey,
            grantHarnessPermission: true,
            clearance: PermissionClearance.ApprovedByPermittedAgent);
        await host.Chat.SendMessageAsync(
            seeded.Channel.Id,
            new ChatRequest("warm approve cost"));
        var session = host.Services.GetRequiredService<SessionService>();
        session.UserId = null;
        var svc = host.Services.GetRequiredService<AgentJobService>();

        var pending = await ExecuteResultAsync<AgentJobResponse>(
            await AgentJobHandlers.Submit(
                seeded.Channel.Id,
                new SubmitAgentJobRequest(
                    ActionKey: TestHarnessConstants.JobPermissionedTool,
                    ScriptJson: """{"result":"approved-api"}"""),
                svc,
                host.Chat));

        pending!.Status.Should().Be(AgentJobStatus.AwaitingApproval);

        var approved = await ExecuteResultAsync<AgentJobResponse>(
            await AgentJobHandlers.Approve(
                seeded.Channel.Id,
                pending.Id,
                new ApproveAgentJobRequest(ApproverAgentId: seeded.Agent.Id),
                svc,
                host.Chat));

        approved!.Status.Should().Be(AgentJobStatus.Completed);
        approved.ResultData.Should().Be("approved-api");
        approved.ChannelCost.Should().NotBeNull();
        approved.ChannelCost!.TotalTokens.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task ApiJobRoutesDoNotLeakOrMutateJobsFromAnotherChannel()
    {
        await using var host = ChatHarnessHost.Create();
        host.Harness.ConfigurePermissionedJobTool(new TestHarnessToolBehavior { Result = "wrong-channel" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.ToolProviderKey,
            grantHarnessPermission: true);
        var otherChannel = new SharpClaw.Contracts.Entities.Core.Context.ChannelDB
        {
            Id = Guid.NewGuid(),
            Title = "Other Channel",
            AgentId = seeded.Agent.Id,
            Agent = seeded.Agent,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        host.Db.Channels.Add(otherChannel);
        await host.Db.SaveChangesAsync();
        var svc = host.Services.GetRequiredService<AgentJobService>();
        var job = await ExecuteResultAsync<AgentJobResponse>(
            await AgentJobHandlers.Submit(
                seeded.Channel.Id,
                new SubmitAgentJobRequest(
                    ActionKey: TestHarnessConstants.JobPermissionedTool,
                    ScriptJson: """{"result":"wrong-channel","remainExecuting":true}"""),
                svc,
                host.Chat));

        var detail = await ExecuteRawResultAsync(
            await AgentJobHandlers.GetById(otherChannel.Id, job!.Id, svc, host.Chat));
        var pause = await ExecuteRawResultAsync(
            await AgentJobHandlers.Pause(otherChannel.Id, job.Id, svc, host.Chat));
        var after = await svc.GetAsync(job.Id);

        detail.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        pause.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        after!.Status.Should().Be(AgentJobStatus.Executing);
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

    [Test]
    public async Task GatewayJobListAndSummariesForwardStablePaths()
    {
        var channelId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        using var listResponse = JsonResponse(new[]
        {
            BuildJobResponse(channelId, jobId, AgentJobStatus.Completed)
        });
        var listController = CreateGatewayJobController(listResponse, out var listHandler);

        var listResult = await listController.List(channelId, default);

        listResult.Should().BeAssignableTo<OkObjectResult>().Subject
            .Value.Should().BeAssignableTo<IReadOnlyList<AgentJobResponse>>().Subject
            .Should().ContainSingle(j => j.Id == jobId);
        listHandler.Requests.Should().ContainSingle()
            .Which.Should().Match<CapturedGatewayRequest>(r =>
                r.Method == HttpMethod.Get
                && r.PathAndQuery == $"/channels/{channelId}/jobs");

        using var summariesResponse = JsonResponse(new[]
        {
            new AgentJobSummaryResponse(
                jobId,
                channelId,
                Guid.NewGuid(),
                TestHarnessConstants.JobPermissionedTool,
                null,
                AgentJobStatus.Completed,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow)
        });
        var summariesController = CreateGatewayJobController(summariesResponse, out var summariesHandler);

        var summariesResult = await summariesController.ListSummaries(channelId, default);

        summariesResult.Should().BeAssignableTo<OkObjectResult>().Subject
            .Value.Should().BeAssignableTo<IReadOnlyList<AgentJobSummaryResponse>>().Subject
            .Should().ContainSingle(j => j.Id == jobId);
        summariesHandler.Requests.Should().ContainSingle()
            .Which.Should().Match<CapturedGatewayRequest>(r =>
                r.Method == HttpMethod.Get
                && r.PathAndQuery == $"/channels/{channelId}/jobs/summaries");
    }

    [TestCase("detail", "GET", "")]
    [TestCase("approve", "POST", "/approve")]
    [TestCase("stop", "POST", "/stop")]
    [TestCase("cancel", "POST", "/cancel")]
    [TestCase("pause", "PUT", "/pause")]
    [TestCase("resume", "PUT", "/resume")]
    public async Task GatewayJobLifecycleActionsForwardStableMethodsAndPaths(
        string action,
        string expectedMethod,
        string suffix)
    {
        var channelId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        using var response = JsonResponse(BuildJobResponse(channelId, jobId, AgentJobStatus.Completed));
        var controller = CreateGatewayJobController(response, out var handler);

        var result = action switch
        {
            "detail" => await controller.GetById(channelId, jobId, default),
            "approve" => await controller.Approve(channelId, jobId, new ApproveAgentJobRequest(), default),
            "stop" => await controller.Stop(channelId, jobId, default),
            "cancel" => await controller.Cancel(channelId, jobId, default),
            "pause" => await controller.Pause(channelId, jobId, default),
            "resume" => await controller.Resume(channelId, jobId, default),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };

        result.Should().BeAssignableTo<OkObjectResult>().Subject
            .Value.Should().BeAssignableTo<AgentJobResponse>().Subject
            .Id.Should().Be(jobId);
        handler.Requests.Should().ContainSingle()
            .Which.Should().Match<CapturedGatewayRequest>(r =>
                r.Method.Method == expectedMethod
                && r.PathAndQuery == $"/channels/{channelId}/jobs/{jobId}{suffix}");
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

    [TestCase("approve")]
    [TestCase("stop")]
    [TestCase("cancel")]
    [TestCase("pause")]
    [TestCase("resume")]
    public async Task GatewayJobMutationNotFoundEnvelopeShapeIsStable(string action)
    {
        var controller = CreateGatewayJobController(new HttpResponseMessage(HttpStatusCode.NotFound));
        var channelId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        var result = action switch
        {
            "approve" => await controller.Approve(channelId, jobId, new ApproveAgentJobRequest(), default),
            "stop" => await controller.Stop(channelId, jobId, default),
            "cancel" => await controller.Cancel(channelId, jobId, default),
            "pause" => await controller.Pause(channelId, jobId, default),
            "resume" => await controller.Resume(channelId, jobId, default),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };

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

    private static async Task<(int StatusCode, string Body)> ExecuteRawResultAsync(IResult result)
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
        return (context.Response.StatusCode, await new StreamReader(body).ReadToEndAsync());
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

    private static AgentJobsController CreateGatewayJobController(
        HttpResponseMessage response,
        out RecordingResponseHandler handler)
    {
        handler = new RecordingResponseHandler(response);
        return new AgentJobsController(CreateInternalApiClient(handler));
    }

    private static InternalApiClient CreateInternalApiClient(HttpResponseMessage response)
        => CreateInternalApiClient(new RecordingResponseHandler(response));

    private static InternalApiClient CreateInternalApiClient(HttpMessageHandler handler)
    {
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

    private static HttpResponseMessage JsonResponse<T>(T value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json")
        };

    private static AgentJobResponse BuildJobResponse(
        Guid channelId,
        Guid jobId,
        AgentJobStatus status) =>
        new(
            jobId,
            channelId,
            Guid.NewGuid(),
            TestHarnessConstants.JobPermissionedTool,
            null,
            status,
            PermissionClearance.Independent,
            "gateway-job",
            null,
            [new AgentJobLogResponse("done", "Info", DateTimeOffset.UtcNow)],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            JobCost: new TokenUsageResponse(1, 2, 3),
            ChannelCost: new ChannelCostResponse(channelId, 1, 2, 3, []));

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

    private sealed class RecordingResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        private readonly List<CapturedGatewayRequest> _requests = [];

        public IReadOnlyList<CapturedGatewayRequest> Requests => _requests;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            _requests.Add(new CapturedGatewayRequest(
                request.Method,
                request.RequestUri!.PathAndQuery,
                body));
            return response;
        }
    }

    private sealed record CapturedGatewayRequest(
        HttpMethod Method,
        string PathAndQuery,
        string? Body);
}
