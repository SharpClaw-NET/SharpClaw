using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Chat;
using SharpClaw.Core.Modules;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ChatNativeToolCallParserTests
{
    [Test]
    public void BuildParsePlan_WhenToolResolves_BuildsEnvelopeAndExtractsResourceIdAlias()
    {
        var resourceId = Guid.NewGuid();
        var parser = new ChatNativeToolCallParser();
        var plan = parser.BuildParsePlan(
            new ChatToolCall(
                "call-1",
                "run",
                $$"""{"resource_id":"{{resourceId}}","value":5}"""),
            CreateRegistry(),
            new RegistrationToolExecutionPlanner());

        plan.Should().NotBeNull();
        plan!.CallId.Should().Be("call-1");
        plan.ActionKey.Should().Be("run");
        plan.SourceId.Should().Be("test_registration");
        plan.ToolName.Should().Be("run");
        plan.DirectResourceId.Should().Be(resourceId);
        plan.RequiresResourceExtractor.Should().BeFalse();

        using var envelope = JsonDocument.Parse(plan.ScriptJson);
        envelope.RootElement.GetProperty("module").GetString().Should().Be("test_registration");
        envelope.RootElement.GetProperty("tool").GetString().Should().Be("run");
        envelope.RootElement.GetProperty("params").GetProperty("value").GetInt32().Should().Be(5);
    }

    [Test]
    public void BuildParsePlan_WhenNoDirectResourceId_RequestsExtractor()
    {
        var parser = new ChatNativeToolCallParser();
        var plan = parser.BuildParsePlan(
            new ChatToolCall("call-1", "run", """{"name":"alpha"}"""),
            CreateRegistry(),
            new RegistrationToolExecutionPlanner());

        plan.Should().NotBeNull();
        plan!.DirectResourceId.Should().BeNull();
        plan.RequiresResourceExtractor.Should().BeTrue();
        plan.ArgumentsJson.Should().Be("""{"name":"alpha"}""");
    }

    [Test]
    public void BuildParsePlan_WhenResourceIdIsNotString_RequestsExtractor()
    {
        var parser = new ChatNativeToolCallParser();
        var plan = parser.BuildParsePlan(
            new ChatToolCall("call-1", "run", """{"resourceId":5}"""),
            CreateRegistry(),
            new RegistrationToolExecutionPlanner());

        plan.Should().NotBeNull();
        plan!.DirectResourceId.Should().BeNull();
        plan.RequiresResourceExtractor.Should().BeTrue();
    }

    [Test]
    public void BuildParsePlan_WhenToolDoesNotResolve_ReturnsNull()
    {
        var parser = new ChatNativeToolCallParser();

        var plan = parser.BuildParsePlan(
            new ChatToolCall("call-1", "missing", "{}"),
            CreateRegistry(),
            new RegistrationToolExecutionPlanner());

        plan.Should().BeNull();
        ChatNativeToolCallParser.MalformedToolCallResult.Should().Be(
            "Error: unrecognized tool or malformed arguments.");
    }

    [Test]
    public void CompleteParse_UsesExtractorResourceIdOnlyWhenDirectIdIsMissing()
    {
        var directId = Guid.NewGuid();
        var extractedId = Guid.NewGuid();
        var parser = new ChatNativeToolCallParser();

        var direct = parser.CompleteParse(
            new ChatNativeToolCallParsePlan(
                "call-1",
                "run",
                "{}",
                "test_registration",
                "run",
                """{"module":"test_registration","tool":"run","params":{}}""",
                directId,
                RequiresResourceExtractor: false),
            extractedId);

        direct.ResourceId.Should().Be(directId);

        var extracted = parser.CompleteParse(
            new ChatNativeToolCallParsePlan(
                "call-2",
                "run",
                "{}",
                "test_registration",
                "run",
                """{"module":"test_registration","tool":"run","params":{}}""",
                null,
                RequiresResourceExtractor: true),
            extractedId);

        extracted.ResourceId.Should().Be(extractedId);
    }

    [Test]
    public async Task ResolveAsync_WhenExtractorIsNeeded_UsesHostExtractionDelegate()
    {
        var extractedId = Guid.NewGuid();
        var parser = new ChatNativeToolCallParser();
        var registry = CreateRegistry();
        registry.RegisterResourceIdExtractor(
            "run",
            (services, argumentsJson, ct) =>
            {
                services.GetRequiredService<ExtractorService>()
                    .Touched = true;
                argumentsJson.Should().Be("""{"name":"alpha"}""");
                return Task.FromResult<Guid?>(extractedId);
            });
        using var provider = CreateExtractorProvider();
        var trace = new List<string>();

        var parsed = await parser.ResolveAsync(
            new ChatNativeToolCallResolutionRequest(
                new ChatToolCall("call-1", "run", """{"name":"alpha"}"""),
                registry,
                new RegistrationToolExecutionPlanner(),
                async (extraction, ct) =>
                {
                    await Task.Yield();
                    return await extraction.Extractor(
                        provider,
                        extraction.ArgumentsJson,
                        ct);
                },
                trace.Add));

        parsed.Should().NotBeNull();
        parsed!.ResourceId.Should().Be(extractedId);
        parsed.ActionKey.Should().Be("run");
        provider.GetRequiredService<ExtractorService>()
            .Touched
            .Should()
            .BeTrue();
        trace.Should().HaveCount(2);
        trace[0].Should().Contain("Module tool: run");
        trace[1].Should().Contain($"ResourceId={extractedId:D}");
    }

    [Test]
    public async Task ResolveAsync_WhenDirectResourceIdExists_DoesNotInvokeExtractor()
    {
        var resourceId = Guid.NewGuid();
        var parser = new ChatNativeToolCallParser();
        var registry = CreateRegistry();
        var extractorCalls = 0;
        registry.RegisterResourceIdExtractor(
            "run",
            (_, _, _) =>
            {
                extractorCalls++;
                return Task.FromResult<Guid?>(Guid.NewGuid());
            });

        var parsed = await parser.ResolveAsync(
            new ChatNativeToolCallResolutionRequest(
                new ChatToolCall(
                    "call-1",
                    "run",
                    $$"""{"resourceId":"{{resourceId:D}}"}"""),
                registry,
                new RegistrationToolExecutionPlanner(),
                (_, _) => throw new InvalidOperationException(
                    "Extractor delegate should not be called.")));

        parsed.Should().NotBeNull();
        parsed!.ResourceId.Should().Be(resourceId);
        extractorCalls.Should().Be(0);
    }

    [Test]
    public async Task ResolveAsync_WhenToolDoesNotResolve_ReturnsNull()
    {
        var parser = new ChatNativeToolCallParser();

        var parsed = await parser.ResolveAsync(
            new ChatNativeToolCallResolutionRequest(
                new ChatToolCall("call-1", "missing", "{}"),
                CreateRegistry(),
                new RegistrationToolExecutionPlanner()));

        parsed.Should().BeNull();
    }

    [Test]
    public void BuildJobRequest_MapsParsedToolCallToAgentJobSubmission()
    {
        var callerAgentId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var parser = new ChatNativeToolCallParser();

        var request = parser.BuildJobRequest(
            new ParsedChatToolCall(
                "call-1",
                resourceId,
                """{"module":"test_registration","tool":"run","params":{}}""",
                ActionKey: "run"),
            callerAgentId);

        request.ActionKey.Should().Be("run");
        request.ResourceId.Should().Be(resourceId);
        request.CallerAgentId.Should().Be(callerAgentId);
        request.ScriptJson.Should().Be("""{"module":"test_registration","tool":"run","params":{}}""");
        request.AgentId.Should().BeNull();
    }

    private static RegistrationCatalog CreateRegistry()
    {
        var registry = new RegistrationCatalog();
        registry.Register(new TestRegistration());
        return registry;
    }

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static ServiceProvider CreateExtractorProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ExtractorService>();
        return services.BuildServiceProvider();
    }

    private sealed class ExtractorService
    {
        public bool Touched { get; set; }
    }

    private sealed class TestRegistration : ISharpClawCoreRegistration
    {
        public string Id => "test_registration";
        public string DisplayName => "Test Module";
        public string ToolPrefix => "test";

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public IReadOnlyList<RegistrationToolDefinition> GetToolDefinitions() =>
        [
            new(
                "run",
                "Run",
                Json("""{"type":"object"}"""),
                new RegistrationToolPermission(
                    IsPerResource: true,
                    Check: (_, _, _, _) => Task.FromResult(
                        AgentActionResult.Approve(
                            "ok",
                            PermissionClearance.ApprovedByWhitelistedUser))))
        ];

        public Task<string> ExecuteToolAsync(
            string toolName,
            JsonElement parameters,
            AgentJobContext job,
            IServiceProvider scopedServices,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
