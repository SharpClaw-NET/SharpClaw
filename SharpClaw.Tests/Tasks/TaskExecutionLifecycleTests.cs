using JSONColdStore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Runtime.Host;
using SharpClaw.Core.Clients;
using SharpClaw.Runtime.BLL.Modules;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Entities.Core.Tasks;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Contracts.Providers;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Core.Modules;
using SharpClaw.Core.Tasks.Administration;
using SharpClaw.Core.Tasks.Preflight;
using SharpClaw.Core.Tasks.Runtime;

namespace SharpClaw.Tests.Tasks;

[TestFixture]
public sealed class TaskExecutionLifecycleTests
{
    [Test]
    public async Task CreateInstance_PersistsQueuedInstanceWithCallerChannelContextAndParameters()
    {
        await using var host = TaskLifecycleHost.Create();
        var svc = host.Services.GetRequiredService<TaskService>();
        var definition = await svc.CreateDefinitionAsync(new CreateTaskDefinitionRequest(LogOnlySource("queued-task")));
        var channelId = Guid.NewGuid();
        var contextId = Guid.NewGuid();
        var callerUserId = Guid.NewGuid();
        var callerAgentId = Guid.NewGuid();

        var created = await svc.CreateInstanceAsync(
            new StartTaskInstanceRequest(
                definition.Id,
                ChannelId: channelId,
                ParameterValues: new Dictionary<string, string>
                {
                    ["Topic"] = "phase-five"
                },
                ContextId: contextId),
            callerUserId,
            callerAgentId);

        created.Status.Should().Be(TaskInstanceStatus.Queued);
        created.ChannelId.Should().Be(channelId);
        created.ContextId.Should().Be(contextId);

        var row = await host.Db.TaskInstances.SingleAsync(i => i.Id == created.Id);
        row.CallerUserId.Should().Be(callerUserId);
        row.CallerAgentId.Should().Be(callerAgentId);
        row.ParameterValuesJson.Should().Contain("phase-five");
    }

    [Test]
    public async Task StartAsync_LogOnlyTask_CompletesAndPersistsLifecycleLogs()
    {
        await using var host = TaskLifecycleHost.Create();
        var svc = host.Services.GetRequiredService<TaskService>();
        var orchestrator = host.Services.GetRequiredService<TaskOrchestrator>();
        var definition = await svc.CreateDefinitionAsync(new CreateTaskDefinitionRequest(LogOnlySource("log-lifecycle")));
        var created = await svc.CreateInstanceAsync(new StartTaskInstanceRequest(definition.Id, ChannelId: Guid.NewGuid()));

        await orchestrator.StartAsync(created.Id);
        var completed = await host.WaitForStatusAsync(created.Id, TaskInstanceStatus.Completed);

        completed.StartedAt.Should().NotBeNull();
        completed.CompletedAt.Should().NotBeNull();
        completed.Logs.Select(l => l.Message).Should().Contain("Task started.");
        completed.Logs.Select(l => l.Message).Should().Contain("task-body-log");
        completed.Logs.Select(l => l.Message).Should().Contain("Task Completed.");
    }

    [Test]
    public async Task StartAsync_CompilationFailure_SavesFailureWithoutRegisteringRuntime()
    {
        await using var host = TaskLifecycleHost.Create();
        var orchestrator = host.Services.GetRequiredService<TaskOrchestrator>();
        var registry = host.Services.GetRequiredService<TaskRuntimeRegistry>();
        var definition = new TaskDefinitionDB
        {
            Id = Guid.NewGuid(),
            Name = "compile-failure",
            SourceText = InvalidSource("compile-failure")
        };
        var instance = new TaskInstanceDB
        {
            Id = Guid.NewGuid(),
            TaskDefinitionId = definition.Id,
            Status = TaskInstanceStatus.Queued
        };
        host.Db.TaskDefinitions.Add(definition);
        host.Db.TaskInstances.Add(instance);
        await host.Db.SaveChangesAsync();

        await orchestrator.StartAsync(instance.Id);

        var failed = await host.WaitForStatusAsync(
            instance.Id,
            TaskInstanceStatus.Failed);
        failed.ErrorMessage.Should().StartWith("Compilation failed: ");
        failed.Logs.Select(l => l.Message).Should().NotContain("Task started.");
        registry.ActiveCount.Should().Be(0);
        orchestrator.GetOutputReader(instance.Id).Should().BeNull();
    }

