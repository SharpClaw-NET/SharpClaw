using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Application.Services;
using SharpClaw.Application.Infrastructure.Models.Tasks;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Infrastructure.Persistence.JSON;
using SharpClaw.Utils.Security;

namespace SharpClaw.Tests.Tasks;

/// <summary>
/// Phase 3 tests for <see cref="TaskRuntimeHost"/>: startup recovery, entry
/// registration, and operational delegates.
/// </summary>
[TestFixture]
public class TaskRuntimeHostTests
{
    private string _dataDir = null!;
    private SharpClawDbContext _db = null!;
    private TaskService _taskService = null!;
    private ColdEntityStore _coldStore = null!;
    private TaskRuntimeHost _host = null!;
    private IServiceProvider _serviceProvider = null!;

    [SetUp]
    public void SetUp()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"trhtest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);

        var dbOptions = new DbContextOptionsBuilder<SharpClawDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        _db = new SharpClawDbContext(dbOptions);

        var fileOptions = new JsonFileOptions { DataDirectory = _dataDir, EncryptAtRest = false };
        _coldStore = new ColdEntityStore(
            new PhysicalPersistenceFileSystem(),
            fileOptions,
            new EncryptionOptions { Key = ApiKeyEncryptor.GenerateKey() },
            NullLogger<ColdEntityStore>.Instance);

        _taskService = new TaskService(_db, _coldStore);

        // Build a minimal service provider so the host can resolve TaskService
        // and SharpClawDbContext from a DI scope.
        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton(_coldStore);
        services.AddScoped<TaskService>();
        _serviceProvider = services.BuildServiceProvider();

