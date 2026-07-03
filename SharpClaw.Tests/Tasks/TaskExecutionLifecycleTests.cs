using JSONColdStore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Application.API;
using SharpClaw.Core.Clients;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Entities.Core.Tasks;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Contracts.Providers;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Modules.AgentOrchestration;
using SharpClaw.Modules.AgentOrchestration.Models;
using SharpClaw.Modules.AgentOrchestration.ScheduledJobs;
using SharpClaw.Modules.AgentOrchestration.Services;
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
    public async Task StartAsync_EmitTask_PersistsLatestSnapshotAndOutputHistory()
    {
        await using var host = TaskLifecycleHost.Create();
        var svc = host.Services.GetRequiredService<TaskService>();
        var orchestrator = host.Services.GetRequiredService<TaskOrchestrator>();
        var definition = await svc.CreateDefinitionAsync(new CreateTaskDefinitionRequest(EmitSource("emit-history")));
        var created = await svc.CreateInstanceAsync(new StartTaskInstanceRequest(definition.Id, ChannelId: Guid.NewGuid()));

        await orchestrator.StartAsync(created.Id);
        var completed = await host.WaitForStatusAsync(created.Id, TaskInstanceStatus.Completed);
        var outputs = await host.ReadOutputsAsync(created.Id);

        completed.OutputSnapshotJson.Should().Be("task-output");
        outputs.Should().ContainSingle();
        outputs.Single().Data.Should().Be("task-output");
        outputs.Single().Sequence.Should().Be(2);
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

    [Test]
    public async Task ScheduledJobWorker_ProcessDueJobs_LaunchesBoundTaskWithParameters()
    {
        await using var host = ScheduledJobHost.Create();
        var taskDefinitionId = Guid.NewGuid();
        var callerAgentId = Guid.NewGuid();
        var due = DateTimeOffset.UtcNow.AddSeconds(-1);
        await host.Store.CreateAsync(new ScheduledJobDB
        {
            Id = Guid.NewGuid(),
            Name = "due-job",
            TaskDefinitionId = taskDefinitionId,
            CallerAgentId = callerAgentId,
            ParameterValuesJson = """{"Topic":"scheduled"}""",
            NextRunAt = due,
            RepeatInterval = TimeSpan.FromMinutes(5)
        });

        await host.Worker.ProcessDueJobsAsync(CancellationToken.None);

        host.Launcher.Launches.Should().ContainSingle();
        var launch = host.Launcher.Launches.Single();
        launch.TaskDefinitionId.Should().Be(taskDefinitionId);
        launch.CallerAgentId.Should().Be(callerAgentId);
        launch.ParameterValues.Should().ContainKey("Topic").WhoseValue.Should().Be("scheduled");
        launch.ChannelId.Should().BeNull();
        launch.ContextId.Should().BeNull();

        var updated = await host.ReadScheduledJobAsync("due-job");
        updated.Status.Should().Be(ScheduledTaskStatus.Pending);
        updated.RetryCount.Should().Be(0);
        updated.LastError.Should().BeNull();
        updated.LastRunAt.Should().NotBeNull();
        updated.NextRunAt.Should().BeAfter(due);
    }

    [Test]
    public async Task ScheduledJobWorker_SkipMissedFire_DoesNotLaunchTask()
    {
        await using var host = ScheduledJobHost.Create(
            new Dictionary<string, string?>
            {
                ["Scheduler:MissedFireThresholdMinutes"] = "1"
            });
        var missedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await host.Store.CreateAsync(new ScheduledJobDB
        {
            Id = Guid.NewGuid(),
            Name = "missed-job",
            TaskDefinitionId = Guid.NewGuid(),
            NextRunAt = missedAt,
            RepeatInterval = TimeSpan.FromMinutes(5),
            MissedFirePolicy = MissedFirePolicy.Skip
        });

        await host.Worker.ProcessDueJobsAsync(CancellationToken.None);

        host.Launcher.Launches.Should().BeEmpty();
        var updated = await host.ReadScheduledJobAsync("missed-job");
        updated.Status.Should().Be(ScheduledTaskStatus.Pending);
        updated.LastRunAt.Should().BeNull();
        updated.NextRunAt.Should().BeAfter(missedAt);
    }

    [Test]
    public async Task ScheduledJobWorker_FailedLaunch_RequeuesUntilMaxRetriesThenFails()
    {
        await using var host = ScheduledJobHost.Create();
        host.Launcher.ThrowOnLaunch = true;
        var jobId = Guid.NewGuid();
        await host.Store.CreateAsync(new ScheduledJobDB
        {
            Id = jobId,
            Name = "retry-job",
            TaskDefinitionId = Guid.NewGuid(),
            NextRunAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            MaxRetries = 2
        });

        await host.Worker.ProcessDueJobsAsync(CancellationToken.None);
        var firstAttempt = await host.ReadScheduledJobAsync("retry-job");
        firstAttempt.Status.Should().Be(ScheduledTaskStatus.Pending);
        firstAttempt.RetryCount.Should().Be(1);
        firstAttempt.LastError.Should().Contain("forced launch failure");

        await host.Store.UpdateAsync(
            jobId,
            job => job.NextRunAt = DateTimeOffset.UtcNow.AddSeconds(-1));
        await host.Worker.ProcessDueJobsAsync(CancellationToken.None);

        var secondAttempt = await host.ReadScheduledJobAsync("retry-job");
        secondAttempt.Status.Should().Be(ScheduledTaskStatus.Failed);
        secondAttempt.RetryCount.Should().Be(2);
        host.Launcher.Launches.Should().HaveCount(2);
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

    private static string EmitSource(string name) => $$"""
[Task("{{name}}")]
public class EmitTask
{
    public async Task RunAsync(CancellationToken ct)
    {
        await Emit("task-output");
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
        services.AddScoped<ITaskStepExecutorExtension, TaskScriptingStepExecutor>();
        services.AddScoped<ITaskStepExecutorExtension, AgentOrchestrationTaskStepExecutor>();
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

internal sealed class ScheduledJobHost : IAsyncDisposable
{
    private readonly ServiceProvider _root;
    private readonly AsyncServiceScope _scope;
    private readonly string _dataDirectory;

    private ScheduledJobHost(ServiceProvider root, AsyncServiceScope scope, string dataDirectory)
    {
        _root = root;
        _scope = scope;
        _dataDirectory = dataDirectory;
    }

    public ScheduledJobStore Store => _scope.ServiceProvider.GetRequiredService<ScheduledJobStore>();
    public ScheduledJobWorker Worker => _root.GetRequiredService<ScheduledJobWorker>();
    public RecordingTaskInstanceLauncher Launcher => _root.GetRequiredService<RecordingTaskInstanceLauncher>();

    public static ScheduledJobHost Create(IReadOnlyDictionary<string, string?>? settings = null)
    {
        var services = new ServiceCollection();
        var dataDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "jsoncoldstore-scheduled-jobs-" + Guid.NewGuid().ToString("N"));
        var storageOptions = new JsonColdStoreStorageOptions
        {
            DataDirectory = dataDirectory,
            EncryptAtRest = false,
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddDbContext<SharpClawDbContext>(
            options => options.UseJsonColdStoreDatabase(
                storageOptions.DataDirectory,
                store => JsonColdStoreRegistration.ConfigureStore(store, storageOptions, null)));
        services.AddSingleton<IModuleStorageContractProvider>(
            new StaticModuleStorageContractProvider(new AgentOrchestrationModule().GetStorageContracts()));
        services.AddScoped<IModuleStorageGateway, BundledModuleStorageGateway>();
        services.AddScoped<ScheduledJobStore>();
        services.AddSingleton<RecordingTaskInstanceLauncher>();
        services.AddSingleton<ITaskInstanceLauncher>(sp => sp.GetRequiredService<RecordingTaskInstanceLauncher>());
        services.AddSingleton<ScheduledJobWorker>();

        var root = services.BuildServiceProvider();
        using (var initScope = root.CreateScope())
        {
            var db = initScope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
            db.Database.EnsureDeleted();
            db.Database.EnsureCreated();
        }

        return new ScheduledJobHost(root, root.CreateAsyncScope(), dataDirectory);
    }

    public async Task<ScheduledJobDB> ReadScheduledJobAsync(string name)
    {
        var jobs = await Store.ListAsync();
        return jobs.Single(j => j.Name == name);
    }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _root.DisposeAsync();

        try
        {
            if (Directory.Exists(_dataDirectory))
                Directory.Delete(_dataDirectory, recursive: true);
        }
        catch
        {
        }
    }
}

internal sealed class StaticModuleStorageContractProvider(
    IReadOnlyList<ModuleStorageContractDescriptor> contracts) : IModuleStorageContractProvider
{
    public IReadOnlyList<ModuleStorageContractDescriptor> GetStorageContracts() => contracts;

    public ModuleStorageContractDescriptor? FindStorageContract(
        string moduleId,
        string storageName) =>
        contracts.FirstOrDefault(contract =>
            contract.ModuleId == moduleId && contract.StorageName == storageName);
}

internal sealed class RecordingTaskInstanceLauncher : ITaskInstanceLauncher
{
    public List<RecordedLaunch> Launches { get; } = [];
    public bool ThrowOnLaunch { get; set; }

    public Task<Guid> LaunchAsync(
        Guid taskDefinitionId,
        IReadOnlyDictionary<string, string>? parameterValues,
        Guid? callerAgentId,
        Guid? channelId,
        Guid? contextId,
        CancellationToken ct)
    {
        Launches.Add(new RecordedLaunch(
            taskDefinitionId,
            parameterValues is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(parameterValues, StringComparer.Ordinal),
            callerAgentId,
            channelId,
            contextId));

        if (ThrowOnLaunch)
            throw new InvalidOperationException("forced launch failure");

        return Task.FromResult(Guid.NewGuid());
    }
}

internal sealed record RecordedLaunch(
    Guid TaskDefinitionId,
    IReadOnlyDictionary<string, string> ParameterValues,
    Guid? CallerAgentId,
    Guid? ChannelId,
    Guid? ContextId);
