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
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.Host;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.Security;

namespace SharpClaw.Tests.Kernel;

[TestFixture]
public sealed class RuntimeHostCompositionTests
{
    [Test]
    [NonParallelizable]
    public async Task PackagedInProcessModule_ComposesHostGraphAndServesChat()
    {
        var moduleRoot = AppContext.BaseDirectory;
        Directory.Exists(moduleRoot).Should().BeTrue(
            $"the test build must provide the normal Host module payload at '{moduleRoot}'");

        using var workspace = new TemporaryWorkspace();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provider:Key"] = "sharpclaw-test",
                ["Provider:Model"] = "test-harness-model",
                ["Modules:sharpclaw_providers_anthropic"] = "false",
                ["Modules:sharpclaw_providers_google"] = "false",
                ["Modules:sharpclaw_providers_llamasharp"] = "false",
                ["Modules:sharpclaw_providers_ollama"] = "false",
                ["Modules:sharpclaw_providers_openai_compat"] = "false",
            })
            .Build();
        using var moduleSet = PackagedDotNetModuleSet.Load(
            [
                Path.Combine(moduleRoot, "modules"),
                Path.Combine(moduleRoot, "test-modules"),
            ],
            configuration);
        moduleSet.Modules.Should().ContainSingle()
            .Which.Identity.Id.Should().Be("sharpclaw_test_harness_in_process");

        var databaseOptions = new DatabaseProviderOptions
        {
            Provider = StorageMode.JsonFile,
        };
        databaseOptions.JsonFile.DataDirectory = workspace.DatabaseDirectory;
        databaseOptions.JsonFile.EncryptAtRest = false;
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
            new EncryptionOptions
            {
                Key = new byte[32],
            },
            databaseOptions,
            moduleSet.Modules);

        await using var app = builder.Build();
        var readiness = app.Services.GetRequiredService<RuntimeReadinessState>();
        readiness.IsReady.Should().BeFalse();
        var adapter = app.Services.GetRequiredService<RuntimeKernelAdapter>();
        app.Services.GetRequiredService<IActionDispatcher>()
            .Should().BeSameAs(adapter.ActionDispatcher);
        adapter.Graph.ContainsAction(new SharpClawActionKey("runtime.request.receive"))
            .Should().BeTrue();
        await app.Services.GetRequiredService<RuntimeDatabaseReadiness>().ValidateAsync();
        await adapter.StartAsync("test-host");
        readiness.MarkReady();
        KernelHostEndpoints.Map(app);

        try
        {
            await app.StartAsync();
            using var client = new HttpClient
            {
                BaseAddress = new Uri(app.Urls.Single()),
            };
            using var response = await client.PostAsJsonAsync(
                "/chat",
                new { message = "hello" });
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            body.Should().Contain("test harness response");

            using var streamResponse = await client.PostAsJsonAsync(
                "/chat/stream",
                new { message = "stream hello" });
            var streamBody = await streamResponse.Content.ReadAsStringAsync();

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
            await adapter.StopAsync();
            await app.StopAsync();
        }
    }

    [Test]
    [NonParallelizable]
    public async Task ConcurrentAuthenticatedHttpRequests_UseDistinctKernelRootContexts()
    {
        using var workspace = new TemporaryWorkspace();
        var probe = new RequestContextProbe(expected: 2);
        var module = new RequestContextProbeModule(probe);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provider:Key"] = "context-probe",
                ["Provider:Model"] = "context-probe-model",
            })
            .Build();
        var databaseOptions = new DatabaseProviderOptions
        {
            Provider = StorageMode.JsonFile,
        };
        databaseOptions.JsonFile.DataDirectory = workspace.DatabaseDirectory;
        databaseOptions.JsonFile.EncryptAtRest = false;
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
            [module]);
        builder.Services.AddSingleton(new KernelGraphCompileOptions
        {
            ActionModuleCapabilityGrants = new Dictionary<
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

        await using var app = builder.Build();
        var readiness = app.Services.GetRequiredService<RuntimeReadinessState>();
        var adapter = app.Services.GetRequiredService<RuntimeKernelAdapter>();
        await app.Services.GetRequiredService<RuntimeDatabaseReadiness>().ValidateAsync();
        await adapter.StartAsync("request-context-test");
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
            await next(context);
        });
        KernelHostEndpoints.Map(app);

        try
        {
            await app.StartAsync();
            using var client = new HttpClient
            {
                BaseAddress = new Uri(app.Urls.Single()),
            };
            var first = SendAuthenticatedChatAsync(client, "caller-a", "idempotency-a");
            var second = SendAuthenticatedChatAsync(client, "caller-b", "idempotency-b");
            await probe.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            probe.Release.TrySetResult(true);
            var responses = await Task.WhenAll(first, second);
            var completedResponse = await SendAuthenticatedChatAsync(
                client,
                "caller-c",
                "idempotency-c");
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
                "idempotency-fail");
            failedResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            failedResponse.Body.Should().Contain("An internal server error occurred.");
            failedResponse.Body.Should().NotContain("request context probe failure");

            var failedStreamResponse = await SendAuthenticatedAsync(
                client,
                "/chat/stream",
                "caller-fail",
                "idempotency-stream-fail");
            failedStreamResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            failedStreamResponse.Body.Should().Contain("An internal server error occurred.");
            failedStreamResponse.Body.Should().NotContain("request context probe failure");
            failedStreamResponse.ContentType.Should().Be("application/json");
            probe.FailureSubject = null;

            var afterFailureResponse = await SendAuthenticatedChatAsync(
                client,
                "caller-d",
                "idempotency-d");
            afterFailureResponse.StatusCode.Should().Be(HttpStatusCode.OK, afterFailureResponse.Body);
            probe.Items.Should().Contain(item =>
                item.Caller.SubjectId == "caller-d" && item.Depth == 0);
        }
        finally
        {
            readiness.MarkNotReady();
            await adapter.StopAsync();
            await app.StopAsync();
        }
    }

    [Test]
    [NonParallelizable]
    public async Task NormalHostPayload_ComposesPackagedProviderAndExecutesChat()
    {
        await using var providerServer = await FakeOpenAiServer.CreateAsync();
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

        using var moduleSet = PackagedDotNetModuleSet.Load(
            Path.Combine(AppContext.BaseDirectory, "modules"),
            configuration);
        moduleSet.Modules.Select(module => module.Identity.Id)
            .Should().BeEquivalentTo(
                [
                    "sharpclaw_providers_anthropic",
                    "sharpclaw_providers_google",
                    "sharpclaw_providers_llamasharp",
                    "sharpclaw_providers_ollama",
                    "sharpclaw_providers_openai_compat",
                ]);
        var openAiModule = moduleSet.Modules.Single(module =>
            module.Identity.Id == "sharpclaw_providers_openai_compat");
        openAiModule.GetType().Assembly.GetName().Name.Should()
            .Be("SharpClaw.Modules.Providers.OpenAICompatible");
        Path.GetFullPath(openAiModule.GetType().Assembly.Location)
            .Should()
            .Be(Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "modules",
                "sharpclaw_providers_openai_compat",
                "SharpClaw.Modules.Providers.OpenAICompatible.dll")));
        moduleSet.Modules.Should().NotContain(module =>
            module.Identity.Id == "sharpclaw_test_harness_in_process");

        var databaseOptions = new DatabaseProviderOptions
        {
            Provider = StorageMode.JsonFile,
        };
        databaseOptions.JsonFile.DataDirectory = workspace.DatabaseDirectory;
        databaseOptions.JsonFile.EncryptAtRest = false;

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

        await using var app = builder.Build();
        var adapter = app.Services.GetRequiredService<RuntimeKernelAdapter>();
        var graphPlugins = (IEnumerable<IProviderPlugin>?)adapter.Graph.GetService(
            typeof(IEnumerable<IProviderPlugin>));
        graphPlugins.Should().NotBeNull();
        graphPlugins!.Should().Contain(plugin => plugin.ProviderKey == "custom");

        var readiness = app.Services.GetRequiredService<RuntimeReadinessState>();
        await app.Services.GetRequiredService<RuntimeDatabaseReadiness>().ValidateAsync();
        await adapter.StartAsync("normal-provider-test");
        readiness.MarkReady();
        KernelHostEndpoints.Map(app);

        try
        {
            await app.StartAsync();
            using var client = new HttpClient
            {
                BaseAddress = new Uri(app.Urls.Single()),
            };
            using var response = await client.PostAsJsonAsync(
                "/chat",
                new { message = "normal packaged provider" });
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            body.Should().Contain("normal packaged provider response");
            readiness.IsReady.Should().BeTrue();
        }
        finally
        {
            readiness.MarkNotReady();
            await adapter.StopAsync();
            await app.StopAsync();
        }
    }

    [Test]
    [NonParallelizable]
    public async Task NormalHostPayload_RestartHasNoHistoryWithoutContextModule()
    {
        await using var providerServer = await FakeOpenAiServer.CreateAsync();
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
        var databaseOptions = new DatabaseProviderOptions
        {
            Provider = StorageMode.JsonFile,
        };
        databaseOptions.JsonFile.DataDirectory = workspace.DatabaseDirectory;
        databaseOptions.JsonFile.EncryptAtRest = false;

        await RunNormalProductionHostAsync(
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
                    new { message = "packaged restart" });
                var body = await response.Content.ReadAsStringAsync();

                response.StatusCode.Should().Be(HttpStatusCode.OK, body);
                body.Should().Contain("normal packaged provider response");
            });

        var conversationId = Guid.Parse(workspace.InstancePaths.Manifest.InstanceId);
        IReadOnlyList<string>? history = null;
        await RunNormalProductionHostAsync(
            workspace,
            configuration,
            databaseOptions,
            async app =>
            {
                await using var scope = app.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
                history = await db.ChatMessages
                    .AsNoTracking()
                    .Where(message => message.ChannelId == conversationId)
                    .OrderBy(message => message.CreatedAt)
                    .ThenBy(message => message.Id)
                    .Select(message => message.Content)
                    .ToListAsync();
            });

        history.Should().NotBeNull();
        history!.Should().BeEmpty();
    }

    [Test]
    [NonParallelizable]
    public async Task NormalHostPayload_LlamaLocalModelStorePersistsThroughRestart()
    {
        using var workspace = new TemporaryWorkspace();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provider:Key"] = "llamasharp",
                ["Provider:Model"] = "local-model",
            })
            .Build();
        var databaseOptions = new DatabaseProviderOptions
        {
            Provider = StorageMode.JsonFile,
        };
        databaseOptions.JsonFile.DataDirectory = workspace.DatabaseDirectory;
        databaseOptions.JsonFile.EncryptAtRest = false;

        var modelId = Guid.NewGuid();
        await RunNormalProductionHostAsync(
            workspace,
            configuration,
            databaseOptions,
            async app =>
            {
                var adapter = app.Services.GetRequiredService<RuntimeKernelAdapter>();
                var store = ResolveLlamaLocalModelStore(adapter);
                await InvokeLlamaPlaceholderAsync(store, modelId);
                (await InvokeLlamaGetByModelIdAsync(store, modelId)).Should().NotBeNull();
            });

        await RunNormalProductionHostAsync(
            workspace,
            configuration,
            databaseOptions,
            async app =>
            {
                var store = ResolveLlamaLocalModelStore(
                    app.Services.GetRequiredService<RuntimeKernelAdapter>());
                var record = await InvokeLlamaGetByModelIdAsync(store, modelId);
                record.Should().NotBeNull();
                record!.GetType().GetProperty("ModelId")!.GetValue(record).Should().Be(modelId);
            });
    }

    [Test]
    [NonParallelizable]
    public async Task ProductionJsonColdStore_RestartHasNoHistoryWithoutContextModule()
    {
        using var workspace = new TemporaryWorkspace();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provider:Key"] = "sharpclaw-test",
                ["Provider:Model"] = "test-harness-model",
                ["Modules:sharpclaw_providers_anthropic"] = "false",
                ["Modules:sharpclaw_providers_google"] = "false",
                ["Modules:sharpclaw_providers_llamasharp"] = "false",
                ["Modules:sharpclaw_providers_ollama"] = "false",
                ["Modules:sharpclaw_providers_openai_compat"] = "false",
            })
            .Build();
        var databaseOptions = new DatabaseProviderOptions
        {
            Provider = StorageMode.JsonFile,
        };
        databaseOptions.JsonFile.DataDirectory = workspace.DatabaseDirectory;
        databaseOptions.JsonFile.EncryptAtRest = false;

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
                    new { message = "restart me" });
                var body = await response.Content.ReadAsStringAsync();

                response.StatusCode.Should().Be(HttpStatusCode.OK, body);
                body.Should().Contain("test harness response");
            });

        var conversationId = Guid.Parse(workspace.InstancePaths.Manifest.InstanceId);
        IReadOnlyList<string>? history = null;
        await RunProductionHostAsync(
            workspace,
            configuration,
            databaseOptions,
            async app =>
            {
                await using var scope = app.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
                history = await db.ChatMessages
                    .AsNoTracking()
                    .Where(message => message.ChannelId == conversationId)
                    .OrderBy(message => message.CreatedAt)
                    .ThenBy(message => message.Id)
                    .Select(message => message.Content)
                    .ToListAsync();
            });

        history.Should().NotBeNull();
        history!.Should().BeEmpty();
    }

    [Test]
    public void MissingConfiguredProviderFailsBeforeReadiness()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        using var workspace = new TemporaryWorkspace();
        using var moduleSet = PackagedDotNetModuleSet.Load(
            [
                Path.Combine(AppContext.BaseDirectory, "modules"),
                Path.Combine(AppContext.BaseDirectory, "test-modules"),
            ],
            configuration);
        var services = new ServiceCollection();
        RuntimeHostComposition.RegisterServices(
            services,
            configuration,
            workspace.InstancePaths,
            new EncryptionOptions { Key = new byte[32] },
            new DatabaseProviderOptions
            {
                Provider = StorageMode.JsonFile,
            },
            moduleSet.Modules);

        using var provider = services.BuildServiceProvider();
        var exception = FluentActions.Invoking(() =>
            provider.GetRequiredService<RuntimeKernelAdapter>())
            .Should().Throw<InvalidOperationException>();
        exception.Which.Message.Should().Contain("Provider:Key");
        provider.GetRequiredService<RuntimeReadinessState>().IsReady.Should().BeFalse();
    }

    [Test]
    public void DisabledPackagedInProcessModule_IsExcludedBeforeGraphCompilation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Modules:sharpclaw_test_harness_in_process"] = "false",
                ["Modules:sharpclaw_providers_anthropic"] = "false",
                ["Modules:sharpclaw_providers_google"] = "false",
                ["Modules:sharpclaw_providers_llamasharp"] = "false",
                ["Modules:sharpclaw_providers_ollama"] = "false",
                ["Modules:sharpclaw_providers_openai_compat"] = "false",
            })
            .Build();

        using var moduleSet = PackagedDotNetModuleSet.Load(
            [
                Path.Combine(AppContext.BaseDirectory, "modules"),
                Path.Combine(AppContext.BaseDirectory, "test-modules"),
            ],
            configuration);

        moduleSet.Modules.Should().BeEmpty();
    }

    [TestCase("null")]
    [TestCase("0")]
    [TestCase("\"false\"")]
    public void PackagedModuleSet_RejectsNonBooleanEnabledValues(string enabledJson)
    {
        using var moduleRoot = new TemporaryModuleRoot();
        moduleRoot.WriteManifest(
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
              "moduleType": "Unused.Module",
              "enabled": {{enabledJson}}
            }
            """);

        var act = () => PackagedDotNetModuleSet.Load(
            moduleRoot.Path,
            new ConfigurationBuilder().Build());

        act.Should().Throw<JsonException>();
    }

    [Test]
    public void PackagedModuleSet_RejectsDuplicateManifestIdentityBeforeLoad()
    {
        using var moduleRoot = new TemporaryModuleRoot();
        const string manifest = """
            {
              "id": "duplicate-module",
              "displayName": "Duplicate module",
              "version": "0.1.0",
              "toolPrefix": "duplicate",
              "runtime": "dotnet",
              "hostMode": "inprocess",
              "entryAssembly": "unused.dll",
              "moduleType": "Unused.Module",
              "enabled": false
            }
            """;
        moduleRoot.WriteManifest("first", manifest);
        moduleRoot.WriteManifest("second", manifest);

        var act = () => PackagedDotNetModuleSet.Load(
            moduleRoot.Path,
            new ConfigurationBuilder().Build());

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("duplicate-module");
    }

    private static async Task<(HttpStatusCode StatusCode, string Body, string? ContentType)> SendAuthenticatedChatAsync(
        HttpClient client,
        string subject,
        string idempotencyKey)
        => await SendAuthenticatedAsync(client, "/chat", subject, idempotencyKey);

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
        using var response = await client.SendAsync(request);
        return (
            response.StatusCode,
            await response.Content.ReadAsStringAsync(),
            response.Content.Headers.ContentType?.MediaType);
    }

    private static async Task RunProductionHostAsync(
        TemporaryWorkspace workspace,
        IConfiguration configuration,
        DatabaseProviderOptions databaseOptions,
        Func<WebApplication, Task> operation)
    {
        using var moduleSet = PackagedDotNetModuleSet.Load(
            [
                Path.Combine(AppContext.BaseDirectory, "modules"),
                Path.Combine(AppContext.BaseDirectory, "test-modules"),
            ],
            configuration);
        moduleSet.Modules.Should().ContainSingle()
            .Which.Identity.Id.Should().Be("sharpclaw_test_harness_in_process");

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

        await using var app = builder.Build();
        var readiness = app.Services.GetRequiredService<RuntimeReadinessState>();
        var adapter = app.Services.GetRequiredService<RuntimeKernelAdapter>();
        await app.Services.GetRequiredService<RuntimeDatabaseReadiness>().ValidateAsync();
        await adapter.StartAsync("test-host");
        readiness.MarkReady();
        KernelHostEndpoints.Map(app);

        try
        {
            await app.StartAsync();
            await operation(app);
        }
        finally
        {
            readiness.MarkNotReady();
            await adapter.StopAsync();
            await app.StopAsync();
        }
    }

    private static async Task RunNormalProductionHostAsync(
        TemporaryWorkspace workspace,
        IConfiguration configuration,
        DatabaseProviderOptions databaseOptions,
        Func<WebApplication, Task> operation)
    {
        using var moduleSet = PackagedDotNetModuleSet.Load(
            Path.Combine(AppContext.BaseDirectory, "modules"),
            configuration);
        moduleSet.Modules.Select(module => module.Identity.Id)
            .Should().BeEquivalentTo(
                [
                    "sharpclaw_providers_anthropic",
                    "sharpclaw_providers_google",
                    "sharpclaw_providers_llamasharp",
                    "sharpclaw_providers_ollama",
                    "sharpclaw_providers_openai_compat",
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
            moduleSet.Modules);

        await using var app = builder.Build();
        var readiness = app.Services.GetRequiredService<RuntimeReadinessState>();
        var adapter = app.Services.GetRequiredService<RuntimeKernelAdapter>();
        await app.Services.GetRequiredService<RuntimeDatabaseReadiness>().ValidateAsync();
        await adapter.StartAsync("normal-provider-restart-test");
        readiness.MarkReady();
        KernelHostEndpoints.Map(app);

        try
        {
            await app.StartAsync();
            await operation(app);
        }
        finally
        {
            readiness.MarkNotReady();
            await adapter.StopAsync();
            await app.StopAsync();
        }
    }

    private static object ResolveLlamaLocalModelStore(RuntimeKernelAdapter adapter)
    {
        var modules = adapter.Graph.Modules.Modules;
        var moduleIndex = -1;
        for (var index = 0; index < modules.Count; index++)
        {
            if (modules[index].Identity.Id == "sharpclaw_providers_llamasharp")
            {
                moduleIndex = index;
                break;
            }
        }

        if (moduleIndex < 0)
            throw new InvalidOperationException("The LlamaSharp module was not compiled into the graph.");

        var serviceTypes = modules[moduleIndex].ServiceTypes;
        Type? storeType = null;
        for (var index = 0; index < serviceTypes.Count; index++)
        {
            if (serviceTypes[index].FullName ==
                "SharpClaw.Modules.Providers.LlamaSharp.Services.LocalModelStore")
            {
                storeType = serviceTypes[index];
                break;
            }
        }

        if (storeType is null)
            throw new InvalidOperationException("The LlamaSharp LocalModelStore was not registered.");
        return adapter.Graph.GetService(storeType)
            ?? throw new InvalidOperationException("The LlamaSharp LocalModelStore was not composed into the graph.");
    }

    private static async Task InvokeLlamaPlaceholderAsync(object store, Guid modelId)
    {
        var storeType = store.GetType();
        var resolvedFileType = storeType.Assembly.GetType(
            "SharpClaw.Providers.LocalCommon.ResolvedModelFile")
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("SharpClaw.Providers.LocalCommon.ResolvedModelFile"))
                .FirstOrDefault(type => type is not null)
            ?? throw new InvalidOperationException("The LlamaSharp model file contract was not loaded.");
        var resolvedFile = Activator.CreateInstance(
            resolvedFileType,
            "https://example.invalid/model.gguf",
            "model.gguf",
            "Q4_K_M")!;
        var method = storeType.GetMethod("CreateOrReuseDownloadPlaceholderAsync")
            ?? throw new InvalidOperationException("The LlamaSharp LocalModelStore write method was not loaded.");
        var task = (Task)method.Invoke(
            store,
            [modelId, resolvedFile, "https://example.invalid/model.gguf", "model.gguf", CancellationToken.None])!;
        await task;
    }

    private static async Task<object?> InvokeLlamaGetByModelIdAsync(object store, Guid modelId)
    {
        var method = store.GetType().GetMethod("GetByModelIdAsync")
            ?? throw new InvalidOperationException("The LlamaSharp LocalModelStore read method was not loaded.");
        var task = (Task)method.Invoke(store, [modelId, CancellationToken.None])!;
        await task;
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
            await probe.Release.Task.WaitAsync(cancellationToken);
            return await control.ProceedAsync(cancellationToken);
        }
    }

    private sealed class RequestContextProbeModule(RequestContextProbe probe) : ISharpClawModule
    {
        private readonly RequestContextProvider _provider = new();

        public ModuleIdentity Identity { get; } =
            new("request-context-probe", "Request context probe", "context");

        public void Configure(ISharpClawModuleBuilder module)
        {
            module.Services.AddSingleton<IProviderPlugin>(_provider);
            module.Services.AddSingleton(probe);
            module.Services.AddSingleton<RequestContextProbeInterceptor>();
            module.Hooks
                .For(new SharpClawActionKey("runtime.request.receive"))
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

    private sealed class TemporaryModuleRoot : IDisposable
    {
        public TemporaryModuleRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "sharpclaw-module-manifest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void WriteManifest(string moduleDirectory, string json)
        {
            var directory = System.IO.Path.Combine(Path, moduleDirectory);
            Directory.CreateDirectory(directory);
            File.WriteAllText(System.IO.Path.Combine(directory, "module.json"), json);
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
            await app.StartAsync();
            return new FakeOpenAiServer(app);
        }

        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }
}