    [Test]
    public async Task StartAsync_WaitingTask_EmitsStartedEventAfterRunningTransition()
    {
        await using var host = TaskLifecycleHost.Create();
        var svc = host.Services.GetRequiredService<TaskService>();
        var orchestrator = host.Services.GetRequiredService<TaskOrchestrator>();
        var definition = await svc.CreateDefinitionAsync(
            new CreateTaskDefinitionRequest(WaitSource("start-event-order")));
        var created = await svc.CreateInstanceAsync(
            new StartTaskInstanceRequest(
                definition.Id,
                ChannelId: Guid.NewGuid()));

        await orchestrator.StartAsync(created.Id);

        var running = await host.WaitForStatusAsync(
            created.Id,
            TaskInstanceStatus.Running);
        var reader = orchestrator.GetOutputReader(created.Id);
        reader.Should().NotBeNull();
        reader!.TryRead(out var started).Should().BeTrue();
        started.Type.Should().Be(TaskOutputEventType.StatusChange);
        started.Data.Should().Be("Running");
        running.Logs.Select(l => l.Message).Should().Contain("Task started.");

        await orchestrator.StopAsync(created.Id);
        await host.WaitForStatusAsync(created.Id, TaskInstanceStatus.Cancelled);
    }

    [Test]
    public async Task PauseResumeAndStop_WaitingTask_UpdatesStatusAndEndsCancelled()
    {
        await using var host = TaskLifecycleHost.Create();
        var svc = host.Services.GetRequiredService<TaskService>();
        var orchestrator = host.Services.GetRequiredService<TaskOrchestrator>();
        var definition = await svc.CreateDefinitionAsync(new CreateTaskDefinitionRequest(WaitSource("wait-lifecycle")));
        var created = await svc.CreateInstanceAsync(new StartTaskInstanceRequest(definition.Id, ChannelId: Guid.NewGuid()));

        await orchestrator.StartAsync(created.Id);
        await host.WaitForStatusAsync(created.Id, TaskInstanceStatus.Running);

        (await orchestrator.PauseAsync(created.Id)).Should().BeTrue();
        await host.WaitForStatusAsync(created.Id, TaskInstanceStatus.Paused);

        (await orchestrator.ResumeAsync(created.Id)).Should().BeTrue();
        await host.WaitForStatusAsync(created.Id, TaskInstanceStatus.Running);

        await orchestrator.StopAsync(created.Id);
        var cancelled = await host.WaitForStatusAsync(created.Id, TaskInstanceStatus.Cancelled);
        cancelled = await host.WaitForLogAsync(created.Id, "Task Cancelled.");

        cancelled.CompletedAt.Should().NotBeNull();
        cancelled.Logs.Select(l => l.Message).Should().Contain("Task paused.");
        cancelled.Logs.Select(l => l.Message).Should().Contain("Task resumed.");
        cancelled.Logs.Select(l => l.Message).Should().Contain("Task Cancelled.");
    }

    [Test]
    public async Task CancelQueuedInstance_PreventsLaterOrchestratorStart()
    {
        await using var host = TaskLifecycleHost.Create();
        var svc = host.Services.GetRequiredService<TaskService>();
        var orchestrator = host.Services.GetRequiredService<TaskOrchestrator>();
        var definition = await svc.CreateDefinitionAsync(new CreateTaskDefinitionRequest(LogOnlySource("cancel-queued")));
        var created = await svc.CreateInstanceAsync(new StartTaskInstanceRequest(definition.Id, ChannelId: Guid.NewGuid()));

        (await svc.CancelInstanceAsync(created.Id)).Should().BeTrue();
        var start = async () => await orchestrator.StartAsync(created.Id);

        await start.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*expected Queued*");
    }

    [Test]
    public async Task RuntimeHostStartupRecovery_MarksStaleRunningAndPausedInstancesFailed()
    {
        await using var host = TaskLifecycleHost.Create();
        var definition = new TaskDefinitionDB
        {
            Id = Guid.NewGuid(),
            Name = "stale-definition",
            SourceText = LogOnlySource("stale-definition")
        };
        var running = new TaskInstanceDB
        {
            Id = Guid.NewGuid(),
            TaskDefinitionId = definition.Id,
            TaskDefinition = definition,
            Status = TaskInstanceStatus.Running
        };
        var paused = new TaskInstanceDB
        {
            Id = Guid.NewGuid(),
            TaskDefinitionId = definition.Id,
            TaskDefinition = definition,
            Status = TaskInstanceStatus.Paused
        };
        host.Db.TaskDefinitions.Add(definition);
        host.Db.TaskInstances.AddRange(running, paused);
        await host.Db.SaveChangesAsync();

        var runtimeHost = host.Services.GetRequiredService<TaskRuntimeHost>();
        using var cts = new CancellationTokenSource();
        await runtimeHost.StartAsync(cts.Token);
        await runtimeHost.RecoveryComplete.WaitAsync(TimeSpan.FromSeconds(2));
        await runtimeHost.StopAsync(CancellationToken.None);

        var recovered = await host.ReadInstancesAsync();
        recovered.Should().OnlyContain(i => i.Status == TaskInstanceStatus.Failed);
        recovered.Should().OnlyContain(i => i.ErrorMessage!.Contains("Manual restart required."));
    }

