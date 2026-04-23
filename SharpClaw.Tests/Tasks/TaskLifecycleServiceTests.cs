using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Application.Services;
using SharpClaw.Application.Infrastructure.Models.Tasks;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Infrastructure.Persistence.JSON;
using SharpClaw.Utils.Security;

namespace SharpClaw.Tests.Tasks;

[TestFixture]
public class TaskLifecycleServiceTests
{
    private string _dataDir = null!;
    private SharpClawDbContext _db = null!;
    private TaskService _service = null!;
    private ColdEntityStore _coldStore = null!;

    [SetUp]
    public void SetUp()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"tasksvc_{Guid.NewGuid():N}");
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

        _service = new TaskService(_db, _coldStore);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    [Test]
    public async Task CreateInstanceAsync_StartImmediatelyFlag_PersistsQueuedInstanceUntilOrchestratorStarts()
    {
        var definition = await CreateDefinitionAsync();

        var response = await _service.CreateInstanceAsync(new StartTaskInstanceRequest(definition.Id, StartImmediately: true));

        response.Status.Should().Be(TaskInstanceStatus.Queued);
        var entity = await _db.TaskInstances.FindAsync(response.Id);
        entity.Should().NotBeNull();
        entity!.Status.Should().Be(TaskInstanceStatus.Queued);
    }

    [Test]
    public async Task PauseResumeAndStartRunningTransitions_RespectLifecycleRules()
    {
        var instance = await CreateQueuedInstanceAsync();

        (await _service.PauseInstanceAsync(instance.Id)).Should().BeFalse();
        (await _service.TryMarkInstanceRunningAsync(instance.Id)).Should().BeTrue();
        (await _service.PauseInstanceAsync(instance.Id)).Should().BeTrue();
        (await _service.ResumeInstanceAsync(instance.Id)).Should().BeTrue();

        var updated = await _db.TaskInstances.FindAsync(instance.Id);
        updated.Should().NotBeNull();
        updated!.Status.Should().Be(TaskInstanceStatus.Running);
        updated.StartedAt.Should().NotBeNull();
    }

    [Test]
    public async Task StopAndCancelTransitions_MoveInstancesToCancelledWhenAllowed()
    {
        var queued = await CreateQueuedInstanceAsync();
        var running = await CreateQueuedInstanceAsync();
        await _service.TryMarkInstanceRunningAsync(running.Id);

        (await _service.CancelInstanceAsync(queued.Id)).Should().BeTrue();
        (await _service.StopInstanceAsync(running.Id)).Should().BeTrue();

        var queuedEntity = await _db.TaskInstances.FindAsync(queued.Id);
        var runningEntity = await _db.TaskInstances.FindAsync(running.Id);
        queuedEntity!.Status.Should().Be(TaskInstanceStatus.Cancelled);
        runningEntity!.Status.Should().Be(TaskInstanceStatus.Cancelled);
        runningEntity.CompletedAt.Should().NotBeNull();
    }

    private async Task<TaskDefinitionDB> CreateDefinitionAsync()
    {
        var definition = new TaskDefinitionDB
        {
            Name = $"Task-{Guid.NewGuid():N}",
            SourceText = """
[Task("SampleTask")]
public class SampleTask
{
    public async Task RunAsync(CancellationToken ct)
    {
        Log("hi");
    }
}
""",
            IsActive = true,
            ParametersJson = "[]"
        };

        _db.TaskDefinitions.Add(definition);
        await _db.SaveChangesAsync();
        return definition;
    }

    private async Task<TaskInstanceResponse> CreateQueuedInstanceAsync()
    {
        var definition = await CreateDefinitionAsync();
        return await _service.CreateInstanceAsync(new StartTaskInstanceRequest(definition.Id));
    }
}
