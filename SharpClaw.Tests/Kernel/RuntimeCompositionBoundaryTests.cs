using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Tests.Kernel;

[TestFixture]
public sealed class RuntimeCompositionBoundaryTests
{
    [Test]
    public async Task Discovered_registration_uses_the_host_service_provider_and_lifecycle()
    {
        var provider = new TestProvider();
        var contribution = new TestContribution(provider);
        using var workspace = new TemporaryWorkspace();
        using var services = BuildServices(
            [contribution],
            out var jobs);
        var adapter = new RuntimeKernelAdapter(
            Configuration(),
            services,
            jobs,
            [contribution],
            workspace.Paths,
            new TestProviderFactory(provider));

        adapter.Graph.GetRequiredService<TestDependency>()
            .Should().BeSameAs(contribution.Dependency);
        adapter.Graph.GetRequiredService<IProviderPlugin>()
            .Should().BeSameAs(provider);

        await adapter.StartAsync("test-host");
        await adapter.StopAsync();

        contribution.StartCalls.Should().Be(1);
        contribution.StopCalls.Should().Be(1);
    }

    [Test]
    public void Runtime_production_source_has_no_module_concept()
    {
        var root = FindSourceRoot();
        var files = Directory.EnumerateFiles(
                Path.Combine(root, "SharpClaw.Runtime"),
                "*",
                SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        var violations = files
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => new { path, line, index })
                .Where(value => ContainsModuleTerm(value.line)))
            .Select(value => $"{value.path}:{value.index + 1}:{value.line.Trim()}")
            .ToArray();

        violations.Should().BeEmpty();
    }

    private static ServiceProvider BuildServices(
        IReadOnlyList<ISharpClawModule> registrations,
        out KernelJobsBindings jobs)
    {
        jobs = new KernelJobsBindings();
        var services = TestServiceGraph.Collect(registrations);
        return services.BuildServiceProvider();
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provider:Key"] = "test",
                ["Provider:Model"] = "test-model",
            })
            .Build();

    private static bool ContainsModuleTerm(string text)
    {
        for (var index = 0; index <= text.Length - 6; index++)
        {
            if (!text.AsSpan(index, 6).Equals("module", StringComparison.OrdinalIgnoreCase))
                continue;
            var before = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var afterIndex = index + 6;
            var after = afterIndex == text.Length || !char.IsLetterOrDigit(text[afterIndex]);
            if (before && after)
                return true;
        }
        return false;
    }

    private static string FindSourceRoot()
    {
        var configured = Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var current = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "SharpClaw.Runtime")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("SharpClaw source root was not found.");
    }

    private sealed class TestDependency;

    private sealed class TestContribution(IProviderPlugin provider) :
        ISharpClawModule,
        IServiceLifecycle
    {
        public const string SourceId = "test-contribution";

        public ModuleIdentity Identity { get; } =
            new(SourceId, "Test contribution", "test");

        public TestDependency Dependency { get; } = new();

        public int StartCalls { get; private set; }

        public int StopCalls { get; private set; }

        public void ConfigureServices(IServiceCollection builder)
        {
            builder.AddSingleton(provider);
            builder.AddSingleton(Dependency);
        }

        public ValueTask StartAsync(ServiceStartContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestProviderFactory(IProviderApiClient provider) : IRuntimeProviderClientFactory
    {
        public IProviderApiClient Create(
            IConfiguration configuration,
            IReadOnlyList<IProviderPlugin> plugins) => provider;
    }

    private sealed class TestProvider : IProviderPlugin, IProviderApiClient
    {
        public string ProviderKey => "test";

        public string DisplayName => "Test";

        public bool RequiresEndpoint => false;

        public bool RequiresApiKey => false;

        public IModelCapabilityResolver Capabilities { get; } = new EmptyCapabilities();

        public IReadOnlyList<ProviderCostSeed> CostSeeds => [];

        public IDeviceCodeFlow? DeviceCodeFlow => null;

        public IProviderApiClient CreateClient(ProviderClientOptions options) => this;

        public Task<IReadOnlyList<string>> ListModelIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["test-model"]);

        public Task<ChatCompletionResult> ChatCompletionAsync(
            string model,
            string? systemPrompt,
            IReadOnlyList<ChatCompletionMessage> messages,
            int? maxCompletionTokens = null,
            Dictionary<string, System.Text.Json.JsonElement>? providerParameters = null,
            CompletionParameters? completionParameters = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatCompletionResult
            {
                Content = "reply",
                FinishReason = FinishReason.Stop,
                Usage = new TokenUsage(1, 1),
            });
    }

    private sealed class EmptyCapabilities : IModelCapabilityResolver
    {
        public HashSet<string> Resolve(string modelName) => [];
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "sharpclaw-runtime-composition-" + Guid.NewGuid().ToString("N"));

        public TemporaryWorkspace()
        {
            Paths = new SharpClawInstancePaths(
                SharpClawInstanceKind.Backend,
                _root,
                _root,
                _root);
            Paths.EnsureDirectories();
        }

        public SharpClawInstancePaths Paths { get; }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }
}
