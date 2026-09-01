using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Core.Kernel;
using SharpClaw.ModuleHost.OutOfProcess;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.Host;
using SharpClaw.Runtime.Host.Api;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.Security;

namespace SharpClaw.Tests.Kernel;

[TestFixture]
[NonParallelizable]
public sealed class AgentOrchestrationHostGateTests
{
    private const string ContextModuleId = "sharpclaw_context";
    private const string PermissionModuleId = "sharpclaw_two_tier_permission";
    private const string AgentsModuleId = "sharpclaw_agents";
    private const string ContextActionKey = "context.api.dispatch";
    private const string PermissionActionKey = "permission.api.dispatch";
    private const string AgentsActionKey = "agents.api.dispatch";
    private static readonly JsonElement EmptyPayload = JsonSerializer.SerializeToElement(new { });
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions HashJson = new(JsonSerializerDefaults.General);

    [Test, CancelAfter(300000)]
    public async Task ProductionHost_ComposesAllPackagedModulesAndExecutesAgentContracts()
    {
        var initialSidecars = FindSidecarProcessIds();
        await using var provider = await FakeOpenAiServer.CreateAsync();
        using var workspace = new TemporaryWorkspace();
        var configuration = CreateConfiguration(provider.Endpoint);

        await using (var moduleSet = await PackagedDotNetModuleSet.LoadProductionAsync(
                         Path.Combine(AppContext.BaseDirectory, "modules"),
                         configuration))
        {
            var sidecars = moduleSet.Modules.OfType<OutOfProcessModuleProxy>().ToArray();
            sidecars.Select(module => module.Identity.Id).Should().Contain(
                [ContextModuleId, PermissionModuleId, AgentsModuleId]);
            var providerModule = moduleSet.Modules.Single(module =>
                module.Identity.Id == "sharpclaw_providers_openai_compat");
            providerModule.Should().NotBeOfType<OutOfProcessModuleProxy>();

            var databaseOptions = new DatabaseProviderOptions
            {
                Provider = StorageMode.JsonFile,
            };
            databaseOptions.JsonFile.DataDirectory = workspace.DatabaseDirectory;
            databaseOptions.JsonFile.EncryptAtRest = false;

            var telemetry = new RecordingModuleStorageTelemetry();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(KernelHostEndpoints).Assembly.GetName().Name,
            });
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddConfiguration(configuration);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            RuntimeHostComposition.RegisterServices(
                builder.Services,
                configuration,
                workspace.InstancePaths,
                new EncryptionOptions { Key = new byte[32] },
                databaseOptions,
                moduleSet.Modules);
            builder.Services.AddSingleton<IModuleStorageTelemetry>(telemetry);

            await using var app = builder.Build();
            var readiness = app.Services.GetRequiredService<RuntimeReadinessState>();
            var adapter = app.Services.GetRequiredService<RuntimeKernelAdapter>();
            var dispatcher = app.Services.GetRequiredService<IActionDispatcher>();
            dispatcher.Should().BeSameAs(adapter.ActionDispatcher);

            await app.Services.GetRequiredService<RuntimeDatabaseReadiness>().ValidateAsync();
            await moduleSet.ConnectCapabilitiesAsync(app.Services);
            await adapter.StartAsync("tracked-package-production-gate");
            readiness.MarkReady();

            var administrator = Administrator(Guid.NewGuid().ToString("D"));
            var currentHttpPrincipal = administrator;
            app.Use(async (context, next) =>
            {
                context.User = ToClaimsPrincipal(currentHttpPrincipal);
                await next();
            });
            app.UseMiddleware<ApiKeyMiddleware>();
            app.UseWebSockets();
            KernelHostEndpoints.Map(app);
            moduleSet.Application.MapEndpoints(app, adapter);

