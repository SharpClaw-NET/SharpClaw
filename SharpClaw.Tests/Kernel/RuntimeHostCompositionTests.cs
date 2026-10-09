using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Core.Kernel;
using SharpClaw.Gateway;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Persistence;
using SharpClaw.Persistence.JSONColdStore;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.Host;
using SharpClaw.Runtime.Host.Handlers;
using SharpClaw.Runtime.Host.Routing;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.Security;

namespace SharpClaw.Tests.Kernel;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and constructs this internal fixture through reflection; its tests are executed by the maintained test suite.")]
[TestFixture]
internal sealed class RuntimeHostCompositionTests
{
    [Test]
    [NonParallelizable]
    public async Task PackagedInProcessRegistration_ComposesHostGraphAndServesChatAsync()
    {
        var registrationRoot = AppContext.BaseDirectory;
        Directory.Exists(registrationRoot).Should().BeTrue(
            $"the test build must provide the normal Host module payload at '{registrationRoot}'");

        using var workspace = new TemporaryWorkspace();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provider:Key"] = "sharpclaw-test",
                ["Provider:Model"] = "test-harness-model",
                ["Packages:sharpclaw_providers_anthropic"] = "false",
                ["Packages:sharpclaw_providers_google"] = "false",
                ["Packages:sharpclaw_providers_llamasharp"] = "false",
                ["Packages:sharpclaw_providers_ollama"] = "false",
                ["Packages:sharpclaw_providers_openai_compat"] = "false",
            })
            .Build();
        using var registrationSet = PackagedDotNetRegistrationSet.Load(
            [
                Path.Combine(registrationRoot, "contributions"),
                Path.Combine(registrationRoot, "test-contributions"),
            ],
            configuration);
        registrationSet.SourceIds.Should().BeEquivalentTo(
            [
                "sharpclaw_persistence_jsoncoldstore",
                "sharpclaw_persistence_postgresql",
                "sharpclaw_persistence_sqlite",
                "sharpclaw_persistence_sqlserver",
                "sharpclaw_test_harness_in_process",
            ]);

        var databaseOptions = PersistenceOptions(workspace.DatabaseDirectory);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(KernelHostEndpoints).Assembly.GetName().Name,
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddConfiguration(configuration);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });
        RuntimeHostComposition.RegisterServices(
            builder.Services,
            configuration,
            workspace.InstancePaths,
            new EncryptionOptions
            {
                Key = new byte[32],
            },
            databaseOptions,
            registrationSet.Services);

        var app = builder.Build();
        await using var appAsyncDisposal = app.ConfigureAwait(false);
        var readiness = app.Services.GetRequiredService<RuntimeReadinessState>();
        readiness.IsReady.Should().BeFalse();
        var adapter = app.Services.GetRequiredService<RuntimeKernelAdapter>();
        app.Services.GetService<IConversationStore>().Should().BeNull();
        app.Services.GetRequiredService<IActionDispatcher>()
            .Should().BeSameAs(adapter.ActionDispatcher);
        adapter.Graph.ContainsAction(new SharpClawActionKey("runtime.request.receive"))
            .Should().BeTrue();
        adapter.Graph.ContainsAction(new SharpClawActionKey("jobs.submit"))
            .Should().BeTrue();
        adapter.Graph.ContainsAction(new SharpClawActionKey("jobs.dispatch"))
            .Should().BeTrue();
        adapter.Graph.ContainsAction(new SharpClawActionKey("jobs.cancel"))
            .Should().BeTrue();
        adapter.Graph.ContainsAction(new SharpClawActionKey("storage.query"))
            .Should().BeTrue();
        {
            var jobsScope = app.Services.CreateAsyncScope();
            await using (jobsScope.ConfigureAwait(false))
            {
                jobsScope.ServiceProvider
                    .GetRequiredService<KernelJobsStore>()
                    .Should().NotBeNull();
                jobsScope.ServiceProvider
                    .GetRequiredService<KernelJobsCoordinator>()
                    .Should().NotBeNull();
            }
        }
        await app.Services.GetRequiredService<RuntimeDatabaseReadiness>().ValidateAsync().ConfigureAwait(false);
        await registrationSet.ConnectCapabilitiesAsync(app.Services).ConfigureAwait(false);
        await adapter.StartAsync("test-host").ConfigureAwait(false);
        readiness.MarkReady();
        KernelHostEndpoints.Map(app);

        try
        {
            await app.StartAsync().ConfigureAwait(false);
            var cliContext = adapter.CreateCliExecutionContext(RequestPrincipal.Anonymous);
            var firstCli = await registrationSet.Application.TryInvokeCliAsync(
                "test-harness-scope",
                [],
                adapter,
                cliContext,
                CancellationToken.None).ConfigureAwait(false);
            var secondCli = await registrationSet.Application.TryInvokeCliAsync(
                "test-harness-scope",
                [],
                adapter,
                cliContext,
                CancellationToken.None).ConfigureAwait(false);
            var thirdCli = await registrationSet.Application.TryInvokeCliAsync(
                "test-harness-scope",
                [],
                adapter,
                cliContext,
                CancellationToken.None).ConfigureAwait(false);
            firstCli.Should().NotBeNull();
            secondCli.Should().NotBeNull();
            thirdCli.Should().NotBeNull();
            using var firstCliState = JsonDocument.Parse(firstCli!.Output.Single().Text);
            using var secondCliState = JsonDocument.Parse(secondCli!.Output.Single().Text);
            using var thirdCliState = JsonDocument.Parse(thirdCli!.Output.Single().Text);
            firstCliState.RootElement.GetProperty("instanceId").GetGuid()
                .Should().NotBe(secondCliState.RootElement.GetProperty("instanceId").GetGuid());
            secondCliState.RootElement.GetProperty("disposed").GetInt32().Should().Be(1);
            thirdCliState.RootElement.GetProperty("disposed").GetInt32().Should().Be(2);
            thirdCliState.RootElement.GetProperty("active").GetInt32().Should().Be(1);

            using var client = new HttpClient
            {
                BaseAddress = new Uri(app.Urls.Single()),
            };
            using var response = await client.PostAsJsonAsync(
                "/chat",
                new { message = "hello" }).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            body.Should().Contain("test harness response");

            using var streamResponse = await client.PostAsJsonAsync(
                "/chat/stream",
                new { message = "stream hello" }).ConfigureAwait(false);
            var streamBody = await streamResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

            streamResponse.StatusCode.Should().Be(HttpStatusCode.OK, streamBody);
            streamResponse.Content.Headers.ContentType!.MediaType
                .Should().Be("text/event-stream");
            streamBody.Should().Contain("test harness response");
            streamBody.Split("data: ", StringSplitOptions.RemoveEmptyEntries)
                .Should().HaveCountGreaterThan(1);
            readiness.IsReady.Should().BeTrue();
        }
        finally
        {
            readiness.MarkNotReady();
            await adapter.StopAsync().ConfigureAwait(false);
            await app.StopAsync().ConfigureAwait(false);
        }
    }

    [Test]
    [NonParallelizable]
    public async Task CanonicalJobsHttpPath_SubmitsAndDispatchesThroughProductionGraphAsync()
    {
        using var workspace = new TemporaryWorkspace();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provider:Key"] = "sharpclaw-test",
                ["Provider:Model"] = "test-harness-model",
                ["Packages:sharpclaw_providers_anthropic"] = "false",
                ["Packages:sharpclaw_providers_google"] = "false",
                ["Packages:sharpclaw_providers_llamasharp"] = "false",
                ["Packages:sharpclaw_providers_ollama"] = "false",
                ["Packages:sharpclaw_providers_openai_compat"] = "false",
            })
            .Build();
        using var registrationSet = PackagedDotNetRegistrationSet.Load(
            [
                Path.Combine(AppContext.BaseDirectory, "contributions"),
                Path.Combine(AppContext.BaseDirectory, "test-contributions"),
            ],
            configuration);
        var jobCapture = new JobProbeCapture();
        var jobRegistration = new JobProbeRegistration(jobCapture);
        var jobServices = SharpClawModuleCompiler.Compile(jobRegistration).Services;
        var modules = registrationSet.Services
            .Concat(jobServices)
            .ToArray();
        var databaseOptions = PersistenceOptions(workspace.DatabaseDirectory);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(KernelHostEndpoints).Assembly.GetName().Name,
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddConfiguration(configuration);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });
        RuntimeHostComposition.RegisterServices(
            builder.Services,
            configuration,
            workspace.InstancePaths,
            new EncryptionOptions { Key = new byte[32] },
            databaseOptions,
            modules);
        builder.Services.AddSingleton(new KernelGraphCompileOptions
        {
            ActionRegistrationCapabilityGrants = new Dictionary<
                string,
                IReadOnlyDictionary<string, ActionInterceptionCapabilities>>
            {
                [jobRegistration.Identity.Id] = new Dictionary<
                    string,
                    ActionInterceptionCapabilities>(StringComparer.Ordinal)
                {
                    [JobProbeHandler.Action.Value] =
                        ActionInterceptionCapabilities.Inspect |
                        ActionInterceptionCapabilities.Wrap |
                        ActionInterceptionCapabilities.Observe,
                },
            },
        });

        var app = builder.Build();
        await using var appAsyncDisposal_ = app.ConfigureAwait(false);
        var adapter = app.Services.GetRequiredService<RuntimeKernelAdapter>();
        using (var jobsScope = app.Services.CreateScope())
        {
            jobsScope.ServiceProvider
                .GetRequiredService<KernelJobsCoordinator>()
                .Should().NotBeNull();
        }
        var readiness = app.Services.GetRequiredService<RuntimeReadinessState>();
        await app.Services.GetRequiredService<RuntimeDatabaseReadiness>().ValidateAsync().ConfigureAwait(false);
        await adapter.StartAsync("jobs-http-test").ConfigureAwait(false);
        readiness.MarkReady();
        KernelHostEndpoints.Map(app);
        app.MapHandlers(typeof(KernelJobsHandlers).Assembly);

        try
        {
            await app.StartAsync().ConfigureAwait(false);
            using var client = new HttpClient
            {
                BaseAddress = new Uri(app.Urls.Single()),
            };
            using var submitResponse = await client.PostAsJsonAsync(
                "/jobs",
                new
                {
                    actionKey = JobProbeHandler.Action.Value,
                    input = new
                    {
                        contractName = JobProbeHandler.ContractName,
                        schemaVersion = 1,
                        value = JsonSerializer.Serialize(new
                        {
                            value = "queued-value",
                        }),
                    },
                }).ConfigureAwait(false);
            var submitBody = await submitResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

            submitResponse.StatusCode.Should().Be(HttpStatusCode.OK, submitBody);
            using var submitted = JsonDocument.Parse(submitBody);
            var jobId = submitted.RootElement.GetProperty("id").GetGuid();
            submitted.RootElement.GetProperty("status").GetInt32()
                .Should().Be((int)JobStatus.Queued);

            using var dispatchResponse = await client.PostAsync(
                $"/jobs/{jobId:D}/dispatch",
                content: null).ConfigureAwait(false);
            var dispatchBody = await dispatchResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

            dispatchResponse.StatusCode.Should().Be(HttpStatusCode.OK, dispatchBody);
            using var dispatched = JsonDocument.Parse(dispatchBody);
            dispatched.RootElement.GetProperty("outcome").GetInt32()
                .Should().Be((int)ActionOutcomeKind.Completed);
            var resultValue = dispatched.RootElement
                .GetProperty("result")
                .GetProperty("value")
                .GetString();
            resultValue.Should().NotBeNull();
            using var resultPayload = JsonDocument.Parse(resultValue!);
            resultPayload.RootElement.GetProperty("value").GetString()
                .Should().Be("queued-value-executed");
            jobCapture.ExecutionCount.Should().Be(1);

            using var progressResponse = await client.GetAsync(
                $"/jobs/{jobId:D}/progress").ConfigureAwait(false);
            progressResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using (var progress = JsonDocument.Parse(
                await progressResponse.Content.ReadAsStringAsync().ConfigureAwait(false)))
            {
                progress.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
            }

            using var attemptsResponse = await client.GetAsync(
                $"/jobs/{jobId:D}/attempts").ConfigureAwait(false);
            attemptsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using (var attempts = JsonDocument.Parse(
                await attemptsResponse.Content.ReadAsStringAsync().ConfigureAwait(false)))
            {
                attempts.RootElement.GetArrayLength().Should().Be(1);
            }

            using var artifactResponse = await client.GetAsync(
                $"/jobs/{jobId:D}/artifact").ConfigureAwait(false);
            artifactResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var artifact = await artifactResponse.Content
                .ReadFromJsonAsync<JobPayloadEnvelope>().ConfigureAwait(false);
            artifact.Should().NotBeNull();
            artifact!.Value.Should().Contain("queued-value-executed");

            using var recoveryResponse = await client.PostAsync(
                $"/jobs/{jobId:D}/recover",
                content: null).ConfigureAwait(false);
            recoveryResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using (var recovered = JsonDocument.Parse(
                await recoveryResponse.Content.ReadAsStringAsync().ConfigureAwait(false)))
            {
                recovered.RootElement.GetProperty("status").GetInt32()
                    .Should().Be((int)JobStatus.Completed);
            }

            using var replayResponse = await client.PostAsync(
                $"/jobs/{jobId:D}/dispatch",
                content: null).ConfigureAwait(false);
            replayResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using (var replay = JsonDocument.Parse(
                await replayResponse.Content.ReadAsStringAsync().ConfigureAwait(false)))
            {
                replay.RootElement.GetProperty("outcome").GetInt32()
                    .Should().Be((int)ActionOutcomeKind.Completed);
            }
            jobCapture.ExecutionCount.Should().Be(1);

            using var secondSubmitResponse = await client.PostAsJsonAsync(
                "/jobs",
                new
                {
                    actionKey = JobProbeHandler.Action.Value,
                    input = new
                    {
                        contractName = JobProbeHandler.ContractName,
                        schemaVersion = 1,
                        value = JsonSerializer.Serialize(new
                        {
                            value = "second-value",
                        }),
                    },
                }).ConfigureAwait(false);
            secondSubmitResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using var secondSubmitted = JsonDocument.Parse(
                await secondSubmitResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
            var secondJobId = secondSubmitted.RootElement.GetProperty("id").GetGuid();

            using var secondDispatchResponse = await client.PostAsync(
                $"/jobs/{secondJobId:D}/dispatch",
                content: null).ConfigureAwait(false);
            secondDispatchResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            jobCapture.ExecutionCount.Should().Be(2);
            jobCapture.ExecutionInstanceIds.Should().OnlyHaveUniqueItems();
            jobCapture.ExecutionInstanceIds.Should().HaveCount(2);

            using var secondDeleteResponse = await client.DeleteAsync(
                $"/jobs/{secondJobId:D}").ConfigureAwait(false);
            secondDeleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

            using var deleteResponse = await client.DeleteAsync(
                $"/jobs/{jobId:D}").ConfigureAwait(false);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

            using var deletedResponse = await client.GetAsync(
                $"/jobs/{jobId:D}").ConfigureAwait(false);
            deletedResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            jobCapture.ActiveCount.Should().Be(0);
            jobCapture.DisposedCount.Should().Be(jobCapture.CreatedCount);
        }
        finally
        {
            readiness.MarkNotReady();
            await adapter.StopAsync().ConfigureAwait(false);
            await app.StopAsync().ConfigureAwait(false);
        }
    }

    [Test]
    [NonParallelizable]
    public async Task ConcurrentAuthenticatedHttpRequests_UseDistinctKernelRootContextsAsync()
    {
        using var workspace = new TemporaryWorkspace();
        var probe = new RequestContextProbe(expected: 2);
        var module = new RequestContextProbeRegistration(probe);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provider:Key"] = "context-probe",
                ["Provider:Model"] = "context-probe-model",
            })
            .Build();
        var databaseOptions = PersistenceOptions(workspace.DatabaseDirectory);
        var receiveKey = new SharpClawActionKey("runtime.request.receive");
        var receiveManifest = KernelActionCatalog.DescriptorFor(receiveKey);
        var receiveDescriptor = receiveManifest.ToDescriptor();
        var receiveTypes = KernelSchemaIdentity.ActionTypes(
            receiveDescriptor,
            typeof(KernelActionEnvelope),
            typeof(object));

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
            TestServiceGraph.Collect([module, new JSONColdStorePersistenceModule()]));
        builder.Services.AddSingleton(new KernelGraphCompileOptions
        {
            ActionRegistrationCapabilityGrants = new Dictionary<
                string,
                IReadOnlyDictionary<string, ActionInterceptionCapabilities>>
            {
                [module.Identity.Id] = new Dictionary<string, ActionInterceptionCapabilities>(
                    StringComparer.Ordinal)
                {
                    [receiveKey.Value] = receiveManifest.Capabilities,
                },
            },
            SensitiveActionApprovals =
            [
                new KernelSensitiveActionApproval(
                    module.Identity.Id,
                    receiveKey,
                    receiveDescriptor.Version,
                    receiveTypes.ActionType.AssemblyQualifiedName!,
                    receiveTypes.ResultType.AssemblyQualifiedName!,
                    KernelSchemaIdentity.Action(
                        receiveDescriptor,
                        typeof(KernelActionEnvelope),
                        typeof(object))),
            ],
        });

        var app = builder.Build();
        await using var appAsyncDisposal__ = app.ConfigureAwait(false);
        var readiness = app.Services.GetRequiredService<RuntimeReadinessState>();
        var adapter = app.Services.GetRequiredService<RuntimeKernelAdapter>();
        await app.Services.GetRequiredService<RuntimeDatabaseReadiness>().ValidateAsync().ConfigureAwait(false);
        await adapter.StartAsync("request-context-test").ConfigureAwait(false);
        readiness.MarkReady();
        app.Use(async (context, next) =>
        {
            var subject = context.Request.Headers["X-Test-Subject"].ToString();
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, subject),
                    new Claim(ClaimTypes.Name, subject),
                    new Claim(ClaimTypes.Role, "operator"),
                ],
                "test"));
            context.Items[typeof(ExtensionFeatureSet)] = new ExtensionFeatureSet(
            [
                new ExtensionFeature(
                    $"test.{subject}",
                    1,
                    "request-context-probe",
                    256,
                    JsonSerializer.SerializeToElement(new { subject })),
            ]);
            await next(context).ConfigureAwait(false);
        });
        KernelHostEndpoints.Map(app);

        try
        {
            await app.StartAsync().ConfigureAwait(false);
            using var client = new HttpClient
            {
                BaseAddress = new Uri(app.Urls.Single()),
            };
            var first = SendAuthenticatedChatAsync(client, "caller-a", "idempotency-a");
            var second = SendAuthenticatedChatAsync(client, "caller-b", "idempotency-b");
            await probe.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            probe.Release.TrySetResult(true);
            var responses = await Task.WhenAll(first, second).ConfigureAwait(false);
            var completedResponse = await SendAuthenticatedChatAsync(
                client,
                "caller-c",
                "idempotency-c").ConfigureAwait(false);
            completedResponse.StatusCode.Should().Be(HttpStatusCode.OK, completedResponse.Body);

            responses.Should().AllSatisfy(response =>
            {
                response.StatusCode.Should().Be(HttpStatusCode.OK, response.Body);
                response.Body.Should().Contain("context probe response");
            });
            var observations = probe.Items.ToArray();
            observations.Should().HaveCount(3);
            observations.Select(item => item.Caller.SubjectId)
                .Should().BeEquivalentTo(["caller-a", "caller-b", "caller-c"]);
            observations.Should().AllSatisfy(item =>
            {
                item.Caller.IsAuthenticated.Should().BeTrue();
                item.Caller.Roles.Should().Contain("operator");
                item.Features.Items.Should().ContainSingle();
                item.Features.Items[0].ContractName.Should().Be($"test.{item.Caller.SubjectId}");
            });
            var expectedFirst = new DefaultHttpContext();
            expectedFirst.Request.Headers["Idempotency-Key"] = "idempotency-a";
            var expectedSecond = new DefaultHttpContext();
            expectedSecond.Request.Headers["Idempotency-Key"] = "idempotency-b";
            var expectedThird = new DefaultHttpContext();
            expectedThird.Request.Headers["Idempotency-Key"] = "idempotency-c";
            observations.Select(item => item.IdempotencyKey)
                .Should().BeEquivalentTo(
                [
                    KernelHostEndpoints.CreateExecutionContext(expectedFirst).IdempotencyKey,
                    KernelHostEndpoints.CreateExecutionContext(expectedSecond).IdempotencyKey,
                    KernelHostEndpoints.CreateExecutionContext(expectedThird).IdempotencyKey,
                ]);
            observations.Select(item => item.TraceId).Distinct().Should().HaveCount(3);
            observations.Should().AllSatisfy(item => item.Depth.Should().Be(0));

            probe.FailureSubject = "caller-fail";
            var failedResponse = await SendAuthenticatedChatAsync(
                client,
                "caller-fail",
                "idempotency-fail").ConfigureAwait(false);
            failedResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            failedResponse.Body.Should().Contain("An internal server error occurred.");
            failedResponse.Body.Should().NotContain("request context probe failure");

            var failedStreamResponse = await SendAuthenticatedAsync(
                client,
                "/chat/stream",
                "caller-fail",
                "idempotency-stream-fail").ConfigureAwait(false);
            failedStreamResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            failedStreamResponse.Body.Should().Contain("An internal server error occurred.");
            failedStreamResponse.Body.Should().NotContain("request context probe failure");
            failedStreamResponse.ContentType.Should().Be("application/json");
            probe.FailureSubject = null;

            var afterFailureResponse = await SendAuthenticatedChatAsync(
                client,
                "caller-d",
                "idempotency-d").ConfigureAwait(false);
            afterFailureResponse.StatusCode.Should().Be(HttpStatusCode.OK, afterFailureResponse.Body);
            probe.Items.Should().Contain(item =>
                item.Caller.SubjectId == "caller-d" && item.Depth == 0);
        }
        finally
        {
            readiness.MarkNotReady();
            await adapter.StopAsync().ConfigureAwait(false);
            await app.StopAsync().ConfigureAwait(false);
        }
    }

    [Test]
    [NonParallelizable]
    public async Task NormalHostPayload_ComposesPackagedProviderAndExecutesChatAsync()
    {
        var providerServer = await FakeOpenAiServer.CreateAsync().ConfigureAwait(false);
        await using var providerServerAsyncDisposal = providerServer.ConfigureAwait(false);
        using var workspace = new TemporaryWorkspace();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provider:Key"] = "custom",
                ["Provider:Model"] = "gpt-3.5-turbo",
                ["Provider:Endpoint"] = providerServer.Endpoint,
                ["Provider:ApiKey"] = "normal-payload-test-key",
            })
            .Build();

        using var registrationSet = PackagedDotNetRegistrationSet.Load(
            Path.Combine(AppContext.BaseDirectory, "contributions"),
            configuration);
        registrationSet.SourceIds
            .Should().BeEquivalentTo(
                [
                    "sharpclaw_providers_anthropic",
                    "sharpclaw_providers_google",
                    "sharpclaw_providers_llamasharp",
                    "sharpclaw_providers_ollama",
                    "sharpclaw_providers_openai_compat",
                    "sharpclaw_persistence_jsoncoldstore",
                    "sharpclaw_persistence_postgresql",
                    "sharpclaw_persistence_sqlite",
                    "sharpclaw_persistence_sqlserver",
                ]);
        File.Exists(Path.Combine(
                AppContext.BaseDirectory,
                "contributions",
                "sharpclaw_providers_openai_compat",
                "SharpClaw.Modules.Providers.OpenAICompatible.dll"))
            .Should().BeTrue();
        registrationSet.SourceIds.Should().NotContain("sharpclaw_test_harness_in_process");

        var databaseOptions = PersistenceOptions(workspace.DatabaseDirectory);

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
            registrationSet.Services);

        var app = builder.Build();
        await using var appAsyncDisposal___ = app.ConfigureAwait(false);
        var adapter = app.Services.GetRequiredService<RuntimeKernelAdapter>();
        var graphPlugins = (IEnumerable<IProviderPlugin>?)adapter.Graph.GetService(
            typeof(IEnumerable<IProviderPlugin>));
        graphPlugins.Should().NotBeNull();
        graphPlugins!.Should().Contain(plugin => plugin.ProviderKey == "custom");

        var readiness = app.Services.GetRequiredService<RuntimeReadinessState>();
        await app.Services.GetRequiredService<RuntimeDatabaseReadiness>().ValidateAsync().ConfigureAwait(false);
        await adapter.StartAsync("normal-provider-test").ConfigureAwait(false);
        readiness.MarkReady();
        KernelHostEndpoints.Map(app);

        try
        {
            await app.StartAsync().ConfigureAwait(false);
            using var client = new HttpClient
            {
                BaseAddress = new Uri(app.Urls.Single()),
            };
            using var response = await client.PostAsJsonAsync(
                "/chat",
                new { message = "normal packaged provider" }).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            body.Should().Contain("normal packaged provider response");
            readiness.IsReady.Should().BeTrue();
        }
        finally
        {
            readiness.MarkNotReady();
            await adapter.StopAsync().ConfigureAwait(false);
            await app.StopAsync().ConfigureAwait(false);
        }
    }

    [Test]
    [NonParallelizable]
    public async Task NormalHostPayload_RestartRemainsStatelessWithoutContextRegistrationAsync()
    {
        var providerServer = await FakeOpenAiServer.CreateAsync().ConfigureAwait(false);
        await using var providerServerAsyncDisposal2 = providerServer.ConfigureAwait(false);
        using var workspace = new TemporaryWorkspace();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provider:Key"] = "custom",
                ["Provider:Model"] = "gpt-3.5-turbo",
                ["Provider:Endpoint"] = providerServer.Endpoint,
                ["Provider:ApiKey"] = "normal-payload-restart-key",
            })
            .Build();
        var databaseOptions = PersistenceOptions(workspace.DatabaseDirectory);

        await RunNormalProductionHostAsync(
            workspace,
            configuration,
            databaseOptions,
            async (app, _) =>
            {
                using var client = new HttpClient
                {
                    BaseAddress = new Uri(app.Urls.Single()),
                };
                using var response = await client.PostAsJsonAsync(
                    "/chat",
                    new { message = "packaged restart" }).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                response.StatusCode.Should().Be(HttpStatusCode.OK, body);
                body.Should().Contain("normal packaged provider response");
            }).ConfigureAwait(false);

        await RunNormalProductionHostAsync(
            workspace,
            configuration,
            databaseOptions,
            async (app, _) =>
            {
                await Task.CompletedTask.ConfigureAwait(false);
                app.Services.GetService<IConversationStore>().Should().BeNull();
            }).ConfigureAwait(false);
    }

    [Test]
    [NonParallelizable]
    public async Task NormalHostPayload_LlamaLocalModelStorePersistsThroughRestartAsync()
    {
        using var workspace = new TemporaryWorkspace();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provider:Key"] = "llamasharp",
                ["Provider:Model"] = "local-model",
            })
            .Build();
        var databaseOptions = PersistenceOptions(workspace.DatabaseDirectory);

        var modelId = Guid.NewGuid();
        await RunNormalProductionHostAsync(
            workspace,
            configuration,
            databaseOptions,
            async (app, services) =>
            {
                var adapter = app.Services.GetRequiredService<RuntimeKernelAdapter>();
                await UseLlamaLocalModelStoreAsync(
                    adapter,
                    services,
                    async store =>
                    {
                        await InvokeLlamaPlaceholderAsync(store, modelId).ConfigureAwait(false);
                        (await InvokeLlamaGetByModelIdAsync(store, modelId).ConfigureAwait(false)).Should().NotBeNull();
                    }).ConfigureAwait(false);
            }).ConfigureAwait(false);

        await RunNormalProductionHostAsync(
            workspace,
            configuration,
            databaseOptions,
            async (app, services) =>
            {
                await UseLlamaLocalModelStoreAsync(
                    app.Services.GetRequiredService<RuntimeKernelAdapter>(),
                    services,
                    async store =>
                    {
                        var record = await InvokeLlamaGetByModelIdAsync(store, modelId).ConfigureAwait(false);
                        record.Should().NotBeNull();
                        record!.GetType().GetProperty("ModelId")!.GetValue(record)
                            .Should().Be(modelId);
                    }).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    [Test]
    [NonParallelizable]
    public async Task NormalHostPayload_MapsLlamaSharpEndpointThroughRuntimeAndGatewayAsync()
    {
        using var workspace = new TemporaryWorkspace();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provider:Key"] = "llamasharp",
                ["Provider:Model"] = "local-model",
            })
            .Build();
        var databaseOptions = PersistenceOptions(workspace.DatabaseDirectory);

        await RunNormalProductionHostAsync(
            workspace,
            configuration,
            databaseOptions,
            async (runtime, _) =>
            {
                var adapter = runtime.Services.GetRequiredService<RuntimeKernelAdapter>();
                runtime.Services.GetRequiredService<IActionDispatcher>()
                    .Should().BeSameAs(adapter.ActionDispatcher);
                {
                    var storageScope = runtime.Services.CreateAsyncScope();
                    await using (storageScope.ConfigureAwait(false))
                    {
                        storageScope.ServiceProvider.GetServices<IScopedStorageGateway>()
                            .Should().ContainSingle();
                    }
                }

                using (var runtimeClient = new HttpClient
                {
                    BaseAddress = new Uri(runtime.Urls.Single()),
                })
                using (var runtimeResponse = await runtimeClient.GetAsync("/models/local/").ConfigureAwait(false))
                {
                    var body = await runtimeResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                    runtimeResponse.StatusCode.Should().Be(HttpStatusCode.OK, body);
                    JsonDocument.Parse(body).RootElement.ValueKind.Should().Be(JsonValueKind.Array);
                }

                var gatewayConfiguration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [$"{InternalApiOptions.SectionName}:BaseUrl"] = runtime.Urls.Single(),
                        [$"{InternalApiOptions.SectionName}:ApiKey"] = "scope-test-key",
                    })
                    .Build();
                var gatewayBuilder = WebApplication.CreateBuilder();
                gatewayBuilder.Configuration.Sources.Clear();
                gatewayBuilder.Configuration.AddConfiguration(gatewayConfiguration);
                gatewayBuilder.WebHost.UseUrls("http://127.0.0.1:0");
                gatewayBuilder.Services.Configure<InternalApiOptions>(
                    gatewayBuilder.Configuration.GetSection(InternalApiOptions.SectionName));
                gatewayBuilder.Services.AddHttpContextAccessor();
                gatewayBuilder.Services.AddHttpClient<InternalApiClient>(client =>
                {
                    client.BaseAddress = new Uri(runtime.Urls.Single());
                    client.Timeout = TimeSpan.FromSeconds(30);
                });

                var gateway = gatewayBuilder.Build();
                await using var gatewayAsyncDisposal = gateway.ConfigureAwait(false);
                gateway.MapGatewayProxyEndpoints();
                await gateway.StartAsync().ConfigureAwait(false);
                try
                {
                    using var gatewayClient = new HttpClient
                    {
                        BaseAddress = new Uri(gateway.Urls.Single()),
                    };
                    using var gatewayResponse = await gatewayClient.GetAsync("/api/models/local/").ConfigureAwait(false);
                    var body = await gatewayResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                    gatewayResponse.StatusCode.Should().Be(HttpStatusCode.OK, body);
                    JsonDocument.Parse(body).RootElement.ValueKind.Should().Be(JsonValueKind.Array);
                }
                finally
                {
                    await gateway.StopAsync().ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
    }

    [Test]
    [NonParallelizable]
    public async Task ProductionJsonColdStore_RestartHasNoHistoryWithoutContextRegistrationAsync()
    {
        using var workspace = new TemporaryWorkspace();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provider:Key"] = "sharpclaw-test",
                ["Provider:Model"] = "test-harness-model",
                ["Packages:sharpclaw_providers_anthropic"] = "false",
                ["Packages:sharpclaw_providers_google"] = "false",
                ["Packages:sharpclaw_providers_llamasharp"] = "false",
                ["Packages:sharpclaw_providers_ollama"] = "false",
                ["Packages:sharpclaw_providers_openai_compat"] = "false",
            })
            .Build();
        var databaseOptions = PersistenceOptions(workspace.DatabaseDirectory);

        await RunProductionHostAsync(
            workspace,
            configuration,
            databaseOptions,
            async app =>
            {
                using var client = new HttpClient
                {
                    BaseAddress = new Uri(app.Urls.Single()),
                };
                using var response = await client.PostAsJsonAsync(
                    "/chat",
                    new { message = "restart me" }).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                response.StatusCode.Should().Be(HttpStatusCode.OK, body);
                body.Should().Contain("test harness response");
            }).ConfigureAwait(false);

        await RunProductionHostAsync(
            workspace,
            configuration,
            databaseOptions,
            async app =>
            {
                await Task.CompletedTask.ConfigureAwait(false);
                app.Services.GetService<IConversationStore>().Should().BeNull();
            }).ConfigureAwait(false);
    }

    [Test]
    public async Task MissingConfiguredProviderAllowsGraphStartupForSetupAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        using var workspace = new TemporaryWorkspace();
        using var registrationSet = PackagedDotNetRegistrationSet.Load(
            [
                Path.Combine(AppContext.BaseDirectory, "contributions"),
                Path.Combine(AppContext.BaseDirectory, "test-contributions"),
            ],
            configuration);
        var services = new ServiceCollection();
        RuntimeHostComposition.RegisterServices(
            services,
            configuration,
            workspace.InstancePaths,
            new EncryptionOptions { Key = new byte[32] },
            PersistenceOptions(workspace.DatabaseDirectory),
            registrationSet.Services);

        var provider = services.BuildServiceProvider();
        await using var providerAsyncDisposal = provider.ConfigureAwait(false);
        var adapter = provider.GetRequiredService<RuntimeKernelAdapter>();
        await adapter.StartAsync("unconfigured-host-setup").ConfigureAwait(false);
        try
        {
            RuntimeProviderSetup.Describe(configuration, adapter).SetupRequired.Should().BeTrue();
        }
        finally { await adapter.StopAsync().ConfigureAwait(false); }
        provider.GetRequiredService<RuntimeReadinessState>().IsReady.Should().BeFalse();
    }

    [Test]
    public void DisabledPackagedInProcessRegistration_IsExcludedBeforeGraphCompilation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Packages:sharpclaw_test_harness_in_process"] = "false",
                ["Packages:sharpclaw_providers_anthropic"] = "false",
                ["Packages:sharpclaw_providers_google"] = "false",
                ["Packages:sharpclaw_providers_llamasharp"] = "false",
                ["Packages:sharpclaw_providers_ollama"] = "false",
                ["Packages:sharpclaw_providers_openai_compat"] = "false",
                ["Packages:sharpclaw_persistence_jsoncoldstore"] = "false",
                ["Packages:sharpclaw_persistence_postgresql"] = "false",
                ["Packages:sharpclaw_persistence_sqlite"] = "false",
                ["Packages:sharpclaw_persistence_sqlserver"] = "false",
            })
            .Build();

        using var registrationSet = PackagedDotNetRegistrationSet.Load(
            [
                Path.Combine(AppContext.BaseDirectory, "contributions"),
                Path.Combine(AppContext.BaseDirectory, "test-contributions"),
            ],
            configuration);

        registrationSet.SourceIds.Should().BeEmpty();
    }

    [TestCase("null")]
    [TestCase("0")]
    [TestCase("\"false\"")]
    public void PackagedRegistrationSet_RejectsNonBooleanEnabledValues(string enabledJson)
    {
        using var registrationRoot = new TemporaryRegistrationRoot();
        registrationRoot.WriteManifest(
            "invalid-enabled",
            $$"""
            {
              "id": "invalid-enabled",
              "displayName": "Invalid enabled",
              "version": "0.1.0",
              "toolPrefix": "invalid",
              "runtime": "dotnet",
              "hostMode": "inprocess",
              "entryAssembly": "unused.dll",
              "entryType": "Unused.Module",
              "enabled": {{enabledJson}}
            }
            """);

        var act = () => PackagedDotNetRegistrationSet.Load(
            registrationRoot.Path,
            new ConfigurationBuilder().Build());

        act.Should().Throw<JsonException>();
    }

    [Test]
    public void PackagedRegistrationSet_RejectsDuplicateManifestIdentityBeforeLoad()
    {
        using var registrationRoot = new TemporaryRegistrationRoot();
        const string manifest = """
            {
              "id": "duplicate-module",
              "displayName": "Duplicate module",
              "version": "0.1.0",
              "toolPrefix": "duplicate",
              "runtime": "dotnet",
              "hostMode": "inprocess",
              "entryAssembly": "unused.dll",
              "entryType": "Unused.Module",
              "enabled": false
            }
            """;
        registrationRoot.WriteManifest("first", manifest);
        registrationRoot.WriteManifest("second", manifest);

        var act = () => PackagedDotNetRegistrationSet.Load(
            registrationRoot.Path,
            new ConfigurationBuilder().Build());

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("duplicate-module");
    }

    private static async Task<(HttpStatusCode StatusCode, string Body, string? ContentType)> SendAuthenticatedChatAsync(
        HttpClient client,
        string subject,
        string idempotencyKey)
        => await SendAuthenticatedAsync(client, "/chat", subject, idempotencyKey).ConfigureAwait(false);

    private static async Task<(HttpStatusCode StatusCode, string Body, string? ContentType)> SendAuthenticatedAsync(
        HttpClient client,
        string path,
        string subject,
        string idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new { message = subject }),
        };
        request.Headers.Add("X-Test-Subject", subject);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        return (
            response.StatusCode,
            await response.Content.ReadAsStringAsync().ConfigureAwait(false),
            response.Content.Headers.ContentType?.MediaType);
    }

    private static SharpClawPersistenceOptions PersistenceOptions(string dataDirectory) =>
        new()
        {
            ProviderKey = SharpClawPersistenceOptions.DefaultProviderKey,
            DataDirectory = dataDirectory,
        };

    private static async Task RunProductionHostAsync(
        TemporaryWorkspace workspace,
        IConfiguration configuration,
        SharpClawPersistenceOptions databaseOptions,
        Func<WebApplication, Task> operation)
    {
        using var registrationSet = PackagedDotNetRegistrationSet.Load(
            [
                Path.Combine(AppContext.BaseDirectory, "contributions"),
                Path.Combine(AppContext.BaseDirectory, "test-contributions"),
            ],
            configuration);
        registrationSet.SourceIds.Should().BeEquivalentTo(
            [
                "sharpclaw_persistence_jsoncoldstore",
                "sharpclaw_persistence_postgresql",
                "sharpclaw_persistence_sqlite",
                "sharpclaw_persistence_sqlserver",
                "sharpclaw_test_harness_in_process",
            ]);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(KernelHostEndpoints).Assembly.GetName().Name,
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddConfiguration(configuration);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });
        RuntimeHostComposition.RegisterServices(
            builder.Services,
            configuration,
            workspace.InstancePaths,
            new EncryptionOptions { Key = new byte[32] },
            databaseOptions,
            registrationSet.Services);

        var app = builder.Build();
        await using var appAsyncDisposal____ = app.ConfigureAwait(false);
        var readiness = app.Services.GetRequiredService<RuntimeReadinessState>();
        var adapter = app.Services.GetRequiredService<RuntimeKernelAdapter>();
        await app.Services.GetRequiredService<RuntimeDatabaseReadiness>().ValidateAsync().ConfigureAwait(false);
        await adapter.StartAsync("test-host").ConfigureAwait(false);
        readiness.MarkReady();
        KernelHostEndpoints.Map(app);

        try
        {
            await app.StartAsync().ConfigureAwait(false);
            await operation(app).ConfigureAwait(false);
        }
        finally
        {
            readiness.MarkNotReady();
            await adapter.StopAsync().ConfigureAwait(false);
            await app.StopAsync().ConfigureAwait(false);
        }
    }

    private static async Task RunNormalProductionHostAsync(
        TemporaryWorkspace workspace,
        IConfiguration configuration,
        SharpClawPersistenceOptions databaseOptions,
        Func<WebApplication, IReadOnlyList<ServiceDescriptor>, Task> operation)
    {
        using var registrationSet = PackagedDotNetRegistrationSet.Load(
            Path.Combine(AppContext.BaseDirectory, "contributions"),
            configuration);
        registrationSet.SourceIds
            .Should().BeEquivalentTo(
                [
                    "sharpclaw_providers_anthropic",
                    "sharpclaw_providers_google",
                    "sharpclaw_providers_llamasharp",
                    "sharpclaw_providers_ollama",
                    "sharpclaw_providers_openai_compat",
                    "sharpclaw_persistence_jsoncoldstore",
                    "sharpclaw_persistence_postgresql",
                    "sharpclaw_persistence_sqlite",
                    "sharpclaw_persistence_sqlserver",
                ]);

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
            registrationSet.Services);

        var app = builder.Build();
        await using var appAsyncDisposal_____ = app.ConfigureAwait(false);
        var readiness = app.Services.GetRequiredService<RuntimeReadinessState>();
        var adapter = app.Services.GetRequiredService<RuntimeKernelAdapter>();
        await app.Services.GetRequiredService<RuntimeDatabaseReadiness>().ValidateAsync().ConfigureAwait(false);
        await registrationSet.ConnectCapabilitiesAsync(app.Services).ConfigureAwait(false);
        await adapter.StartAsync("normal-provider-restart-test").ConfigureAwait(false);
        readiness.MarkReady();
        KernelHostEndpoints.Map(app);
        registrationSet.Application.MapEndpoints(app, adapter);

        try
        {
            await app.StartAsync().ConfigureAwait(false);
            await operation(app, registrationSet.Services).ConfigureAwait(false);
        }
        finally
        {
            readiness.MarkNotReady();
            await adapter.StopAsync().ConfigureAwait(false);
            await app.StopAsync().ConfigureAwait(false);
        }
    }

    private static async ValueTask UseLlamaLocalModelStoreAsync(
        RuntimeKernelAdapter adapter,
        IReadOnlyList<ServiceDescriptor> services,
        Func<object, ValueTask> operation)
    {
        var storeType = services
            .Select(descriptor => descriptor.ServiceType)
            .FirstOrDefault(type => type.FullName ==
                "SharpClaw.Modules.Providers.LlamaSharp.Services.LocalModelStore");

        if (storeType is null)
            throw new InvalidOperationException("The LlamaSharp LocalModelStore was not registered.");
        await adapter.Graph.RunInServiceScopeAsync(async serviceProvider =>
        {
            await operation(serviceProvider.GetRequiredService(storeType)).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    private static async Task InvokeLlamaPlaceholderAsync(object store, Guid modelId)
    {
        var storeType = store.GetType();
        var method = storeType.GetMethod("CreateOrReuseDownloadPlaceholderAsync")
            ?? throw new InvalidOperationException("The LlamaSharp LocalModelStore write method was not loaded.");
        var resolvedFileType = method.GetParameters()[1].ParameterType;
        var resolvedFile = Activator.CreateInstance(
            resolvedFileType,
            "https://example.invalid/model.gguf",
            "model.gguf",
            "Q4_K_M")!;
        var task = (Task)method.Invoke(
            store,
            [modelId, resolvedFile, "https://example.invalid/model.gguf", "model.gguf", CancellationToken.None])!;
        await task.ConfigureAwait(false);
    }

    private static async Task<object?> InvokeLlamaGetByModelIdAsync(object store, Guid modelId)
    {
        var method = store.GetType().GetMethod("GetByModelIdAsync")
            ?? throw new InvalidOperationException("The LlamaSharp LocalModelStore read method was not loaded.");
        var task = (Task)method.Invoke(store, [modelId, CancellationToken.None])!;
        await task.ConfigureAwait(false);
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }

    private sealed record ContextObservation(
        RequestPrincipal Caller,
        Guid TraceId,
        Guid IdempotencyKey,
        int Depth,
        ExtensionFeatureSet Features);

    private sealed class RequestContextProbe(int expected)
    {
        private string? _failureSubject;

        public ConcurrentQueue<ContextObservation> Items { get; } = new();

        public string? FailureSubject
        {
            get => Volatile.Read(ref _failureSubject);
            set => Volatile.Write(ref _failureSubject, value);
        }

        public TaskCompletionSource<bool> Observed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Record(ActionContext<KernelActionEnvelope> context)
        {
            Items.Enqueue(new ContextObservation(
                context.Caller,
                context.TraceId,
                context.IdempotencyKey,
                context.Depth,
                context.Features));
            if (Items.Count >= expected)
                Observed.TrySetResult(true);
        }

        public bool ShouldFail(string subjectId) =>
            string.Equals(FailureSubject, subjectId, StringComparison.Ordinal);
    }

    private sealed class RequestContextProbeInterceptor(RequestContextProbe probe)
        : IActionInterceptor<KernelActionEnvelope, object>
    {
        public async ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            probe.Record(context);
            if (probe.ShouldFail(context.Caller.SubjectId))
                throw new ApplicationException("request context probe failure");
            await probe.Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await control.ProceedAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class RequestContextProbeRegistration(RequestContextProbe probe) : ISharpClawModule
    {
        private readonly RequestContextProvider _provider = new();

        public ModuleIdentity Identity { get; } =
            new("request-context-probe", "Request context probe", "context");

        public void ConfigureServices(IServiceCollection module)
        {
            module.AddSingleton<IProviderPlugin>(_provider);
            module.AddSingleton(probe);
            module.AddSingleton<RequestContextProbeInterceptor>();
            module.OnAction(new SharpClawActionKey("runtime.request.receive"))
                .Use<RequestContextProbeInterceptor>(
                    new HookOrdering(
                        "request-context-probe",
                        HookPriority.Normal,
                        [],
                        [],
                        TimeSpan.FromSeconds(5),
                        HookFailurePolicy.FailAction));
        }
    }

    private sealed class JobProbeRegistration(JobProbeCapture capture) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new(
            "jobs-http-probe",
            "Jobs HTTP probe",
            "jobs-http");

        public void ConfigureServices(IServiceCollection module)
        {
            module.AddAction(new ActionDescriptor<KernelActionEnvelope, object>(
                JobProbeHandler.Action,
                1,
                "jobs-http-probe",
                ActionInterceptionCapabilities.Inspect |
                ActionInterceptionCapabilities.Wrap |
                ActionInterceptionCapabilities.Observe,
                false,
                false,
                new ActionRepeatPolicy(
                    ActionRepeatKind.None,
                    1,
                    TimeSpan.Zero,
                    "jobs-http-probe"),
                null,
                TimeSpan.FromSeconds(5))
            {
                SafePoints =
                [
                    ActionSafePoint.BeforeTerminal,
                ],
            });
            module.AddSingleton(capture);
            module.AddScoped<IJobHandler, JobProbeHandler>();
        }
    }

    private sealed class JobProbeHandler : IJobHandler<ProbePayload, ProbePayload>, IDisposable
    {
        private readonly JobProbeCapture _capture;
        private readonly Guid _instanceId = Guid.NewGuid();
        private int _disposed;

        public JobProbeHandler(JobProbeCapture capture)
        {
            _capture = capture;
            _capture.Created(_instanceId);
        }

        public const string ContractName = "jobs-http-probe";

        public static SharpClawActionKey Action { get; } =
            new("probe.jobs.http");

        public SharpClawActionKey ActionKey => Action;

        public JobExecutionSafety Safety => JobExecutionSafety.Idempotent;

        public IJobPayloadCodec<ProbePayload> InputCodec { get; } =
            new JsonJobPayloadCodec<ProbePayload>(ContractName);

        public IJobPayloadCodec<ProbePayload> ResultCodec { get; } =
            new JsonJobPayloadCodec<ProbePayload>(ContractName);

        public ValueTask<ProbePayload> ExecuteAsync(
            JobExecutionContext context,
            ProbePayload input,
            CancellationToken cancellationToken)
        {
            _capture.Executed(_instanceId);
            return ValueTask.FromResult(new ProbePayload(input.Value + "-executed"));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _capture.Disposed(_instanceId);
        }
    }

    private sealed class JobProbeCapture
    {
        private readonly ConcurrentQueue<Guid> _executionInstanceIds = new();
        private int _activeCount;
        private int _createdCount;
        private int _disposedCount;
        private int _executionCount;

        public int ActiveCount => Volatile.Read(ref _activeCount);
        public int CreatedCount => Volatile.Read(ref _createdCount);
        public int DisposedCount => Volatile.Read(ref _disposedCount);
        public int ExecutionCount => Volatile.Read(ref _executionCount);
        public IReadOnlyList<Guid> ExecutionInstanceIds => _executionInstanceIds.ToArray();

        public void Created(Guid instanceId)
        {
            _ = instanceId;
            Interlocked.Increment(ref _activeCount);
            Interlocked.Increment(ref _createdCount);
        }

        public void Executed(Guid instanceId)
        {
            _executionInstanceIds.Enqueue(instanceId);
            Interlocked.Increment(ref _executionCount);
        }

        public void Disposed(Guid instanceId)
        {
            _ = instanceId;
            Interlocked.Decrement(ref _activeCount);
            Interlocked.Increment(ref _disposedCount);
        }
    }

    private sealed record ProbePayload(string Value);

    private sealed class RequestContextProvider : IProviderPlugin, IProviderApiClient
    {
        public string ProviderKey => "context-probe";
        public string DisplayName => "Context probe";
        public bool RequiresEndpoint => false;
        public bool RequiresApiKey => false;
        public IModelCapabilityResolver Capabilities { get; } =
            new EmptyCapabilityResolver();
        public IReadOnlyList<ProviderCostSeed> CostSeeds => [];
        public IDeviceCodeFlow? DeviceCodeFlow => null;

        public IProviderApiClient CreateClient(ProviderClientOptions options) => this;

        public Task<IReadOnlyList<string>> ListModelIdsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(["context-probe-model"]);

        public Task<ChatCompletionResult> ChatCompletionAsync(
            string model,
            string? systemPrompt,
            IReadOnlyList<ChatCompletionMessage> messages,
            int? maxCompletionTokens = null,
            Dictionary<string, JsonElement>? providerParameters = null,
            CompletionParameters? completionParameters = null,
            CancellationToken ct = default) =>
            Task.FromResult(new ChatCompletionResult
            {
                Content = "context probe response",
                FinishReason = FinishReason.Stop,
                Usage = new TokenUsage(1, 1),
            });
    }

    private sealed class EmptyCapabilityResolver : IModelCapabilityResolver
    {
        public HashSet<string> Resolve(string modelName) => [];
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "sharpclaw-runtime-host-" + Guid.NewGuid().ToString("N"));

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
                    Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class TemporaryRegistrationRoot : IDisposable
    {
        public TemporaryRegistrationRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "sharpclaw-module-manifest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void WriteManifest(string registrationDirectory, string json)
        {
            var directory = System.IO.Path.Combine(Path, registrationDirectory);
            Directory.CreateDirectory(directory);
            File.WriteAllText(System.IO.Path.Combine(directory, "package.json"), json);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class FakeOpenAiServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private FakeOpenAiServer(WebApplication app)
        {
            _app = app;
        }

        public string Endpoint => _app.Urls.Single() + "/v1";

        public static async Task<FakeOpenAiServer> CreateAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.MapPost(
                "/v1/chat/completions",
                () => Results.Json(new
                {
                    id = "normal-provider-test",
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            message = new
                            {
                                role = "assistant",
                                content = "normal packaged provider response",
                            },
                            finish_reason = "stop",
                        },
                    },
                    usage = new
                    {
                        prompt_tokens = 1,
                        completion_tokens = 1,
                    },
                }));
            await app.StartAsync().ConfigureAwait(false);
            return new FakeOpenAiServer(app);
        }

        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }
}
