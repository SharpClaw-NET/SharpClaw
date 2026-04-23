using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SharpClaw.Application.Infrastructure.Models.Jobs;
using SharpClaw.Application.Infrastructure.Models.Tasks;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Infrastructure.Persistence.JSON;
using SharpClaw.Utils.Security;

namespace SharpClaw.Tests.Tasks;

/// <summary>
/// Tests for task permission keys, exposing task definitions as agent tools
/// via <see cref="TaskToolProvider"/>, and scheduled job task-definition binding.
/// </summary>
[TestFixture]
public class TaskAgentToolsAndSchedulingTests
{
    private string _dataDir = null!;
    private SharpClawDbContext _db = null!;
    private TaskService _taskService = null!;
    private ColdEntityStore _coldStore = null!;
    private TaskToolProvider _taskToolProvider = null!;

    private const string SimpleScript = """
        [Task("greet")]
        [Description("Greet someone")]
        public class GreetTask
        {
            public string Name { get; set; } = "World";
            public int Times { get; set; } = 1;

            public async Task RunAsync(CancellationToken ct)
            {
                Log($"Hello, {Name}! (x{Times})");
            }
        }
        """;

    private const string NoParamScript = """
        [Task("ping")]
        public class PingTask
        {
            public async Task RunAsync(CancellationToken ct)
            {
                Log("pong");
            }
        }
        """;

