using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Modules;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ModuleJobToolExecutorTests
{
    [Test]
    public async Task ExecuteAsync_WhenToolSucceeds_UsesRestrictedScopeAndRecordsMetrics()
    {
        var module = new JobModule(
            timeoutSeconds: 7,
            ModuleJobCompletionBehavior.RemainExecuting);
        var registry = CreateRegistry(module);
        var metrics = new ModuleMetricsCollector();
        var executor = new ModuleJobToolExecutor(
            metrics,
            NullLogger<ModuleJobToolExecutor>.Instance);
        using var provider = CreateProvider();
        var logs = new List<string>();
        var job = MakeJob(actionKey: "test_run");

        var result = await executor.ExecuteAsync(
            Request(
                job,
                registry,
                provider,
                logs.Add));

        result.ResultData.Should().Be("run:5:test_run");
        result.CompletionBehavior.Should()
            .Be(ModuleJobCompletionBehavior.RemainExecuting);
        result.TimeoutSeconds.Should().Be(7);
        result.StreamingFallbackUsed.Should().BeFalse();
        module.ExecuteCalls.Should().Be(1);
        provider.GetRequiredService<ModuleExecutionContext>()
            .ModuleId
            .Should()
            .Be("test_module");
        logs.Should().ContainSingle()
            .Which.Should()
            .Be("Module dispatch resolved: test_run -> test_module.run (timeout 7s).");

        var snapshot = metrics.GetToolMetrics("test_run");
        snapshot.Should().NotBeNull();
        snapshot!.TotalCalls.Should().Be(1);
        snapshot.SuccessCount.Should().Be(1);
    }

    [Test]
    public async Task ExecuteAsync_WhenStreamingIsUnsupported_FallsBackToNormalExecution()
    {
        var module = new JobModule(
            timeoutSeconds: 7,
            ModuleJobCompletionBehavior.CompleteWhenExecutionReturns)
        {
            ThrowStreamingUnsupported = true
        };
        var registry = CreateRegistry(module);
        var executor = new ModuleJobToolExecutor(
            new ModuleMetricsCollector(),
            NullLogger<ModuleJobToolExecutor>.Instance);
        using var provider = CreateProvider();

        var result = await executor.ExecuteAsync(
            Request(
                MakeJob(),
                registry,
                provider,
                _ => { },
                ex => ex is StreamingUnsupportedException));

        result.ResultData.Should().Be("run:5:test_run");
        result.StreamingFallbackUsed.Should().BeTrue();
        module.ExecuteCalls.Should().Be(1);
    }

    [Test]
    public async Task ExecuteAsync_WhenModuleFails_SanitizesUserVisibleFailureAndRecordsMetrics()
    {
        var module = new JobModule(
            timeoutSeconds: 7,
            ModuleJobCompletionBehavior.CompleteWhenExecutionReturns)
        {
            FailureMessage =
                @"boom C:\secret\file.txt Server=localhost 127.0.0.1 " +
                "d2719a34-0bf8-44f1-86d1-11608b5e2051"
        };
        var registry = CreateRegistry(module);
        var metrics = new ModuleMetricsCollector();
        var executor = new ModuleJobToolExecutor(
            metrics,
            NullLogger<ModuleJobToolExecutor>.Instance);
        using var provider = CreateProvider();

        var act = async () => await executor.ExecuteAsync(
            Request(
                MakeJob(),
                registry,
                provider,
                _ => { }));

        var failure = await act.Should()
            .ThrowAsync<InvalidOperationException>();
        failure.Which.Message.Should()
            .StartWith("[ApplicationException] Module tool 'test_module.run' failed:");
        failure.Which.Message.Should().Contain("[path]");
        failure.Which.Message.Should().Contain("[connection]");
        failure.Which.Message.Should().Contain("[ip]");
        failure.Which.Message.Should().Contain("[id]");
        failure.Which.Message.Should().NotContain("C:\\secret");
        failure.Which.Message.Should().NotContain("Server=localhost");
        failure.Which.Message.Should().NotContain("127.0.0.1");
        failure.Which.Message.Should().NotContain("d2719a34");

        var snapshot = metrics.GetToolMetrics("test_run");
        snapshot.Should().NotBeNull();
        snapshot!.FailureCount.Should().Be(1);
    }

    [Test]
    public void ResolveTimeoutSeconds_WhenToolHasNoOverride_UsesManifestDefault()
    {
        var module = new JobModule(
            timeoutSeconds: null,
            ModuleJobCompletionBehavior.CompleteWhenExecutionReturns);
        var registry = CreateRegistry(module);
        registry.CacheManifest(
            "test_module",
            new ModuleManifest(
                "test_module",
                "Test Module",
                "1.0.0",
                "test",
                "Test.dll",
                "0.0.0",
                ExecutionTimeoutSeconds: 11));

        ModuleJobToolExecutor.ResolveTimeoutSeconds(
                registry,
                "test_module",
                "run")
            .Should()
            .Be(11);
    }

    private static ModuleJobToolExecutionRequest Request(
        AgentJobDB job,
        ModuleRegistry registry,
        ServiceProvider provider,
        Action<string> addInfoLog,
        Func<Exception, bool>? isStreamingNotSupported = null) =>
        new(
            job,
            new ModuleToolExecutionPlan(
                "test_module",
                "run",
                Json("""{"value":5}"""),
                ResolvedFromActionKey: true),
            registry,
            provider.CreateScope,
            [typeof(BlockedService)],
            addInfoLog,
            isStreamingNotSupported);

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<AllowedService>();
        services.AddSingleton<BlockedService>();
        services.AddSingleton<ModuleExecutionContext>();
        return services.BuildServiceProvider();
    }

    private static ModuleRegistry CreateRegistry(JobModule module)
    {
        var registry = new ModuleRegistry();
        registry.Register(module);
        return registry;
    }

    private static AgentJobDB MakeJob(string actionKey = "test_run") => new()
    {
        Id = Guid.NewGuid(),
        ChannelId = Guid.NewGuid(),
        AgentId = Guid.NewGuid(),
        ResourceId = Guid.NewGuid(),
        ActionKey = actionKey,
        Status = AgentJobStatus.Executing,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed class JobModule(
        int? timeoutSeconds,
        ModuleJobCompletionBehavior behavior)
        : ISharpClawCoreModule
    {
        public int ExecuteCalls { get; private set; }
        public bool ThrowStreamingUnsupported { get; init; }
        public string? FailureMessage { get; init; }
        public string Id => "test_module";
        public string DisplayName => "Test Module";
        public string ToolPrefix => "test";

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() =>
        [
            new(
                "run",
                "Run",
                Json("""{"type":"object"}"""),
                new ModuleToolPermission(false, null),
                timeoutSeconds)
        ];

        public ModuleJobCompletionBehavior GetJobCompletionBehavior(
            string toolName,
            JsonElement parameters,
            AgentJobContext job) =>
            behavior;

        public Task<string> ExecuteToolAsync(
            string toolName,
            JsonElement parameters,
            AgentJobContext job,
            IServiceProvider scopedServices,
            CancellationToken ct)
        {
            ExecuteCalls++;

            if (FailureMessage is not null)
                throw new ApplicationException(FailureMessage);

            toolName.Should().Be("run");
            scopedServices.GetRequiredService<AllowedService>()
                .Should()
                .NotBeNull();
            var blocked = () => scopedServices.GetRequiredService<BlockedService>();
            blocked.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*blocked service*BlockedService*");

            var value = parameters.GetProperty("value").GetInt32();
            return Task.FromResult($"run:{value}:{job.ActionKey}");
        }

        public IAsyncEnumerable<string>? ExecuteToolStreamingAsync(
            string toolName,
            JsonElement parameters,
            AgentJobContext job,
            IServiceProvider scopedServices,
            CancellationToken ct) =>
            ThrowStreamingUnsupported
                ? ThrowStreamingUnsupportedAsync(ct)
                : null;
    }

    private static async IAsyncEnumerable<string> ThrowStreamingUnsupportedAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow == DateTimeOffset.MinValue)
            yield return "";

        throw new StreamingUnsupportedException();
    }

    private sealed class StreamingUnsupportedException : Exception;

    private sealed class AllowedService;

    private sealed class BlockedService;
}
