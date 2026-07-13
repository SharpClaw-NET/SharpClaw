using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Runtime.BLL.Modules;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Tests.TestHarness;
using SharpClaw.TestFixtures.ExternalModule;
using SharpClaw.Core.Modules;

namespace SharpClaw.Tests.Modules;

[TestFixture]
[NonParallelizable]
public sealed class InProcessModulePerformanceTests
{
    private const int VariantsPerOperation = 63;
    private InProcessPerformanceHarness _harness = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        _harness = await InProcessPerformanceHarness.CreateAsync();
        await _harness.WarmAsync();
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        if (_harness is not null)
            await _harness.DisposeAsync();
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceGate)]
    public void InProcessPerformanceCaseSource_ContainsAtLeast250DistinctCases()
    {
        var cases = AllPerformanceCases();

        cases.Should().HaveCountGreaterThanOrEqualTo(250);
        cases.Select(testCase => testCase.Name).Should().OnlyHaveUniqueItems();
    }

    [TestCaseSource(nameof(PerformanceCases))]
    [Category(HarnessTestCategories.PerformanceGate)]
    public async Task InProcessModuleHotPath_PerformanceCase(InProcessPerformanceCase testCase)
    {
        var measurement = await _harness.MeasureAsync(testCase);

        measurement.ElapsedMs.Should().BeLessThanOrEqualTo(
            testCase.MaxElapsedMs,
            measurement.Describe());
    }

    private static IEnumerable<TestCaseData> PerformanceCases() =>
        AllPerformanceCases()
            .Select(testCase => new TestCaseData(testCase).SetName(testCase.Name));

    private static IReadOnlyList<InProcessPerformanceCase> AllPerformanceCases()
    {
        var cases = new List<InProcessPerformanceCase>(VariantsPerOperation * 4);
        AddCases(cases, InProcessPerformanceOperation.RegistryLookup, "registry_lookup", 50);
        AddCases(cases, InProcessPerformanceOperation.RuntimeScopeCapabilities, "runtime_scope_capabilities", 75);
        AddCases(cases, InProcessPerformanceOperation.DirectToolDispatch, "direct_tool_dispatch", 100);
        AddCases(cases, InProcessPerformanceOperation.ModuleSubmitsChildJob, "module_submits_child_job", 750);
        return cases;
    }

    private static void AddCases(
        List<InProcessPerformanceCase> cases,
        InProcessPerformanceOperation operation,
        string name,
        double maxElapsedMs)
    {
        for (var variant = 0; variant < VariantsPerOperation; variant++)
        {
            cases.Add(new InProcessPerformanceCase(
                $"InProcessModulePerf_{name}_{variant:D3}",
                operation,
                variant,
                maxElapsedMs));
        }
    }

    private sealed class InProcessPerformanceHarness : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly ChatHarnessHost _host;

        private InProcessPerformanceHarness(
            ChatHarnessHost host,
            InProcessModuleHost runtimeHost,
            ISharpClawCoreModule module,
            SeededChat seeded)
        {
            _host = host;
            RuntimeHost = runtimeHost;
            Module = module;
            Seeded = seeded;
        }

        private InProcessModuleHost RuntimeHost { get; }
        private ISharpClawCoreModule Module { get; }
        private SeededChat Seeded { get; }
        private ModuleRegistry Registry => _host.RootServices.GetRequiredService<ModuleRegistry>();

        public static async Task<InProcessPerformanceHarness> CreateAsync()
        {
            var host = ChatHarnessHost.Create(new Dictionary<string, string?>
            {
                ["Modules:DotNetHostingMode"] = "allow-in-process",
            });

            try
            {
                var moduleDir = CreateExternalModuleDirectory();
                var moduleService = host.Services.GetRequiredService<ModuleService>();
                var response = await moduleService.LoadExternalFromAbsolutePathAsync(
                    moduleDir,
                    host.RootServices,
                    CancellationToken.None,
                    persistDisabledEnvEntry: false);

                response.Enabled.Should().BeTrue();

                var registry = host.RootServices.GetRequiredService<ModuleRegistry>();
                var runtimeHost = registry.GetRuntimeHost(InProcessPerformanceFixtureModule.ModuleId)
                    .Should()
                    .BeOfType<InProcessModuleHost>()
                    .Subject;
                var module = registry.GetModule(InProcessPerformanceFixtureModule.ModuleId)
                    .Should()
                    .NotBeNull()
                    .And
                    .BeAssignableTo<ISharpClawCoreModule>()
                    .Subject;
                var seeded = await host.SeedChatAsync(
                    TestHarnessConstants.PlainProviderKey,
                    disableToolSchemas: true);

                return new InProcessPerformanceHarness(host, runtimeHost, module, seeded);
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        public async Task WarmAsync()
        {
            RunRegistryLookup(0);
            await RunRuntimeScopeCapabilitiesAsync(0);
            await ExecuteDirectToolAsync(InProcessPerformanceFixtureModule.NoopTool, 0);
            await ExecuteDirectToolAsync(InProcessPerformanceFixtureModule.SpawnJobTool, 0);
        }

        public async Task<InProcessCaseMeasurement> MeasureAsync(InProcessPerformanceCase testCase)
        {
            var startedAt = Stopwatch.GetTimestamp();
            await RunCaseAsync(testCase);
            var elapsed = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            return new InProcessCaseMeasurement(testCase, elapsed);
        }

        private Task RunCaseAsync(InProcessPerformanceCase testCase)
        {
            return testCase.Operation switch
            {
                InProcessPerformanceOperation.RegistryLookup =>
                    RunRegistryLookupAsync(testCase.Variant),
                InProcessPerformanceOperation.RuntimeScopeCapabilities =>
                    RunRuntimeScopeCapabilitiesAsync(testCase.Variant),
                InProcessPerformanceOperation.DirectToolDispatch =>
                    RunDirectToolDispatchAsync(testCase.Variant),
                InProcessPerformanceOperation.ModuleSubmitsChildJob =>
                    ExecuteDirectToolAsync(InProcessPerformanceFixtureModule.SpawnJobTool, testCase.Variant),
                _ => throw new InvalidOperationException(
                    $"Unsupported in-process performance operation '{testCase.Operation}'."),
            };
        }

        private Task RunRegistryLookupAsync(int variant)
        {
            RunRegistryLookup(variant);
            return Task.CompletedTask;
        }

        private void RunRegistryLookup(int variant)
        {
            var actionKey = variant % 2 == 0
                ? InProcessPerformanceFixtureModule.NoopTool
                : InProcessPerformanceFixtureModule.StorageTool;

            for (var i = 0; i < 1_000; i++)
            {
                Registry.TryResolve(actionKey, out var moduleId, out var toolName).Should().BeTrue();
                moduleId.Should().Be(InProcessPerformanceFixtureModule.ModuleId);
                toolName.Should().Be(actionKey);
                Registry.GetModule(InProcessPerformanceFixtureModule.ModuleId).Should().NotBeNull();
                Registry.GetRuntimeHost(InProcessPerformanceFixtureModule.ModuleId).Should().BeSameAs(RuntimeHost);
                Registry.FindStorageContract(
                    InProcessPerformanceFixtureModule.ModuleId,
                    InProcessPerformanceFixtureModule.StorageName).Should().NotBeNull();
            }
        }

        private async Task RunRuntimeScopeCapabilitiesAsync(int variant)
        {
            for (var i = 0; i < 10; i++)
            {
                using var scope = RuntimeHost.CreateScope();
                var gateway = scope.ServiceProvider.GetRequiredService<IModuleStorageGateway>();
                gateway.ListContracts().Should().ContainSingle(contract =>
                    string.Equals(
                        contract.ModuleId,
                        InProcessPerformanceFixtureModule.ModuleId,
                        StringComparison.Ordinal));

                var core = scope.ServiceProvider.GetRequiredService<ISharpClawDataContext>();
                core.Agents.Take(1).Count().Should().BeGreaterThanOrEqualTo(0);
                scope.ServiceProvider.GetRequiredService<IAgentJobController>().Should().NotBeNull();
                scope.ServiceProvider.GetRequiredService<IAgentJobReader>().Should().NotBeNull();

                var config = scope.ServiceProvider.GetRequiredService<IModuleConfigStore>();
                var key = $"perf-runtime-scope-{i:D2}";
                var value = $"variant-{variant:D3}";
                await config.SetAsync(key, value);
                (await config.GetAsync(key)).Should().Be(value);
            }
        }

        private async Task RunDirectToolDispatchAsync(int variant)
        {
            using var scope = RuntimeHost.CreateScope();
            var restrictedScope = ModuleHostServiceAccess.CreateRestrictedScope(
                scope.ServiceProvider,
                Module.Id);
            var job = CreateJobContext(Guid.NewGuid());

            for (var i = 0; i < 25; i++)
            {
                using var parameters = JsonDocument.Parse(
                    JsonSerializer.Serialize(new { variant = variant * 100 + i }, JsonOptions));
                var result = await Module.ExecuteToolAsync(
                    InProcessPerformanceFixtureModule.NoopTool,
                    parameters.RootElement,
                    job,
                    restrictedScope,
                    CancellationToken.None);

                result.Should().StartWith("noop:");
            }
        }

        private async Task ExecuteDirectToolAsync(string toolName, int variant)
        {
            using var scope = RuntimeHost.CreateScope();
            var restrictedScope = ModuleHostServiceAccess.CreateRestrictedScope(
                scope.ServiceProvider,
                Module.Id);
            using var parameters = JsonDocument.Parse(
                JsonSerializer.Serialize(new { variant }, JsonOptions));
            var result = await Module.ExecuteToolAsync(
                toolName,
                parameters.RootElement,
                CreateJobContext(Guid.NewGuid()),
                restrictedScope,
                CancellationToken.None);

            result.Should().Contain(variant.ToString());
        }

        private AgentJobContext CreateJobContext(Guid jobId) =>
            new(
                jobId,
                Seeded.Agent.Id,
                Seeded.Channel.Id,
                ResourceId: null,
                ActionKey: InProcessPerformanceFixtureModule.NoopTool);

        public async ValueTask DisposeAsync()
        {
            await _host.DisposeAsync();
        }

        private static string CreateExternalModuleDirectory()
        {
            var assemblyPath = typeof(InProcessPerformanceFixtureModule).Assembly.Location;
            var sourceDir = Path.GetDirectoryName(assemblyPath)!;
            var moduleDir = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "inprocess-performance-modules",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(moduleDir);

            foreach (var file in Directory.GetFiles(sourceDir, "*.dll"))
                File.Copy(file, Path.Combine(moduleDir, Path.GetFileName(file)), overwrite: true);

            foreach (var file in Directory.GetFiles(sourceDir, "*.deps.json"))
                File.Copy(file, Path.Combine(moduleDir, Path.GetFileName(file)), overwrite: true);

            File.WriteAllText(
                Path.Combine(moduleDir, "module.json"),
                $$"""
                {
                  "id": "{{InProcessPerformanceFixtureModule.ModuleId}}",
                  "displayName": "Synthetic In-Process Performance",
                  "version": "1.0.0",
                  "toolPrefix": "{{InProcessPerformanceFixtureModule.ToolPrefixValue}}",
                  "runtime": "dotnet",
                  "hostMode": "in-process",
                  "entryAssembly": "{{Path.GetFileName(assemblyPath)}}",
                  "moduleType": "{{typeof(InProcessPerformanceFixtureModule).FullName}}",
                  "minHostVersion": "0.0.0",
                  "executionTimeoutSeconds": 5
                }
                """);

            return moduleDir;
        }
    }
}

public sealed record InProcessPerformanceCase(
    string Name,
    InProcessPerformanceOperation Operation,
    int Variant,
    double MaxElapsedMs)
{
    public override string ToString() => Name;
}

public enum InProcessPerformanceOperation
{
    RegistryLookup,
    RuntimeScopeCapabilities,
    DirectToolDispatch,
    ModuleSubmitsChildJob,
}

public sealed record InProcessCaseMeasurement(
    InProcessPerformanceCase TestCase,
    double ElapsedMs)
{
    public string Describe() =>
        $"{TestCase.Name}: operation={TestCase.Operation}, variant={TestCase.Variant}, " +
        $"elapsedMs={ElapsedMs:F3}, maxElapsedMs={TestCase.MaxElapsedMs:F3}";
}