        _host = new TaskRuntimeHost(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TaskRuntimeHost>.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _host.StopAsync(CancellationToken.None);
        _db.Dispose();
        (_serviceProvider as IDisposable)?.Dispose();
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    // ─────────────────────────────────────────────────────────────
    // Startup recovery
    // ─────────────────────────────────────────────────────────────

    [Test]
    public async Task StartupRecovery_RunningInstance_IsMarkedFailed()
    {
        var (definition, instance) = await CreateInstanceWithStatusAsync(TaskInstanceStatus.Running);

        await RunRecoveryAsync();

        var updated = await _db.TaskInstances.FindAsync(instance.Id);
        updated!.Status.Should().Be(TaskInstanceStatus.Failed);
        updated.ErrorMessage.Should().Contain("restarted");
        updated.CompletedAt.Should().NotBeNull();
    }

    [Test]
    public async Task StartupRecovery_PausedInstance_IsMarkedFailed()
    {
        var (_, instance) = await CreateInstanceWithStatusAsync(TaskInstanceStatus.Paused);

        await RunRecoveryAsync();

        var updated = await _db.TaskInstances.FindAsync(instance.Id);
        updated!.Status.Should().Be(TaskInstanceStatus.Failed);
        updated.ErrorMessage.Should().Contain("Paused");
    }

    [Test]
    public async Task StartupRecovery_QueuedInstance_IsNotTouched()
    {
        var (_, instance) = await CreateInstanceWithStatusAsync(TaskInstanceStatus.Queued);

        await RunRecoveryAsync();

        var updated = await _db.TaskInstances.FindAsync(instance.Id);
        updated!.Status.Should().Be(TaskInstanceStatus.Queued);
        updated.ErrorMessage.Should().BeNull();
    }

    [Test]
    public async Task StartupRecovery_CompletedInstance_IsNotTouched()
    {
        var (_, instance) = await CreateInstanceWithStatusAsync(TaskInstanceStatus.Completed);

        await RunRecoveryAsync();

        var updated = await _db.TaskInstances.FindAsync(instance.Id);
        updated!.Status.Should().Be(TaskInstanceStatus.Completed);
    }

    [Test]
    public async Task StartupRecovery_MultipleStaleInstances_AllMarkedFailed()
    {
        var (_, running1) = await CreateInstanceWithStatusAsync(TaskInstanceStatus.Running);
        var (_, running2) = await CreateInstanceWithStatusAsync(TaskInstanceStatus.Running);
        var (_, paused) = await CreateInstanceWithStatusAsync(TaskInstanceStatus.Paused);
        var (_, queued) = await CreateInstanceWithStatusAsync(TaskInstanceStatus.Queued);

        await RunRecoveryAsync();

        (await _db.TaskInstances.FindAsync(running1.Id))!.Status.Should().Be(TaskInstanceStatus.Failed);
        (await _db.TaskInstances.FindAsync(running2.Id))!.Status.Should().Be(TaskInstanceStatus.Failed);
        (await _db.TaskInstances.FindAsync(paused.Id))!.Status.Should().Be(TaskInstanceStatus.Failed);
        (await _db.TaskInstances.FindAsync(queued.Id))!.Status.Should().Be(TaskInstanceStatus.Queued);
    }

    [Test]
    public async Task StartupRecovery_RunningInstance_AppendsRecoveryLog()
    {
        var (_, instance) = await CreateInstanceWithStatusAsync(TaskInstanceStatus.Running);

        await RunRecoveryAsync();

        var log = await _db.TaskExecutionLogs
            .Where(l => l.TaskInstanceId == instance.Id)
            .FirstOrDefaultAsync();

        log.Should().NotBeNull();
        log!.Message.Should().Contain("Recovery");
        log.Level.Should().Be("Recovery");
    }

    [Test]
    public async Task StartupRecovery_NoStaleInstances_NoChanges()
    {
        var (_, completed) = await CreateInstanceWithStatusAsync(TaskInstanceStatus.Completed);
        var (_, failed) = await CreateInstanceWithStatusAsync(TaskInstanceStatus.Failed);

        await RunRecoveryAsync();

        (await _db.TaskInstances.FindAsync(completed.Id))!.Status.Should().Be(TaskInstanceStatus.Completed);
        (await _db.TaskInstances.FindAsync(failed.Id))!.Status.Should().Be(TaskInstanceStatus.Failed);
    }

    // ─────────────────────────────────────────────────────────────
    // Entry registration
    // ─────────────────────────────────────────────────────────────

    [Test]
    public void Register_NewInstance_OutputReaderIsAvailable()
    {
        var instanceId = Guid.NewGuid();

        var runtime = _host.Register(instanceId, CancellationToken.None);

        _host.IsRunning(instanceId).Should().BeTrue();
        _host.GetOutputReader(instanceId).Should().NotBeNull();
        runtime.Should().NotBeNull();
    }

    [Test]
    public void Unregister_AfterRegister_OutputReaderReturnsNull()
    {
        var instanceId = Guid.NewGuid();
        _host.Register(instanceId, CancellationToken.None);

        _host.Unregister(instanceId);

        _host.IsRunning(instanceId).Should().BeFalse();
        _host.GetOutputReader(instanceId).Should().BeNull();
    }

    [Test]
    public void Register_CancellationToken_IsLinkedToHostToken()
    {
        var instanceId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();

        var runtime = _host.Register(instanceId, cts.Token);

        runtime.CancellationToken.IsCancellationRequested.Should().BeFalse();
        cts.Cancel();
        runtime.CancellationToken.IsCancellationRequested.Should().BeTrue();
    }

    // ─────────────────────────────────────────────────────────────
    // Pause / resume via host
    // ─────────────────────────────────────────────────────────────

    [Test]
    public async Task PauseAsync_RunningInstance_ReturnsTrueAndPersistsPaused()
    {
        var (_, instance) = await CreateInstanceWithStatusAsync(TaskInstanceStatus.Running);
        _host.Register(instance.Id, CancellationToken.None);

        var result = await _host.PauseAsync(instance.Id);

        result.Should().BeTrue();
        var updated = await _db.TaskInstances.FindAsync(instance.Id);
        updated!.Status.Should().Be(TaskInstanceStatus.Paused);
    }

    [Test]
    public async Task PauseAsync_NoEntry_ReturnsFalse()
    {
        var result = await _host.PauseAsync(Guid.NewGuid());
        result.Should().BeFalse();
    }

    [Test]
    public async Task ResumeAsync_PausedInstance_ReturnsTrueAndPersistsRunning()
    {
        var (_, instance) = await CreateInstanceWithStatusAsync(TaskInstanceStatus.Running);
        _host.Register(instance.Id, CancellationToken.None);
        await _host.PauseAsync(instance.Id);

        var result = await _host.ResumeAsync(instance.Id);

        result.Should().BeTrue();
        var updated = await _db.TaskInstances.FindAsync(instance.Id);
        updated!.Status.Should().Be(TaskInstanceStatus.Running);
    }

    [Test]
    public async Task ResumeAsync_NoEntry_ReturnsFalse()
    {
        var result = await _host.ResumeAsync(Guid.NewGuid());
        result.Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────
    // Output event streaming
    // ─────────────────────────────────────────────────────────────

    [Test]
    public async Task WriteEventAsync_WritesToOutputChannel()
    {
        var instanceId = Guid.NewGuid();
        _host.Register(instanceId, CancellationToken.None);
        var reader = _host.GetOutputReader(instanceId)!;

        await _host.WriteEventAsync(instanceId, SharpClaw.Contracts.DTOs.Tasks.TaskOutputEventType.Log, "hello");

        reader.TryRead(out var evt).Should().BeTrue();
        evt!.Type.Should().Be(SharpClaw.Contracts.DTOs.Tasks.TaskOutputEventType.Log);
        evt.Data.Should().Be("hello");
    }

    [Test]
    public async Task WriteEventAsync_AssignsMonotonicallyIncreasingSequence()
    {
        var instanceId = Guid.NewGuid();
        _host.Register(instanceId, CancellationToken.None);
        var reader = _host.GetOutputReader(instanceId)!;

        await _host.WriteEventAsync(instanceId, SharpClaw.Contracts.DTOs.Tasks.TaskOutputEventType.Log, "a");
        await _host.WriteEventAsync(instanceId, SharpClaw.Contracts.DTOs.Tasks.TaskOutputEventType.Log, "b");

        reader.TryRead(out var first).Should().BeTrue();
        reader.TryRead(out var second).Should().BeTrue();
        second!.Sequence.Should().BeGreaterThan(first!.Sequence);
    }

    [Test]
    public void Unregister_CompletesOutputChannel()
    {
        var instanceId = Guid.NewGuid();
        _host.Register(instanceId, CancellationToken.None);
        var reader = _host.GetOutputReader(instanceId)!;

        _host.Unregister(instanceId);

        reader.Completion.IsCompleted.Should().BeTrue();
    }

    // ─────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────

    private async Task RunRecoveryAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // StartAsync fires ExecuteAsync in the background; we must await
        // RecoveryComplete to know recovery has actually finished before asserting.
        await _host.StartAsync(cts.Token);
        await _host.RecoveryComplete.WaitAsync(cts.Token);
    }

    private async Task<(TaskDefinitionDB, TaskInstanceDB)> CreateInstanceWithStatusAsync(
        TaskInstanceStatus status)
    {
        var definition = new TaskDefinitionDB
        {
            Name = $"Def-{Guid.NewGuid():N}",
            SourceText = "[Task(\"T\")] public class T { public async Task RunAsync(CancellationToken ct) {} }",
            IsActive = true,
            ParametersJson = "[]"
        };
        _db.TaskDefinitions.Add(definition);

        var instance = new TaskInstanceDB
        {
            TaskDefinitionId = definition.Id,
            Status = status,
            StartedAt = status is TaskInstanceStatus.Running or TaskInstanceStatus.Paused
                ? DateTimeOffset.UtcNow.AddMinutes(-5)
                : null
        };
        _db.TaskInstances.Add(instance);
        await _db.SaveChangesAsync();
        return (definition, instance);
    }
}
