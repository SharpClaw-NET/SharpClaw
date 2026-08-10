using System.Net;
using System.Net.Http.Json;
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
    public async Task NormalHostPayload_RestartPreservesPackagedProviderConversation()
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
        history!.Should().ContainInOrder("packaged restart", "normal packaged provider response");
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
    public async Task ProductionJsonColdStore_RestartPreservesDirectChatHistory()
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
        history!.Should().ContainInOrder("restart me", "test harness response");
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