    [SetUp]
    public void SetUp()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"phase4_{Guid.NewGuid():N}");
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
        _taskToolProvider = new TaskToolProvider(_db);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    // ─────────────────────────────────────────────────────────────
    // Permission key constants
    // ─────────────────────────────────────────────────────────────

    [Test]
    public void TaskPermissionKeys_HaveExpectedValues()
    {
        TaskPermissionKeys.CanManageTasks.Should().Be("CanManageTasks");
        TaskPermissionKeys.CanExecuteTasks.Should().Be("CanExecuteTasks");
        TaskPermissionKeys.CanInvokeTasksAsTool.Should().Be("CanInvokeTasksAsTool");
    }

    // ─────────────────────────────────────────────────────────────
    // TaskToolProvider — schema generation
    // ─────────────────────────────────────────────────────────────

    [Test]
    public async Task GetToolDefinitionsAsync_NoDefinitions_ReturnsEmpty()
    {
        var tools = await _taskToolProvider.GetToolDefinitionsAsync();

        tools.Should().BeEmpty();
    }

    [Test]
    public async Task GetToolDefinitionsAsync_ActiveDefinition_ReturnsOneTool()
    {
        await _taskService.CreateDefinitionAsync(new CreateTaskDefinitionRequest(SimpleScript));

        var tools = await _taskToolProvider.GetToolDefinitionsAsync();

        tools.Should().HaveCount(1);
        tools[0].Name.Should().Be($"{TaskToolProvider.ToolPrefix}greet");
    }

    [Test]
    public async Task GetToolDefinitionsAsync_InactiveDefinition_IsExcluded()
    {
        var def = await _taskService.CreateDefinitionAsync(new CreateTaskDefinitionRequest(SimpleScript));
        await _taskService.UpdateDefinitionAsync(def.Id, new UpdateTaskDefinitionRequest(IsActive: false));

        var tools = await _taskToolProvider.GetToolDefinitionsAsync();

        tools.Should().BeEmpty();
    }

    [Test]
    public async Task GetToolDefinitionsAsync_ToolDescription_IncludesTaskDescription()
    {
        await _taskService.CreateDefinitionAsync(new CreateTaskDefinitionRequest(SimpleScript));

        var tools = await _taskToolProvider.GetToolDefinitionsAsync();

        tools[0].Description.Should().Contain("greet").And.Contain("Greet someone");
    }

    [Test]
    public async Task GetToolDefinitionsAsync_ParametersSchema_ContainsExpectedProperties()
    {
        await _taskService.CreateDefinitionAsync(new CreateTaskDefinitionRequest(SimpleScript));

        var tools = await _taskToolProvider.GetToolDefinitionsAsync();

        var schema = tools[0].ParametersSchema;
        schema.GetProperty("type").GetString().Should().Be("object");

        var props = schema.GetProperty("properties");
        props.TryGetProperty("Name", out _).Should().BeTrue();
        props.TryGetProperty("Times", out _).Should().BeTrue();
    }

    [Test]
    public async Task GetToolDefinitionsAsync_IntParam_MappedToNumberType()
    {
        await _taskService.CreateDefinitionAsync(new CreateTaskDefinitionRequest(SimpleScript));

        var tools = await _taskToolProvider.GetToolDefinitionsAsync();

        var timesType = tools[0].ParametersSchema
            .GetProperty("properties")
            .GetProperty("Times")
            .GetProperty("type")
            .GetString();

        timesType.Should().Be("number");
    }

    [Test]
    public async Task GetToolDefinitionsAsync_NoParams_SchemaHasEmptyProperties()
    {
        await _taskService.CreateDefinitionAsync(new CreateTaskDefinitionRequest(NoParamScript));

        var tools = await _taskToolProvider.GetToolDefinitionsAsync();

        tools.Should().HaveCount(1);
        var props = tools[0].ParametersSchema.GetProperty("properties");
        props.EnumerateObject().Should().BeEmpty();
    }

    [Test]
    public async Task GetToolDefinitionsAsync_MultipleActiveDefinitions_AllReturned()
    {
        await _taskService.CreateDefinitionAsync(new CreateTaskDefinitionRequest(SimpleScript));
        await _taskService.CreateDefinitionAsync(new CreateTaskDefinitionRequest(NoParamScript));

        var tools = await _taskToolProvider.GetToolDefinitionsAsync();

        tools.Should().HaveCount(2);
        tools.Select(t => t.Name).Should().Contain($"{TaskToolProvider.ToolPrefix}greet")
            .And.Contain($"{TaskToolProvider.ToolPrefix}ping");
    }

    // ─────────────────────────────────────────────────────────────
    // TaskToolProvider.TryParseTaskName
    // ─────────────────────────────────────────────────────────────

    [Test]
    public void TryParseTaskName_ValidPrefix_ReturnsTaskName()
    {
        var name = TaskToolProvider.TryParseTaskName($"{TaskToolProvider.ToolPrefix}greet");

        name.Should().Be("greet");
    }

    [Test]
    public void TryParseTaskName_NoPrefix_ReturnsNull()
    {
        var name = TaskToolProvider.TryParseTaskName("greet");

        name.Should().BeNull();
    }

    [Test]
    public void TryParseTaskName_OtherPrefix_ReturnsNull()
    {
        var name = TaskToolProvider.TryParseTaskName("other_tool__greet");

        name.Should().BeNull();
    }

    // ─────────────────────────────────────────────────────────────
    // ScheduledJobDB — task definition binding
    // ─────────────────────────────────────────────────────────────

    [Test]
    public async Task ScheduledJob_WithTaskDefinitionId_PersistsAndLoadsRelation()
    {
        var def = await _taskService.CreateDefinitionAsync(
            new CreateTaskDefinitionRequest(SimpleScript));

        var paramJson = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["Name"] = "Alice",
            ["Times"] = "3",
        });

        var job = new ScheduledJobDB
        {
            Name = "greet-scheduled",
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TaskDefinitionId = def.Id,
            ParameterValuesJson = paramJson,
            CallerAgentId = Guid.NewGuid(),
        };
        _db.ScheduledTasks.Add(job);
        await _db.SaveChangesAsync();

        var loaded = await _db.ScheduledTasks
            .Include(j => j.TaskDefinition)
            .FirstAsync(j => j.Id == job.Id);

        loaded.TaskDefinitionId.Should().Be(def.Id);
        loaded.TaskDefinition!.Name.Should().Be("greet");
        loaded.CallerAgentId.Should().NotBeNull();

        var storedParams = JsonSerializer.Deserialize<Dictionary<string, string>>(
            loaded.ParameterValuesJson!);
        storedParams!["Name"].Should().Be("Alice");
        storedParams["Times"].Should().Be("3");
    }

    [Test]
    public async Task ScheduledJob_WithoutTaskDefinitionId_IsValidLegacyRecord()
    {
        var job = new ScheduledJobDB
        {
            Name = "legacy-job",
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(1),
        };
        _db.ScheduledTasks.Add(job);
        await _db.SaveChangesAsync();

        var loaded = await _db.ScheduledTasks.FindAsync(job.Id);

        loaded!.TaskDefinitionId.Should().BeNull();
        loaded.ParameterValuesJson.Should().BeNull();
    }

    [Test]
    public void ScheduledJob_TaskDefinitionForeignKey_IsConfiguredWithSetNullDeleteBehavior()
    {
        var model = _db.Model;
        var scheduledJobEntity = model.FindEntityType(typeof(ScheduledJobDB))!;
        var fk = scheduledJobEntity.GetForeignKeys()
            .SingleOrDefault(f => f.PrincipalEntityType.ClrType == typeof(TaskDefinitionDB));

        fk.Should().NotBeNull("ScheduledJobDB should have a FK to TaskDefinitionDB");
        fk!.DeleteBehavior.Should().Be(DeleteBehavior.SetNull);
    }
}