    private static string LogOnlySource(string name) => $$"""
[Task("{{name}}")]
public class LogOnlyTask
{
    public string Topic { get; set; } = "default";

    public async Task RunAsync(CancellationToken ct)
    {
        Log("task-body-log");
    }
}
""";

    private static string WaitSource(string name) => $$"""
[Task("{{name}}")]
public class WaitTask
{
    public async Task RunAsync(CancellationToken ct)
    {
        Log("before-wait");
        await WaitUntilStopped();
    }
}
""";

    private static string InvalidSource(string name) => $$"""
[Task("{{name}}")]
public class InvalidTask
{
    public async Task NotTheEntryPoint(CancellationToken ct)
    {
    }
}
""";
}

internal sealed class TaskLifecycleHost : IAsyncDisposable
{
    private readonly ServiceProvider _root;
    private readonly AsyncServiceScope _scope;

    private TaskLifecycleHost(ServiceProvider root, AsyncServiceScope scope)
    {
        _root = root;
        _scope = scope;
    }

    public IServiceProvider Services => _scope.ServiceProvider;
    public SharpClawDbContext Db => Services.GetRequiredService<SharpClawDbContext>();

    public static TaskLifecycleHost Create()
    {
        var services = new ServiceCollection();
        var databaseRoot = new InMemoryDatabaseRoot();
        var databaseName = "SharpClawTaskLifecycle_" + Guid.NewGuid().ToString("N");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddDbContext<SharpClawDbContext>(options => options.UseInMemoryDatabase(databaseName, databaseRoot));
        services.AddSingleton<ModuleRegistry>();
        services.AddSingleton<ProviderApiClientFactory>();
        services.AddSingleton<TaskPreflightEngine>();
        services.AddSingleton<TaskAdministrationWorkflowEngine>();
        services.AddScoped<IPersistenceEntityResolver, EfPersistenceEntityResolver>();
        services.AddScoped<EfTaskPreflightHost>();
        services.AddScoped<TaskPreflightChecker>();
        services.AddScoped<EfTaskAdministrationHost>();
        services.AddScoped<TaskService>();
        services.AddScoped<ITaskAuthoring>(sp => sp.GetRequiredService<TaskService>());
        services.AddScoped<TaskStartupPreparationEngine>();
        services.AddScoped<TaskPlanExecutionEngine>();
        services.AddScoped<TaskOrchestrator>();
        services.AddScoped<ITaskInstanceLauncher, TaskInstanceLauncher>();
        services.AddSingleton<TaskRuntimeRegistry>();
        services.AddSingleton<TaskRuntimeHost>();

        var root = services.BuildServiceProvider();
        return new TaskLifecycleHost(root, root.CreateAsyncScope());
    }

    public async Task<TaskInstanceResponse> WaitForStatusAsync(
        Guid instanceId,
        TaskInstanceStatus expected,
        int timeoutMs = 3000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        TaskInstanceResponse? latest = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var scope = _root.CreateAsyncScope();
            latest = await scope.ServiceProvider
                .GetRequiredService<TaskService>()
                .GetInstanceAsync(instanceId);

            if (latest?.Status == expected)
                return latest;

            await Task.Delay(25);
        }

        latest.Should().NotBeNull();
        latest!.Status.Should().Be(expected);
        return latest;
    }

    public async Task<IReadOnlyList<TaskOutputEntryResponse>> ReadOutputsAsync(Guid instanceId)
    {
        await using var scope = _root.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<TaskService>()
            .GetOutputsAsync(instanceId);
    }

    public async Task<TaskInstanceResponse> WaitForLogAsync(
        Guid instanceId,
        string expectedMessage,
        int timeoutMs = 3000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        TaskInstanceResponse? latest = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var scope = _root.CreateAsyncScope();
            latest = await scope.ServiceProvider
                .GetRequiredService<TaskService>()
                .GetInstanceAsync(instanceId);

            if (latest?.Logs.Any(l => l.Message == expectedMessage) == true)
                return latest;

            await Task.Delay(25);
        }

        latest.Should().NotBeNull();
        latest!.Logs.Select(l => l.Message).Should().Contain(expectedMessage);
        return latest;
    }

    public async Task<IReadOnlyList<TaskInstanceDB>> ReadInstancesAsync()
    {
        await using var scope = _root.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<SharpClawDbContext>()
            .TaskInstances
            .AsNoTracking()
            .OrderBy(i => i.Id)
            .ToListAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _root.DisposeAsync();
    }
}
