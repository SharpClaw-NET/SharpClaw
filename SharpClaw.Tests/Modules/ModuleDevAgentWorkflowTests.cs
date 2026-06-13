using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Modules.ModuleDev;
using SharpClaw.Modules.ModuleDev.Services;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class ModuleDevAgentWorkflowTests
{
    private string _externalModulesDir = null!;

    [SetUp]
    public void SetUp()
    {
        _externalModulesDir = Path.Combine(
            Path.GetTempPath(),
            "SharpClawModuleDevAgentWorkflowTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_externalModulesDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_externalModulesDir))
            Directory.Delete(_externalModulesDir, recursive: true);
    }

    [Test]
    public void GetToolDefinitions_ExposeAgentWorkflowTools()
    {
        var module = new ModuleDevModule();

        var names = module.GetToolDefinitions().Select(tool => tool.Name);

        names.Should().Contain("get_sdk_reference");
        names.Should().Contain("apply_module_files");
        names.Should().Contain("apply_task_source");
        names.Should().Contain("record_conversation_steering");
        names.Should().Contain("list_conversation_steering");
    }

    [Test]
    public async Task GetSdkReference_ReturnsRuntimeReferenceForAgents()
    {
        var module = new ModuleDevModule();
        await using var provider = CreateProvider(new RecordingLifecycle(_externalModulesDir));
        using var parameters = JsonDocument.Parse("""{"topic":"javascript"}""");

        var result = await module.ExecuteToolAsync(
            "get_sdk_reference",
            parameters.RootElement,
            Job(),
            provider,
            CancellationToken.None);

        result.Should().Contain("@sharpclaw/module-host");
        result.Should().Contain("addConversationSteering");
    }

    [Test]
    public async Task ApplyModuleFiles_WritesLoadsAndSteersWorkflowResult()
    {
        var lifecycle = new RecordingLifecycle(_externalModulesDir);
        var steering = new RecordingConversationSteering();
        var commands = new RecordingCommandRunner();
        var module = new ModuleDevModule();
        await using var provider = CreateProvider(lifecycle, steering: steering, commandRunner: commands);
        var channelId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var threadId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        using var parameters = JsonDocument.Parse($$$"""
            {
              "module_id": "sample_node",
              "runtime": "node",
              "load": true,
              "files": [
                {
                  "relative_path": "module.json",
                  "content": "{\"id\":\"sample_node\",\"displayName\":\"Sample Node\",\"toolPrefix\":\"sn\",\"runtime\":\"node\",\"entrypoint\":\"module.mjs\",\"entryAssembly\":\"\"}"
                },
                {
                  "relative_path": "module.mjs",
                  "content": "export {};"
                }
              ],
              "conversation": {
                "channel_id": "{{{channelId}}}",
                "thread_id": "{{{threadId}}}"
              }
            }
            """);

        var result = await module.ExecuteToolAsync(
            "apply_module_files",
            parameters.RootElement,
            Job(channelId),
            provider,
            CancellationToken.None);

        using var payload = JsonDocument.Parse(result);
        payload.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        payload.RootElement.GetProperty("verification")
            .GetProperty("steps")
            .EnumerateArray()
            .Select(step => step.GetProperty("name").GetString())
            .Should()
            .Equal("node_syntax");
        File.Exists(Path.Combine(_externalModulesDir, "sample_node", "module.mjs")).Should().BeTrue();
        lifecycle.LoadedDir.Should().Be(Path.Combine(_externalModulesDir, "sample_node"));
        commands.Commands.Should().ContainSingle();
        commands.Commands[0].FileName.Should().Be("node");
        commands.Commands[0].Arguments.Should().Equal("--check", "module.mjs");
        steering.Requests.Should().ContainSingle();
        steering.Requests[0].ChannelId.Should().Be(channelId);
        steering.Requests[0].ThreadId.Should().Be(threadId);
        steering.Requests[0].Category.Should().Be("module_workflow");
        steering.Requests[0].Summary.Should().Contain("verified");
        steering.Requests[0].Summary.Should().Contain("hot-loaded");
    }

    [Test]
    public async Task ApplyModuleFiles_WhenNodeVerificationFails_DoesNotLoadAndSteersDiagnostics()
    {
        var lifecycle = new RecordingLifecycle(_externalModulesDir);
        var steering = new RecordingConversationSteering();
        var commands = new RecordingCommandRunner();
        commands.Enqueue(exitCode: 1, stderr: "SyntaxError: Unexpected token");
        var module = new ModuleDevModule();
        await using var provider = CreateProvider(lifecycle, steering: steering, commandRunner: commands);
        var channelId = Guid.Parse("abababab-abab-abab-abab-abababababab");
        using var parameters = JsonDocument.Parse($$$"""
            {
              "module_id": "bad_node",
              "runtime": "node",
              "files": [
                {
                  "relative_path": "module.json",
                  "content": "{\"id\":\"bad_node\",\"displayName\":\"Bad Node\",\"toolPrefix\":\"bn\",\"runtime\":\"node\",\"entrypoint\":\"module.mjs\",\"entryAssembly\":\"\"}"
                },
                {
                  "relative_path": "module.mjs",
                  "content": "export const broken = ;"
                }
              ],
              "conversation": {
                "channel_id": "{{{channelId}}}"
              }
            }
            """);

        var result = await module.ExecuteToolAsync(
            "apply_module_files",
            parameters.RootElement,
            Job(channelId),
            provider,
            CancellationToken.None);

        using var payload = JsonDocument.Parse(result);
        payload.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        payload.RootElement.GetProperty("verification")
            .GetProperty("success")
            .GetBoolean()
            .Should()
            .BeFalse();
        lifecycle.LoadedDir.Should().BeNull();
        commands.Commands.Should().ContainSingle();
        steering.Requests.Should().ContainSingle();
        steering.Requests[0].Category.Should().Be("module_verify");
        steering.Requests[0].Details.Should().Contain("Unexpected token");
    }

    [Test]
    public async Task ApplyModuleFiles_NodeDependencyInstallAndDeclaredVerifyRunBeforeLoad()
    {
        var lifecycle = new RecordingLifecycle(_externalModulesDir);
        var steering = new RecordingConversationSteering();
        var commands = new RecordingCommandRunner();
        var module = new ModuleDevModule();
        await using var provider = CreateProvider(lifecycle, steering: steering, commandRunner: commands);
        var channelId = Guid.Parse("cdcdcdcd-cdcd-cdcd-cdcd-cdcdcdcdcdcd");
        using var parameters = JsonDocument.Parse($$$"""
            {
              "module_id": "verified_node",
              "runtime": "node",
              "install_dependencies": true,
              "run_declared_verify": true,
              "load": false,
              "files": [
                {
                  "relative_path": "module.json",
                  "content": "{\"id\":\"verified_node\",\"displayName\":\"Verified Node\",\"toolPrefix\":\"vn\",\"runtime\":\"node\",\"entrypoint\":\"module.mjs\",\"entryAssembly\":\"\"}"
                },
                {
                  "relative_path": "module.mjs",
                  "content": "export {};"
                },
                {
                  "relative_path": "package.json",
                  "content": "{\"type\":\"module\",\"scripts\":{\"sharpclawVerify\":\"node --check module.mjs\"}}"
                },
                {
                  "relative_path": "package-lock.json",
                  "content": "{\"lockfileVersion\":3}"
                }
              ],
              "conversation": {
                "channel_id": "{{{channelId}}}"
              }
            }
            """);

        var result = await module.ExecuteToolAsync(
            "apply_module_files",
            parameters.RootElement,
            Job(channelId),
            provider,
            CancellationToken.None);

        using var payload = JsonDocument.Parse(result);
        payload.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        lifecycle.LoadedDir.Should().BeNull();
        commands.Commands.Select(command => command.FileName + " " + string.Join(' ', command.Arguments))
            .Should()
            .Equal(
                "npm ci",
                "node --check module.mjs",
                "npm run sharpclawVerify");
        payload.RootElement.GetProperty("verification")
            .GetProperty("steps")
            .GetArrayLength()
            .Should()
            .Be(3);
    }

    [Test]
    public async Task ApplyModuleFiles_PythonCompileAndDeclaredVerifyRunBeforeLoad()
    {
        var lifecycle = new RecordingLifecycle(_externalModulesDir);
        var steering = new RecordingConversationSteering();
        var commands = new RecordingCommandRunner();
        var module = new ModuleDevModule();
        await using var provider = CreateProvider(lifecycle, steering: steering, commandRunner: commands);
        var channelId = Guid.Parse("efefefef-efef-efef-efef-efefefefefef");
        using var parameters = JsonDocument.Parse($$"""
            {
              "module_id": "verified_python",
              "runtime": "python",
              "load": true,
              "files": [
                {
                  "relative_path": "module.json",
                  "content": "{\"id\":\"verified_python\",\"displayName\":\"Verified Python\",\"toolPrefix\":\"vp\",\"runtime\":\"python\",\"entrypoint\":\"module.py\",\"entryAssembly\":\"\"}"
                },
                {
                  "relative_path": "module.py",
                  "content": "print('ok')\n"
                },
                {
                  "relative_path": "pyproject.toml",
                  "content": "[tool.sharpclaw]\nverify-command = \"python -m pytest\"\n"
                }
              ],
              "conversation": {
                "channel_id": "{{channelId}}"
              }
            }
            """);

        var result = await module.ExecuteToolAsync(
            "apply_module_files",
            parameters.RootElement,
            Job(channelId),
            provider,
            CancellationToken.None);

        using var payload = JsonDocument.Parse(result);
        payload.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        lifecycle.LoadedDir.Should().Be(Path.Combine(_externalModulesDir, "verified_python"));
        commands.Commands.Select(command => command.FileName + " " + string.Join(' ', command.Arguments))
            .Should()
            .Equal(
                "python -m py_compile module.py",
                "python -m pytest");
        steering.Requests.Should().ContainSingle();
        steering.Requests[0].Summary.Should().Contain("verified");
    }

    [Test]
    public async Task ApplyModuleFiles_InvalidPythonVerifyCommandFailsAsVerificationNotWorkflowCrash()
    {
        var lifecycle = new RecordingLifecycle(_externalModulesDir);
        var steering = new RecordingConversationSteering();
        var commands = new RecordingCommandRunner();
        var module = new ModuleDevModule();
        await using var provider = CreateProvider(lifecycle, steering: steering, commandRunner: commands);
        var channelId = Guid.Parse("12121212-3434-5656-7878-909090909090");
        using var parameters = JsonDocument.Parse($$"""
            {
              "module_id": "unsafe_python",
              "runtime": "python",
              "files": [
                {
                  "relative_path": "module.json",
                  "content": "{\"id\":\"unsafe_python\",\"displayName\":\"Unsafe Python\",\"toolPrefix\":\"up\",\"runtime\":\"python\",\"entrypoint\":\"module.py\",\"entryAssembly\":\"\"}"
                },
                {
                  "relative_path": "module.py",
                  "content": "print('ok')\n"
                },
                {
                  "relative_path": "pyproject.toml",
                  "content": "[tool.sharpclaw]\nverify-command = \"python -m pytest && echo unsafe\"\n"
                }
              ],
              "conversation": {
                "channel_id": "{{channelId}}"
              }
            }
            """);

        var result = await module.ExecuteToolAsync(
            "apply_module_files",
            parameters.RootElement,
            Job(channelId),
            provider,
            CancellationToken.None);

        using var payload = JsonDocument.Parse(result);
        payload.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        payload.RootElement.GetProperty("verification")
            .GetProperty("error")
            .GetString()
            .Should()
            .Contain("shell metacharacters");
        lifecycle.LoadedDir.Should().BeNull();
        commands.Commands.Select(command => command.FileName + " " + string.Join(' ', command.Arguments))
            .Should()
            .Equal("python -m py_compile module.py");
        steering.Requests.Should().ContainSingle();
        steering.Requests[0].Category.Should().Be("module_verify");
    }

    [Test]
    public async Task ApplyTaskSource_WhenValidationFails_DoesNotSaveAndSteersDiagnostics()
    {
        var authoring = new RecordingTaskAuthoring(isValid: false);
        var steering = new RecordingConversationSteering();
        var module = new ModuleDevModule();
        await using var provider = CreateProvider(
            new RecordingLifecycle(_externalModulesDir),
            authoring,
            steering);
        var channelId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        using var parameters = JsonDocument.Parse($$"""
            {
              "source_text": "bad task source",
              "conversation": {
                "channel_id": "{{channelId}}"
              }
            }
            """);

        var result = await module.ExecuteToolAsync(
            "apply_task_source",
            parameters.RootElement,
            Job(channelId),
            provider,
            CancellationToken.None);

        using var payload = JsonDocument.Parse(result);
        payload.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        authoring.CreatedSource.Should().BeNull();
        authoring.Updated.Should().BeNull();
        steering.Requests.Should().ContainSingle();
        steering.Requests[0].Category.Should().Be("task_validation");
        steering.Requests[0].Details.Should().Contain("MDK001");
    }

    [Test]
    public async Task ApplyTaskSource_WhenValid_CreatesTaskAndSteersSavedId()
    {
        var authoring = new RecordingTaskAuthoring(isValid: true);
        var steering = new RecordingConversationSteering();
        var module = new ModuleDevModule();
        await using var provider = CreateProvider(
            new RecordingLifecycle(_externalModulesDir),
            authoring,
            steering);
        var channelId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        using var parameters = JsonDocument.Parse($$"""
            {
              "source_text": "good task source",
              "conversation": {
                "channel_id": "{{channelId}}"
              }
            }
            """);

        var result = await module.ExecuteToolAsync(
            "apply_task_source",
            parameters.RootElement,
            Job(channelId),
            provider,
            CancellationToken.None);

        using var payload = JsonDocument.Parse(result);
        payload.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        authoring.CreatedSource.Should().Be("good task source");
        steering.Requests.Should().ContainSingle();
        steering.Requests[0].Category.Should().Be("task_workflow");
        steering.Requests[0].Details.Should().Contain(authoring.TaskId.ToString());
    }

    private ServiceProvider CreateProvider(
        RecordingLifecycle lifecycle,
        RecordingTaskAuthoring? authoring = null,
        RecordingConversationSteering? steering = null,
        RecordingCommandRunner? commandRunner = null)
    {
        var services = new ServiceCollection();
        new ModuleDevModule().ConfigureServices(services);
        services.AddSingleton<IModuleLifecycleManager>(lifecycle);
        services.AddSingleton<IModuleRuntimeCommandRunner>(commandRunner ?? new RecordingCommandRunner());
        services.AddSingleton<IModuleInfoProvider>(new EmptyModuleInfoProvider());
        services.AddSingleton<ITaskAuthoring>(authoring ?? new RecordingTaskAuthoring(isValid: true));
        services.AddSingleton<IConversationSteering>(steering ?? new RecordingConversationSteering());
        return services.BuildServiceProvider();
    }

    private static AgentJobContext Job(Guid? channelId = null) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            channelId ?? Guid.Empty,
            ResourceId: null,
            ActionKey: "mdk_test");

    private sealed class RecordingLifecycle(string externalModulesDir) : IModuleLifecycleManager
    {
        public string ExternalModulesDir { get; } = externalModulesDir;
        public string? LoadedDir { get; private set; }
        public string? ReloadedId { get; private set; }

        public bool IsModuleRegistered(string moduleId) => false;
        public bool IsToolPrefixRegistered(string toolPrefix) => false;
        public (ISharpClawModule Module, string ToolName)? FindToolByName(string toolName) => null;

        public Task<ModuleStateResponse> LoadExternalAsync(
            string moduleDir,
            IServiceProvider hostServices,
            CancellationToken ct = default)
        {
            LoadedDir = moduleDir;
            return Task.FromResult(State(Path.GetFileName(moduleDir)));
        }

        public Task UnloadExternalAsync(string moduleId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<ModuleStateResponse> ReloadExternalAsync(
            string moduleId,
            IServiceProvider hostServices,
            CancellationToken ct = default)
        {
            ReloadedId = moduleId;
            return Task.FromResult(State(moduleId));
        }

        private static ModuleStateResponse State(string moduleId) =>
            new(
                moduleId,
                "Loaded Module",
                "lm",
                true,
                "0.1.0-beta",
                true,
                true,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch);
    }

    private sealed class EmptyModuleInfoProvider : IModuleInfoProvider
    {
        public IReadOnlyList<ModuleInfo> GetAllModules() => [];
    }

    private sealed class RecordingTaskAuthoring(bool isValid) : ITaskAuthoring
    {
        public Guid TaskId { get; } = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        public string? CreatedSource { get; private set; }
        public (Guid Id, string? SourceText, bool? IsActive)? Updated { get; private set; }

        public TaskValidationResponse ValidateDefinition(string sourceText) =>
            isValid
                ? new TaskValidationResponse(true, [])
                : new TaskValidationResponse(
                    false,
                    [new TaskDiagnosticResponse("Error", "MDK001", "Bad task source.", 1, 1)]);

        public Task<TaskDefinitionResponse> CreateDefinitionAsync(
            CreateTaskDefinitionRequest request,
            CancellationToken ct = default)
        {
            CreatedSource = request.SourceText;
            return Task.FromResult(Response(TaskId));
        }

        public Task<TaskDefinitionResponse?> GetDefinitionAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult<TaskDefinitionResponse?>(id == TaskId ? Response(id) : null);

        public Task<IReadOnlyList<TaskDefinitionResponse>> ListDefinitionsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TaskDefinitionResponse>>([Response(TaskId)]);

        public Task<TaskDefinitionResponse?> UpdateDefinitionAsync(
            Guid id,
            UpdateTaskDefinitionRequest request,
            CancellationToken ct = default)
        {
            Updated = (id, request.SourceText, request.IsActive);
            return Task.FromResult<TaskDefinitionResponse?>(Response(id));
        }

        public Task<bool> DeleteDefinitionAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(true);

        private static TaskDefinitionResponse Response(Guid id) =>
            new(
                id,
                "Generated Task",
                "Created by workflow",
                null,
                true,
                [],
                [],
                [],
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch);
    }

    private sealed class RecordingConversationSteering : IConversationSteering
    {
        public List<ConversationSteeringRequest> Requests { get; } = [];

        public Task<ConversationSteeringResponse> AddAsync(
            ConversationSteeringRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ConversationSteeringResponse(
                Guid.NewGuid(),
                request.ChannelId,
                request.ThreadId,
                request.Summary,
                DateTimeOffset.UnixEpoch,
                request.Source,
                request.Category));
        }

        public Task<IReadOnlyList<ConversationSteeringResponse>> ListAsync(
            Guid channelId,
            Guid? threadId = null,
            int limit = 20,
            CancellationToken ct = default)
        {
            IReadOnlyList<ConversationSteeringResponse> rows = Requests
                .Where(request => request.ChannelId == channelId && request.ThreadId == threadId)
                .Take(limit)
                .Select(request => new ConversationSteeringResponse(
                    Guid.NewGuid(),
                    request.ChannelId,
                    request.ThreadId,
                    request.Summary,
                    DateTimeOffset.UnixEpoch,
                    request.Source,
                    request.Category))
                .ToList();
            return Task.FromResult(rows);
        }
    }

    private sealed class RecordingCommandRunner : IModuleRuntimeCommandRunner
    {
        private readonly Queue<(int ExitCode, string Stdout, string Stderr, bool TimedOut)> _outcomes = new();

        public List<ModuleRuntimeCommand> Commands { get; } = [];

        public void Enqueue(
            int exitCode = 0,
            string stdout = "",
            string stderr = "",
            bool timedOut = false) =>
            _outcomes.Enqueue((exitCode, stdout, stderr, timedOut));

        public Task<ModuleRuntimeCommandResult> RunAsync(
            ModuleRuntimeCommand command,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            var outcome = _outcomes.Count > 0
                ? _outcomes.Dequeue()
                : (ExitCode: 0, Stdout: "", Stderr: "", TimedOut: false);
            return Task.FromResult(new ModuleRuntimeCommandResult(
                command.FileName,
                command.Arguments,
                command.WorkingDirectory,
                outcome.ExitCode,
                outcome.TimedOut,
                outcome.Stdout,
                outcome.Stderr,
                TimeSpan.FromMilliseconds(2)));
        }
    }
}