            try
            {
                await app.StartAsync();
                var context = FindModule(sidecars, ContextModuleId);
                var permission = FindModule(sidecars, PermissionModuleId);
                var agents = FindModule(sidecars, AgentsModuleId);

                await AssertApplicationCliAsync(
                    moduleSet.Application,
                    "ctx-channel-list",
                    adapter,
                    administrator);
                await AssertApplicationCliAsync(
                    moduleSet.Application,
                    "perm-policy-list",
                    adapter,
                    administrator);
                await AssertApplicationCliAsync(
                    moduleSet.Application,
                    "agents-list",
                    adapter,
                    administrator);

                using var client = CreateHostClient(app, app.Services.GetRequiredService<ApiKeyProvider>().ApiKey);
                await AssertEndpointAsync(client, "/sharpclaw/context/channels");
                await AssertEndpointAsync(client, "/sharpclaw/permission/policies");
                await AssertEndpointAsync(client, "/sharpclaw/agents/list");

                (await InvokeApiAsync(
                    context.Client,
                    ContextActionKey,
                    "channel.list",
                    EmptyPayload,
                    administrator)).Kind.Should().Be(ActionOutcomeKind.Completed);
                (await InvokeApiAsync(
                    permission.Client,
                    PermissionActionKey,
                    "policy.list",
                    EmptyPayload,
                    administrator)).Kind.Should().Be(ActionOutcomeKind.Completed);
                (await InvokeApiAsync(
                    agents.Client,
                    AgentsActionKey,
                    "agent.list",
                    EmptyPayload,
                    administrator)).Kind.Should().Be(ActionOutcomeKind.Completed);

                var worker = new RequestPrincipal(Guid.NewGuid().ToString("D"), IsAuthenticated: true);
                var denied = await InvokeApiAsync(
                    agents.Client,
                    AgentsActionKey,
                    "agent.list",
                    EmptyPayload,
                    worker);
                denied.Kind.Should().NotBe(ActionOutcomeKind.Completed);

                var policyPayload = JsonSerializer.SerializeToElement(new
                {
                    subjectId = worker.SubjectId,
                    roles = Array.Empty<string>(),
                    capabilities = new[] { "manage_agents", "manage_agent_jobs" },
                    hardDeniedCapabilities = Array.Empty<string>(),
                    clearance = "Independent",
                    requireSourceOptIn = false,
                    delegatedBy = Array.Empty<string>(),
                    expiresAt = (DateTimeOffset?)null,
                    updatedAt = DateTimeOffset.UtcNow,
                    whitelistedUserIds = Array.Empty<string>(),
                    permittedAgentIds = Array.Empty<string>(),
                    whitelistedAgentIds = Array.Empty<string>(),
                }, WebJson);
                var policy = await InvokeApiAsync(
                    permission.Client,
                    PermissionActionKey,
                    "policy.save",
                    policyPayload,
                    administrator);
                policy.Kind.Should().Be(
                    ActionOutcomeKind.Completed,
                    FormatFailure(policy, telemetry));

                var allowed = await InvokeApiAsync(
                    agents.Client,
                    AgentsActionKey,
                    "agent.list",
                    EmptyPayload,
                    worker);
                allowed.Kind.Should().Be(
                    ActionOutcomeKind.Completed,
                    FormatFailure(allowed, telemetry));

                var sourceId = Guid.NewGuid();
                var import = await InvokeApiAsync(
                    agents.Client,
                    AgentsActionKey,
                    "agents.job.import",
                    CreateImportSnapshot(sourceId, administrator.SubjectId),
                    administrator);
                import.Kind.Should().Be(
                    ActionOutcomeKind.Completed,
                    FormatFailure(import, telemetry));
                import.Result.ValueKind.Should().Be(JsonValueKind.Array);
                import.Result.GetArrayLength().Should().Be(1);
                import.Result[0].GetProperty("id").GetGuid().Should().Be(sourceId);

                await AssertReplayRejectedAndLaterUseAsync(
                    permission.Client,
                    adapter,
                    administrator);
                await AssertCancellationAndLaterUseAsync(context.Client, administrator);

                var agentResult = await InvokeApiAsync(
                    agents.Client,
                    AgentsActionKey,
                    "agent.create",
                    JsonSerializer.SerializeToElement(new
                    {
                        name = "Pipeline Agent",
                        modelId = Guid.NewGuid(),
                        providerKey = "custom",
                        modelName = "pipeline-model",
                        systemPrompt = "Run the tracked production gate.",
                    }, WebJson),
                    administrator);
                agentResult.Kind.Should().Be(
                    ActionOutcomeKind.Completed,
                    FormatFailure(agentResult, telemetry));
                var agentId = agentResult.Result.GetProperty("id").GetGuid();
                var agentPrincipal = new RequestPrincipal(
                    agentId.ToString("D"),
                    "Pipeline Agent",
                    IsAuthenticated: true);
                var agentPolicy = await InvokeApiAsync(
                    permission.Client,
                    PermissionActionKey,
                    "policy.save",
                    JsonSerializer.SerializeToElement(new
                    {
                        subjectId = agentPrincipal.SubjectId,
                        roles = Array.Empty<string>(),
                        capabilities = new[]
                        {
                            "read_agent_profile",
                            "context_create",
                            "context_read",
                            "context_write",
                        },
                        hardDeniedCapabilities = Array.Empty<string>(),
                        clearance = "Independent",
                        requireSourceOptIn = false,
                        delegatedBy = Array.Empty<string>(),
                        expiresAt = (DateTimeOffset?)null,
                        updatedAt = DateTimeOffset.UtcNow,
                        whitelistedUserIds = Array.Empty<string>(),
                        permittedAgentIds = new[] { agentPrincipal.SubjectId },
                        whitelistedAgentIds = new[] { agentPrincipal.SubjectId },
                    }, WebJson),
                    administrator);
                agentPolicy.Kind.Should().Be(
                    ActionOutcomeKind.Completed,
                    FormatFailure(agentPolicy, telemetry));
                currentHttpPrincipal = agentPrincipal;

                using var chat = await client.PostAsJsonAsync("/chat", new { message = "pipeline gate" });
                var chatBody = await chat.Content.ReadAsStringAsync();
                chat.StatusCode.Should().Be(HttpStatusCode.OK, chatBody);
                chatBody.Should().Contain("frozen package graph response");
                provider.RequestCount.Should().Be(1);

                telemetry.Events.Should().NotBeEmpty();
                telemetry.Events.Should().OnlyContain(item => item.Success);
                telemetry.Events.Should().Contain(item =>
                    item.ModuleId == PermissionModuleId
                    && item.Operation == ModuleStorageOperations.Upsert);
                telemetry.Events.Should().Contain(item =>
                    item.ModuleId == AgentsModuleId
                    && item.Operation == ModuleStorageOperations.Upsert);
                TestContext.Progress.WriteLine(
                    "module-storage-counts=" + string.Join(
                        ",",
                        telemetry.Events
                            .GroupBy(item => (item.ModuleId, item.Operation))
                            .OrderBy(group => group.Key.ModuleId, StringComparer.Ordinal)
                            .ThenBy(group => group.Key.Operation, StringComparer.Ordinal)
                            .Select(group => $"{group.Key.ModuleId}:{group.Key.Operation}:{group.Count()}")));
            }
            finally
            {
                readiness.MarkNotReady();
                await adapter.StopAsync();
                await app.StopAsync();
            }
        }

        await AssertSidecarsStoppedAsync(initialSidecars);
    }

    private static IConfiguration CreateConfiguration(string providerEndpoint) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provider:Key"] = "custom",
                ["Provider:Model"] = "pipeline-model",
                ["Provider:ApiKey"] = "pipeline-key",
                ["Provider:Endpoint"] = providerEndpoint,
                ["Auth:DisableApiKeyCheck"] = "false",
            })
            .Build();

    private static OutOfProcessModuleProxy FindModule(
        IEnumerable<OutOfProcessModuleProxy> modules,
        string moduleId) =>
        modules.Single(module => module.Identity.Id == moduleId);

    private static async Task AssertApplicationCliAsync(
        PackagedModuleApplicationRegistry application,
        string command,
        RuntimeKernelAdapter adapter,
        RequestPrincipal caller)
    {
        var result = await application.TryInvokeCliAsync(
            command,
            [],
            adapter,
            adapter.CreateCliExecutionContext(caller),
            CancellationToken.None);
        result.Should().NotBeNull();
        result!.Succeeded.Should().BeTrue(
            result.Error?.Message ?? string.Join(" | ", result.Output.Select(item => item.Text)));
    }

    private static HttpClient CreateHostClient(WebApplication app, string apiKey)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(app.Urls.Single()),
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    private static async Task AssertEndpointAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
    }

    private static async ValueTask<IActionOutcome<JsonElement>> InvokeApiAsync(
        OutOfProcessModuleClient client,
        string actionKey,
        string operation,
        JsonElement payload,
        RequestPrincipal caller,
        CancellationToken cancellationToken = default)
    {
        var action = JsonSerializer.SerializeToElement(
            new NeutralApiAction(operation, payload),
            WebJson);
        var entry = client.Application.ActionEntries.Single(item =>
            item.Descriptor.Key.Value == actionKey);
        var definition = client.Discovery.ActionDefinitions.Single(item =>
            item.ActionKey == entry.Descriptor.Key
            && item.Version == entry.Descriptor.Version);
        var context = client.IssueHostActionContext(
            HostActionEntryIngress.CrossModule,
            "sharpclaw-host-gate",
            client.Discovery.ModuleId,
            definition,
            entry.Descriptor,
            action,
            caller,
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(1));
        return await client.InvokeModuleActionEntryAsync(
            definition,
            entry.Descriptor,
            action,
            context,
            cancellationToken);
    }

    private static async Task AssertReplayRejectedAndLaterUseAsync(
        OutOfProcessModuleClient client,
        RuntimeKernelAdapter adapter,
        RequestPrincipal caller)
    {
        const string command = "perm-policy-list";
        var descriptor = adapter.Graph.GetStandardAction(RuntimeCliActionCatalog.Execute);
        var contract = KernelActionCatalog.DescriptorFor(RuntimeCliActionCatalog.Execute);
        descriptor = descriptor with
        {
            InputSchema = contract.InputSchema,
            ResultSchema = contract.ResultSchema,
        };
        var invocation = new RuntimeCliActionInvocation("execute", command, 0);
        var context = client.IssueHostActionContext(
            HostActionEntryIngress.Cli,
            command,
            client.Discovery.ModuleId,
            descriptor,
            new KernelActionEnvelope(descriptor.Key, invocation),
            caller,
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(1));
        var first = await client.InvokeCliAsync(command, [], context);
        first.Result.Succeeded.Should().BeTrue();
        var replay = async () => await client.InvokeCliAsync(command, [], context);
        await replay.Should().ThrowAsync<Exception>();

        var later = await InvokeApiAsync(
            client,
            PermissionActionKey,
            "policy.list",
            EmptyPayload,
            caller);
        later.Kind.Should().Be(ActionOutcomeKind.Completed);
    }

    private static async Task AssertCancellationAndLaterUseAsync(
        OutOfProcessModuleClient client,
        RequestPrincipal caller)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = async () => await InvokeApiAsync(
            client,
            ContextActionKey,
            "channel.list",
            EmptyPayload,
            caller,
            cancellation.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        var later = await InvokeApiAsync(
            client,
            ContextActionKey,
            "channel.list",
            EmptyPayload,
            caller);
        later.Kind.Should().Be(ActionOutcomeKind.Completed);
    }

    private static JsonElement CreateImportSnapshot(Guid sourceId, string callerIdentity)
    {
        const string actionIdentity = "agents.legacy.gate";
        var now = DateTimeOffset.UtcNow;
        var record = new NeutralAgentJobRecord(
            sourceId,
            Guid.NewGuid(),
            callerIdentity,
            actionIdentity,
            "agent:gate",
            "{}",
            "{}",
            "D:\\temp\\SharpClaw\\agent-gate",
            "queued",
            "Independent",
            0,
            0,
            [],
            Guid.NewGuid(),
            Guid.NewGuid(),
            callerIdentity,
            now,
            now,
            null,
            null,
            null,
            null,
            null,
            "sharpclaw.jobs");
        var mapping = new AgentJobActionMapping(
            actionIdentity,
            "sharpclaw.agents.job.canonical.v1",
            "json.v1");
        var sourceHash = ComputeHash(JsonSerializer.Serialize(record, HashJson));
        var mappingHash = ComputeHash(JsonSerializer.Serialize(new[] { mapping }, HashJson));
        var aggregateHash = ComputeHash($"1\n{sourceId:D}:{sourceHash}\n");
        return JsonSerializer.SerializeToElement(new
        {
            snapshotId = "agent-gate-" + sourceId.ToString("N"),
            capturedAt = now,
            expectedRecordCount = 1,
            orderedSourceIds = new[] { sourceId },
            sourceHashes = new[] { sourceHash },
            aggregateHash,
            mappingHash,
            records = new[] { record },
            actionMappings = new[] { mapping },
        }, WebJson);
    }

    private static string ComputeHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string FormatFailure(
        IActionOutcome<JsonElement> outcome,
        RecordingModuleStorageTelemetry telemetry) =>
        $"{outcome.Error?.Code}: {outcome.Error?.Message}; "
        + $"storage={JsonSerializer.Serialize(telemetry.Events.ToArray())}";

    private static RequestPrincipal Administrator(string subject) =>
        new(
            subject,
            subject,
            new HashSet<string>(["admin", "administrator"], StringComparer.OrdinalIgnoreCase),
            true);

    private static ClaimsPrincipal ToClaimsPrincipal(RequestPrincipal principal)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, principal.SubjectId),
            new(ClaimTypes.Name, principal.DisplayName ?? principal.SubjectId),
        };
        if (principal.Roles is not null)
            claims.AddRange(principal.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "tracked-package-gate"));
    }

    private static HashSet<int> FindSidecarProcessIds() =>
        Process.GetProcessesByName("SharpClaw.ModuleHost.OutOfProcess")
            .Select(process => process.Id)
            .ToHashSet();

    private static async Task AssertSidecarsStoppedAsync(IReadOnlySet<int> initialProcessIds)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        HashSet<int> remaining;
        do
        {
            remaining = FindSidecarProcessIds();
            remaining.ExceptWith(initialProcessIds);
            if (remaining.Count == 0)
                return;
            await Task.Delay(100);
        }
        while (DateTimeOffset.UtcNow < deadline);

        remaining.Should().BeEmpty("the production module set must stop every task-owned sidecar");
    }

    private sealed record NeutralApiAction(string Operation, JsonElement Payload);

    private sealed record NeutralAgentJobRecord(
        Guid SourceId,
        Guid AgentId,
        string CallerIdentity,
        string ActionIdentity,
        string Resource,
        string ScriptJson,
        string PayloadJson,
        string WorkingDirectory,
        string Status,
        string Clearance,
        long InputTokens,
        long OutputTokens,
        IReadOnlyList<string> ApprovalIdentities,
        Guid? ChannelId,
        Guid? ContextId,
        string PermissionIdentity,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt,
        Guid? CanonicalJobId,
        string? ResultJson,
        string? Error,
        string ResultAuthority);

    private sealed record AgentJobActionMapping(
        string ActionIdentity,
        string HandlerKey,
        string PayloadCodec);

    private sealed class RecordingModuleStorageTelemetry : IModuleStorageTelemetry
    {
        public ConcurrentQueue<ModuleStorageTelemetryEvent> Events { get; } = new();

        public void Record(ModuleStorageTelemetryEvent telemetryEvent) =>
            Events.Enqueue(telemetryEvent);
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "sharpclaw-package-production-gate-" + Guid.NewGuid().ToString("N"));

        public TemporaryWorkspace()
        {
            Directory.CreateDirectory(_root);
            InstancePaths = new SharpClawInstancePaths(
                SharpClawInstanceKind.Backend,
                _root,
                _root,
                _root);
            InstancePaths.EnsureDirectories();
        }

        public SharpClawInstancePaths InstancePaths { get; }

        public string DatabaseDirectory => Path.Combine(_root, "database");

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, true);
            }
            catch
            {
            }
        }
    }

    private sealed class FakeOpenAiServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private int _requestCount;

        private FakeOpenAiServer(WebApplication app)
        {
            _app = app;
        }

        public string Endpoint => _app.Urls.Single() + "/v1";

        public int RequestCount => Volatile.Read(ref _requestCount);

        public static async Task<FakeOpenAiServer> CreateAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var server = new FakeOpenAiServer(app);
            app.MapPost("/v1/chat/completions", () =>
            {
                Interlocked.Increment(ref server._requestCount);
                return Results.Json(new
                {
                    id = "tracked-package-production-gate",
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            message = new
                            {
                                role = "assistant",
                                content = "frozen package graph response",
                            },
                            finish_reason = "stop",
                        },
                    },
                    usage = new
                    {
                        prompt_tokens = 1,
                        completion_tokens = 1,
                    },
                });
            });
            await app.StartAsync();
            return server;
        }

        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }
}

