using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Core.Modules.Foreign;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class ForeignModuleHostCapabilityTests
{
    [Test]
    public async Task HostCapabilityServerRejectsCallsWithoutPerRunToken()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        await using var server = ForeignModuleHostCapabilityServer.Start("sample_module", services);
        using var client = new HttpClient { BaseAddress = server.Address };

        using var response = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.LogPath,
            new { message = "hello" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task HostCapabilityServerForwardsJobLifecycleCalls()
    {
        var jobs = new RecordingJobController();
        await using var services = new ServiceCollection()
            .AddSingleton<IAgentJobController>(jobs)
            .BuildServiceProvider();
        await using var server = ForeignModuleHostCapabilityServer.Start("sample_module", services);
        using var client = CreateClient(server);
        var jobId = Guid.NewGuid();

        using var logResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.JobLogPath,
            new { jobId, message = "step one", level = "Warning" });
        using var completeResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.JobCompletePath,
            new { jobId, resultData = """{"ok":true}""", message = "done" });
        using var failResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.JobFailPath,
            new { jobId, message = "failed", details = "details" });
        using var cancelResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.JobCancelPath,
            new { jobId, message = "stopping" });
        using var cancelStaleResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.JobCancelStaleByActionPrefixPath,
            new { actionKeyPrefix = "cur_transcribe" });

        logResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        completeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        failResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        cancelResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        cancelStaleResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        jobs.Logs.Should().ContainSingle()
            .Which.Should().Be((jobId, "step one", "Warning"));
        jobs.Completed.Should().ContainSingle()
            .Which.Should().Be((jobId, """{"ok":true}""", "done"));
        jobs.Failed.Should().ContainSingle()
            .Which.Should().Be((jobId, "failed", "details"));
        jobs.Cancelled.Should().ContainSingle()
            .Which.Should().Be((jobId, "stopping"));
        jobs.CancelledStalePrefixes.Should().ContainSingle()
            .Which.Should().Be("cur_transcribe");
    }

    [Test]
    public async Task HostCapabilityServerForwardsJobReaderCalls()
    {
        var reader = new RecordingJobReader();
        await using var services = new ServiceCollection()
            .AddSingleton<IAgentJobReader>(reader)
            .BuildServiceProvider();
        await using var server = ForeignModuleHostCapabilityServer.Start("sample_module", services);
        using var client = CreateClient(server);

        using var getResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.JobGetPath,
            new { id = reader.JobId });
        using var listResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.JobListByActionPrefixPath,
            new { actionKeyPrefix = "cur_", resourceId = reader.ResourceId });
        using var summariesResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.JobListSummariesByActionPrefixPath,
            new { actionKeyPrefix = "cur_", resourceId = reader.ResourceId });
        using var existsResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.JobExistsWithActionPrefixPath,
            new { jobId = reader.JobId, actionKeyPrefix = "cur_" });

        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        summariesResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        existsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var getPayload = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
        getPayload.RootElement.GetProperty("job")
            .GetProperty("id")
            .GetGuid()
            .Should()
            .Be(reader.JobId);

        var listPayload = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        listPayload.RootElement.GetProperty("jobs").GetArrayLength().Should().Be(1);

        var summariesPayload = JsonDocument.Parse(await summariesResponse.Content.ReadAsStringAsync());
        summariesPayload.RootElement.GetProperty("jobs")[0]
            .GetProperty("actionKey")
            .GetString()
            .Should()
            .Be("cur_transcribe_audio_device");

        var existsPayload = JsonDocument.Parse(await existsResponse.Content.ReadAsStringAsync());
        existsPayload.RootElement.GetProperty("value").GetBoolean().Should().BeTrue();
        reader.ListRequest.Should().NotBeNull();
        reader.ListRequest!.Value.Prefix.Should().Be("cur_");
        reader.ListRequest.Value.ResourceId.Should().Be(reader.ResourceId);
        reader.SummaryRequest.Should().NotBeNull();
        reader.SummaryRequest!.Value.Prefix.Should().Be("cur_");
        reader.SummaryRequest.Value.ResourceId.Should().Be(reader.ResourceId);
    }

    [Test]
    public async Task HostCapabilityServerUsesProvidedConfigStore()
    {
        var config = new RecordingConfigStore();
        await using var services = new ServiceCollection()
            .AddSingleton<IModuleConfigStore>(config)
            .BuildServiceProvider();
        await using var server = ForeignModuleHostCapabilityServer.Start("sample_module", services);
        using var client = CreateClient(server);

        using var setResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.ConfigSetPath,
            new { key = "theme", value = "dense" });
        using var getResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.ConfigGetPath,
            new { key = "theme" });
        var getPayload = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());

        setResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        getPayload.RootElement.GetProperty("value").GetString().Should().Be("dense");
    }

    [Test]
    public async Task HostCapabilityServerInvokesProtocolContracts()
    {
        var resolver = new RecordingProtocolContractResolver();
        await using var services = new ServiceCollection()
            .AddSingleton<IForeignModuleProtocolContractResolver>(resolver)
            .BuildServiceProvider();
        await using var server = ForeignModuleHostCapabilityServer.Start("sample_module", services);
        using var client = CreateClient(server);

        using var listResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.ProtocolContractsListPath,
            new { });
        using var invokeResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.ProtocolContractInvokePath,
            new
            {
                contractName = "editor_bridge",
                operation = "open_file",
                parameters = new
                {
                    path = "README.md",
                },
            });
        var listPayload = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var invokePayload = JsonDocument.Parse(await invokeResponse.Content.ReadAsStringAsync());

        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        invokeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        listPayload.RootElement.GetProperty("contracts")[0]
            .GetProperty("contractName").GetString().Should().Be("editor_bridge");
        invokePayload.RootElement.GetProperty("result")
            .GetProperty("operation").GetString().Should().Be("open_file");
        resolver.Invoker.LastParameters.GetProperty("path").GetString().Should().Be("README.md");
    }

    [Test]
    public async Task HostCapabilityServerForwardsTaskAuthoringAndLaunch()
    {
        var authoring = new RecordingTaskAuthoring();
        var launcher = new RecordingTaskLauncher();
        await using var services = new ServiceCollection()
            .AddSingleton<ITaskAuthoring>(authoring)
            .AddSingleton<ITaskInstanceLauncher>(launcher)
            .BuildServiceProvider();
        await using var server = ForeignModuleHostCapabilityServer.Start("sample_module", services);
        using var client = CreateClient(server);
        var taskId = authoring.TaskId;

        using var validateResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.TaskValidatePath,
            new { sourceText = "task source" });
        using var createResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.TaskCreatePath,
            new { sourceText = "create source" });
        using var getResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.TaskGetPath,
            new { id = taskId });
        using var listResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.TaskListPath,
            new { });
        using var updateResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.TaskUpdatePath,
            new { id = taskId, sourceText = "updated", isActive = false });
        using var deleteResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.TaskDeletePath,
            new { id = taskId });
        using var launchResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.TaskLaunchPath,
            new
            {
                taskDefinitionId = taskId,
                parameterValues = new Dictionary<string, string>
                {
                    ["input"] = "value",
                },
                callerAgentId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                channelId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            });

        validateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        launchResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        authoring.ValidatedSource.Should().Be("task source");
        authoring.CreatedSource.Should().Be("create source");
        authoring.Updated.Should().NotBeNull();
        authoring.Updated!.Value.Id.Should().Be(taskId);
        authoring.Updated!.Value.SourceText.Should().Be("updated");
        authoring.Updated!.Value.IsActive.Should().BeFalse();
        authoring.DeletedId.Should().Be(taskId);
        launcher.LastLaunch!.Value.TaskDefinitionId.Should().Be(taskId);
        launcher.LastLaunch!.Value.ParameterValues.Should().ContainKey("input").WhoseValue.Should().Be("value");

        var launchPayload = JsonDocument.Parse(await launchResponse.Content.ReadAsStringAsync());
        launchPayload.RootElement.GetProperty("instanceId").GetGuid()
            .Should()
            .Be(launcher.InstanceId);
    }

    [Test]
    public async Task HostCapabilityServerForwardsCoreMetricsAndAgentCapabilities()
    {
        var core = new RecordingCoreEntityIds();
        var metrics = new RecordingQueueMetrics();
        var agents = new RecordingAgentManager();
        await using var services = new ServiceCollection()
            .AddSingleton<ICoreEntityIdProvider>(core)
            .AddSingleton<IHostQueueMetrics>(metrics)
            .AddSingleton<IAgentManager>(agents)
            .BuildServiceProvider();
        await using var server = ForeignModuleHostCapabilityServer.Start("sample_module", services);
        using var client = CreateClient(server);

        using var agentIdsResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.CoreAgentIdsPath,
            new { });
        using var channelLookupResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.CoreChannelLookupPath,
            new { });
        using var metricsResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.QueueMetricsPath,
            new { });
        using var createAgentResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.AgentCreateSubAgentPath,
            new
            {
                name = "Helper",
                modelId = agents.ModelId,
                systemPrompt = "assist",
            });
        using var updateAgentResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.AgentUpdatePath,
            new
            {
                agentId = agents.AgentId,
                name = "Renamed",
                systemPrompt = "updated",
            });
        using var setAgentHeaderResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.AgentSetHeaderPath,
            new { id = agents.AgentId, header = "agent header" });
        using var setChannelHeaderResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.ChannelSetHeaderPath,
            new { id = agents.ChannelId, header = "channel header" });

        agentIdsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        channelLookupResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        metricsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        createAgentResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        updateAgentResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        setAgentHeaderResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        setChannelHeaderResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var agentIds = JsonDocument.Parse(await agentIdsResponse.Content.ReadAsStringAsync());
        agentIds.RootElement.GetProperty("ids")[0].GetGuid().Should().Be(core.AgentId);
        var channelLookup = JsonDocument.Parse(await channelLookupResponse.Content.ReadAsStringAsync());
        channelLookup.RootElement.GetProperty("items")[0].GetProperty("name").GetString().Should().Be("Clinic");
        var metricPayload = JsonDocument.Parse(await metricsResponse.Content.ReadAsStringAsync());
        metricPayload.RootElement.GetProperty("pendingJobCount").GetDouble().Should().Be(2);
        metricPayload.RootElement.GetProperty("pendingTaskCount").GetDouble().Should().Be(3);
        metricPayload.RootElement.GetProperty("schedulerPendingJobCount").GetDouble().Should().Be(5);
        agents.Created.Should().NotBeNull();
        agents.Created!.Value.Name.Should().Be("Helper");
        agents.Created!.Value.ModelId.Should().Be(agents.ModelId);
        agents.Created!.Value.SystemPrompt.Should().Be("assist");
        agents.Updated.Should().NotBeNull();
        agents.Updated!.Value.AgentId.Should().Be(agents.AgentId);
        agents.Updated!.Value.Name.Should().Be("Renamed");
        agents.Updated!.Value.SystemPrompt.Should().Be("updated");
        agents.Updated!.Value.ModelId.Should().BeNull();
        agents.AgentHeader.Should().NotBeNull();
        agents.AgentHeader!.Value.AgentId.Should().Be(agents.AgentId);
        agents.AgentHeader!.Value.Header.Should().Be("agent header");
        agents.ChannelHeader.Should().NotBeNull();
        agents.ChannelHeader!.Value.ChannelId.Should().Be(agents.ChannelId);
        agents.ChannelHeader!.Value.Header.Should().Be("channel header");
    }

    [Test]
    public async Task HostCapabilityServerForwardsConversationSteering()
    {
        var steering = new RecordingConversationSteering();
        await using var services = new ServiceCollection()
            .AddSingleton<IConversationSteering>(steering)
            .BuildServiceProvider();
        await using var server = ForeignModuleHostCapabilityServer.Start("sample_module", services);
        using var client = CreateClient(server);
        var channelId = Guid.Parse("10101010-1010-1010-1010-101010101010");
        var threadId = Guid.Parse("20202020-2020-2020-2020-202020202020");

        using var steerResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.ConversationSteerPath,
            new
            {
                channelId,
                threadId,
                summary = "Build failed with CS1002.",
                source = "module_dev",
                category = "build",
                details = "Fix Module.cs line 12.",
                clientType = "module-dev",
            });
        using var listResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.ConversationSteeringListPath,
            new { channelId, threadId, limit = 5 });

        steerResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        steering.LastAdd.Should().NotBeNull();
        steering.LastAdd!.ChannelId.Should().Be(channelId);
        steering.LastAdd.ThreadId.Should().Be(threadId);
        steering.LastAdd.Summary.Should().Be("Build failed with CS1002.");
        steering.LastList.Should().NotBeNull();
        steering.LastList!.Value.ChannelId.Should().Be(channelId);
        steering.LastList.Value.ThreadId.Should().Be(threadId);
        steering.LastList.Value.Limit.Should().Be(5);

        var steerPayload = JsonDocument.Parse(await steerResponse.Content.ReadAsStringAsync());
        steerPayload.RootElement.GetProperty("steering")
            .GetProperty("source")
            .GetString()
            .Should()
            .Be("module_dev");
        var listPayload = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        listPayload.RootElement.GetProperty("steering")[0]
            .GetProperty("content")
            .GetString()
            .Should()
            .Contain("listed steering");
    }

    [Test]
    public async Task HostCapabilityServerForwardsModuleLifecycleInfoAndToolInvocation()
    {
        var lifecycle = new RecordingModuleLifecycle();
        var info = new RecordingModuleInfo();
        await using var services = new ServiceCollection()
            .AddSingleton<IModuleLifecycleManager>(lifecycle)
            .AddSingleton<IModuleInfoProvider>(info)
            .BuildServiceProvider();
        await using var server = ForeignModuleHostCapabilityServer.Start("sample_module", services);
        using var client = CreateClient(server);

        using var rootResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.ModulesExternalRootPath,
            new { });
        using var infoResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.ModulesInfoListPath,
            new { });
        using var registeredResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.ModuleRegisteredPath,
            new { moduleId = "loaded_module" });
        using var prefixResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.ModuleToolPrefixRegisteredPath,
            new { toolPrefix = "lm" });
        using var loadResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.ModuleLoadPath,
            new { moduleDir = "E:/modules/new_module" });
        using var reloadResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.ModuleReloadPath,
            new { moduleId = "loaded_module" });
        using var toolResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.ModuleToolInvokePath,
            new
            {
                toolName = "echo_tool",
                parameters = new
                {
                    value = "payload",
                },
            });
        using var unloadResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.ModuleUnloadPath,
            new { moduleId = "loaded_module" });

        rootResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        infoResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        registeredResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        prefixResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        loadResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        reloadResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        toolResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        unloadResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var rootPayload = JsonDocument.Parse(await rootResponse.Content.ReadAsStringAsync());
        rootPayload.RootElement.GetProperty("directory").GetString().Should().Be(lifecycle.ExternalModulesDir);
        var infoPayload = JsonDocument.Parse(await infoResponse.Content.ReadAsStringAsync());
        infoPayload.RootElement.GetProperty("modules")[0].GetProperty("id").GetString().Should().Be("loaded_module");
        var registeredPayload = JsonDocument.Parse(await registeredResponse.Content.ReadAsStringAsync());
        registeredPayload.RootElement.GetProperty("isRegistered").GetBoolean().Should().BeTrue();
        var prefixPayload = JsonDocument.Parse(await prefixResponse.Content.ReadAsStringAsync());
        prefixPayload.RootElement.GetProperty("isRegistered").GetBoolean().Should().BeTrue();
        var toolPayload = JsonDocument.Parse(await toolResponse.Content.ReadAsStringAsync());
        toolPayload.RootElement.GetProperty("result").GetString().Should().Be("echo:payload");
        lifecycle.LoadedDir.Should().Be("E:/modules/new_module");
        lifecycle.ReloadedId.Should().Be("loaded_module");
        lifecycle.UnloadedId.Should().Be("loaded_module");
    }

    [Test]
    public async Task HostCapabilityServerForwardsModuleStorageCapabilities()
    {
        var storage = new RecordingModuleStorageGateway();
        await using var services = new ServiceCollection()
            .AddSingleton<IModuleStorageGateway>(storage)
            .BuildServiceProvider();
        await using var server = ForeignModuleHostCapabilityServer.Start("sample_module", services);
        using var client = CreateClient(server);

        using var listResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.ModuleStorageListPath,
            new { });
        using var invokeResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.ModuleStorageInvokePath,
            new
            {
                storageName = "records",
                operation = "get",
                parameters = new
                {
                    id = "sample",
                },
            });

        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        invokeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var listPayload = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        listPayload.RootElement.GetProperty("contracts")[0].GetProperty("storageName")
            .GetString()
            .Should()
            .Be("records");
        var invokePayload = JsonDocument.Parse(await invokeResponse.Content.ReadAsStringAsync());
        invokePayload.RootElement.GetProperty("result").GetProperty("value").GetString()
            .Should()
            .Be("sample:get");
        storage.LastInvocation.Should().NotBeNull();
        storage.LastInvocation!.Value.ModuleId.Should().Be("sample_module");
        storage.LastInvocation!.Value.StorageName.Should().Be("records");
        storage.LastInvocation!.Value.Operation.Should().Be("get");
        storage.LastInvocation!.Value.Parameters.GetProperty("id").GetString().Should().Be("sample");
    }

    [Test]
    public async Task HostCapabilityServerRejectsModuleStorageRequestsForOtherModules()
    {
        await using var services = new ServiceCollection()
            .AddSingleton<IModuleStorageGateway>(new RecordingModuleStorageGateway())
            .BuildServiceProvider();
        await using var server = ForeignModuleHostCapabilityServer.Start("sample_module", services);
        using var client = CreateClient(server);

        using var response = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.ModuleStorageInvokePath,
            new
            {
                moduleId = "other_module",
                storageName = "records",
                operation = "get",
                parameters = new
                {
                    id = "sample",
                },
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task HostCapabilityServerForwardsModelRegistrarCapabilities()
    {
        var registrar = new RecordingModelRegistrar();
        await using var services = new ServiceCollection()
            .AddSingleton<IModelRegistrar>(registrar)
            .BuildServiceProvider();
        await using var server = ForeignModuleHostCapabilityServer.Start("sample_module", services);
        using var client = CreateClient(server);
        var modelId = Guid.NewGuid();

        using var ensureProviderResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.ModelEnsureProviderPath,
            new { providerKey = "local", displayName = "Local" });
        using var ensureModelResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.ModelEnsureModelPath,
            new
            {
                modelName = "demo.gguf",
                providerId = registrar.ProviderId,
                capabilityTags = new[] { "chat" },
            });
        using var metadataResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.ModelMetadataPath,
            new { modelId });
        using var deleteResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.ModelDeletePath,
            new { modelId });

        ensureProviderResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        ensureModelResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        metadataResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var providerPayload = JsonDocument.Parse(await ensureProviderResponse.Content.ReadAsStringAsync());
        providerPayload.RootElement.GetProperty("id").GetGuid().Should().Be(registrar.ProviderId);
        var modelPayload = JsonDocument.Parse(await ensureModelResponse.Content.ReadAsStringAsync());
        modelPayload.RootElement.GetProperty("id").GetGuid().Should().Be(registrar.ModelId);
        var metadataPayload = JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync());
        metadataPayload.RootElement.GetProperty("metadata").GetProperty("name").GetString()
            .Should()
            .Be("demo.gguf");
        var deletePayload = JsonDocument.Parse(await deleteResponse.Content.ReadAsStringAsync());
        deletePayload.RootElement.GetProperty("value").GetBoolean().Should().BeTrue();

        registrar.EnsureProviderCall.Should().Be(("local", "Local"));
        registrar.EnsureModelCall.Should().NotBeNull();
        registrar.EnsureModelCall!.Value.ModelName.Should().Be("demo.gguf");
        registrar.EnsureModelCall!.Value.ProviderId.Should().Be(registrar.ProviderId);
        registrar.EnsureModelCall!.Value.CapabilityTags.Should().Equal("chat");
        registrar.MetadataModelId.Should().Be(modelId);
        registrar.DeletedModelId.Should().Be(modelId);
    }

    [Test]
    public async Task HostCapabilityServerForwardsModelInfoProviderCapabilities()
    {
        var modelInfo = new RecordingModelInfoProvider();
        await using var services = new ServiceCollection()
            .AddSingleton<IModelInfoProvider>(modelInfo)
            .BuildServiceProvider();
        await using var server = ForeignModuleHostCapabilityServer.Start("sample_module", services);
        using var client = CreateClient(server);
        var modelId = Guid.NewGuid();

        using var providerInfoResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.ModelProviderInfoPath,
            new { modelId });
        using var localPathResponse = await client.PostAsJsonAsync(
            ForeignModuleHostCapabilityProtocol.ModelLocalFilePathPath,
            new { modelId });

        providerInfoResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        localPathResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var providerInfoPayload = JsonDocument.Parse(await providerInfoResponse.Content.ReadAsStringAsync());
        var info = providerInfoPayload.RootElement.GetProperty("info");
        info.GetProperty("modelName").GetString().Should().Be("capability-test-model");
        info.GetProperty("providerKey").GetString().Should().Be("groq");
        info.GetProperty("decryptedApiKey").GetString().Should().Be("secret-key");

        var localPathPayload = JsonDocument.Parse(await localPathResponse.Content.ReadAsStringAsync());
        localPathPayload.RootElement.GetProperty("path").GetString().Should().Be("E:/models/demo.gguf");

        modelInfo.ProviderInfoModelId.Should().Be(modelId);
        modelInfo.LocalPathModelId.Should().Be(modelId);
    }

    private static HttpClient CreateClient(ForeignModuleHostCapabilityServer server)
    {
        var client = new HttpClient { BaseAddress = server.Address };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            ForeignModuleProtocol.TokenHeaderName,
            server.Token);
        return client;
    }

    private sealed class RecordingConfigStore : IModuleConfigStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_values.GetValueOrDefault(key));

        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
            where T : IParsable<T>
        {
            var value = _values.GetValueOrDefault(key);
            return Task.FromResult(value is not null
                && T.TryParse(value, null, out var parsed)
                    ? parsed
                    : default);
        }

        public Task SetAsync(string key, string? value, CancellationToken ct = default)
        {
            if (value is null)
                _values.Remove(key);
            else
                _values[key] = value;

            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string>(_values, StringComparer.OrdinalIgnoreCase));
    }

    private sealed class RecordingJobController : IAgentJobController
    {
        public List<(Guid JobId, string Message, string Level)> Logs { get; } = [];
        public List<(Guid JobId, string? ResultData, string? Message)> Completed { get; } = [];
        public List<(Guid JobId, string Message, string? Details)> Failed { get; } = [];
        public List<(Guid JobId, string? Message)> Cancelled { get; } = [];
        public List<string> CancelledStalePrefixes { get; } = [];

        public Task<AgentJobResponse> SubmitJobAsync(
            Guid channelId,
            SubmitAgentJobRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AgentJobResponse?> StopJobAsync(
            Guid jobId,
            string? requiredActionPrefix = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task AddJobLogAsync(
            Guid jobId,
            string message,
            string level = "Info",
            CancellationToken ct = default)
        {
            Logs.Add((jobId, message, level));
            return Task.CompletedTask;
        }

        public Task MarkJobCompletedAsync(
            Guid jobId,
            string? resultData = null,
            string? message = null,
            CancellationToken ct = default)
        {
            Completed.Add((jobId, resultData, message));
            return Task.CompletedTask;
        }

        public Task MarkJobFailedAsync(
            Guid jobId,
            Exception exception,
            CancellationToken ct = default)
        {
            Failed.Add((jobId, exception.Message, exception.ToString()));
            return Task.CompletedTask;
        }

        public Task MarkJobFailedAsync(
            Guid jobId,
            string message,
            string? details = null,
            CancellationToken ct = default)
        {
            Failed.Add((jobId, message, details));
            return Task.CompletedTask;
        }

        public Task MarkJobCancelledAsync(
            Guid jobId,
            string? message = null,
            CancellationToken ct = default)
        {
            Cancelled.Add((jobId, message));
            return Task.CompletedTask;
        }

        public Task CancelStaleJobsByActionPrefixAsync(
            string actionKeyPrefix,
            CancellationToken ct = default)
        {
            CancelledStalePrefixes.Add(actionKeyPrefix);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingJobReader : IAgentJobReader
    {
        public Guid JobId { get; } = Guid.Parse("12121212-1212-1212-1212-121212121212");
        public Guid ChannelId { get; } = Guid.Parse("23232323-2323-2323-2323-232323232323");
        public Guid AgentId { get; } = Guid.Parse("34343434-3434-3434-3434-343434343434");
        public Guid ResourceId { get; } = Guid.Parse("45454545-4545-4545-4545-454545454545");
        public (string Prefix, Guid? ResourceId)? ListRequest { get; private set; }
        public (string Prefix, Guid? ResourceId)? SummaryRequest { get; private set; }

        public Task<AgentJobResponse?> GetJobAsync(Guid jobId, CancellationToken ct = default) =>
            Task.FromResult<AgentJobResponse?>(jobId == JobId ? Job() : null);

        public Task<IReadOnlyList<AgentJobResponse>> ListJobsByActionPrefixAsync(
            string actionKeyPrefix,
            Guid? resourceId = null,
            CancellationToken ct = default)
        {
            ListRequest = (actionKeyPrefix, resourceId);
            return Task.FromResult<IReadOnlyList<AgentJobResponse>>([Job()]);
        }

        public Task<IReadOnlyList<AgentJobSummaryResponse>> ListJobSummariesByActionPrefixAsync(
            string actionKeyPrefix,
            Guid? resourceId = null,
            CancellationToken ct = default)
        {
            SummaryRequest = (actionKeyPrefix, resourceId);
            return Task.FromResult<IReadOnlyList<AgentJobSummaryResponse>>([Summary()]);
        }

        public Task<bool> JobExistsWithActionPrefixAsync(
            Guid jobId,
            string actionKeyPrefix,
            CancellationToken ct = default) =>
            Task.FromResult(jobId == JobId && actionKeyPrefix == "cur_");

        private AgentJobResponse Job() =>
            new(
                JobId,
                ChannelId,
                AgentId,
                "cur_transcribe_audio_device",
                ResourceId,
                AgentJobStatus.Executing,
                PermissionClearance.Independent,
                ResultData: null,
                ErrorLog: null,
                Logs: [],
                CreatedAt: DateTimeOffset.UnixEpoch,
                StartedAt: DateTimeOffset.UnixEpoch,
                CompletedAt: null);

        private AgentJobSummaryResponse Summary() =>
            new(
                JobId,
                ChannelId,
                AgentId,
                "cur_transcribe_audio_device",
                ResourceId,
                AgentJobStatus.Executing,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                CompletedAt: null);
    }

    private sealed class RecordingProtocolContractResolver : IForeignModuleProtocolContractResolver
    {
        private readonly ForeignModuleProtocolContractExport _export = CreateExport();

        public RecordingProtocolContractInvoker Invoker { get; } = new();

        public IForeignModuleProtocolContractInvoker? Resolve(string contractName) =>
            contractName == _export.ContractName ? Invoker : null;

        public IReadOnlyList<ForeignModuleProtocolContractExport> GetAllExports() => [_export];

        private static ForeignModuleProtocolContractExport CreateExport() =>
            new(
                "editor_bridge",
                EmptyObjectSchema(),
                [
                    new ForeignModuleProtocolContractOperation(
                        "open_file",
                        EmptyObjectSchema(),
                        EmptyObjectSchema())
                ]);
    }

    private sealed class RecordingProtocolContractInvoker : IForeignModuleProtocolContractInvoker
    {
        public string ContractName => "editor_bridge";
        public IReadOnlyList<ForeignModuleProtocolContractOperation> Operations => [];
        public JsonElement LastParameters { get; private set; }

        public Task<JsonElement> InvokeAsync(
            string operation,
            JsonElement parameters,
            CancellationToken ct = default)
        {
            LastParameters = parameters.Clone();
            using var document = JsonDocument.Parse(
                $$"""{"operation":"{{operation}}","ok":true}""");
            return Task.FromResult(document.RootElement.Clone());
        }
    }

    private static JsonElement EmptyObjectSchema()
    {
        using var document = JsonDocument.Parse("""{"type":"object","properties":{}}""");
        return document.RootElement.Clone();
    }

    private sealed class RecordingTaskAuthoring : ITaskAuthoring
    {
        public Guid TaskId { get; } = Guid.Parse("11111111-1111-1111-1111-111111111111");
        public string? ValidatedSource { get; private set; }
        public string? CreatedSource { get; private set; }
        public (Guid Id, string? SourceText, bool? IsActive)? Updated { get; private set; }
        public Guid? DeletedId { get; private set; }

        public TaskValidationResponse ValidateDefinition(string sourceText)
        {
            ValidatedSource = sourceText;
            return new TaskValidationResponse(true, []);
        }

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

        public Task<bool> DeleteDefinitionAsync(Guid id, CancellationToken ct = default)
        {
            DeletedId = id;
            return Task.FromResult(true);
        }

        private static TaskDefinitionResponse Response(Guid id) =>
            new(
                id,
                "Sample Task",
                null,
                null,
                true,
                [],
                [],
                [],
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch);
    }

    private sealed class RecordingTaskLauncher : ITaskInstanceLauncher
    {
        public Guid InstanceId { get; } = Guid.Parse("44444444-4444-4444-4444-444444444444");
        public (Guid TaskDefinitionId, IReadOnlyDictionary<string, string>? ParameterValues, Guid? CallerAgentId, Guid? ChannelId, Guid? ContextId)? LastLaunch { get; private set; }

        public Task<Guid> LaunchAsync(
            Guid taskDefinitionId,
            IReadOnlyDictionary<string, string>? parameterValues,
            Guid? callerAgentId,
            Guid? channelId,
            Guid? contextId,
            CancellationToken ct)
        {
            LastLaunch = (taskDefinitionId, parameterValues, callerAgentId, channelId, contextId);
            return Task.FromResult(InstanceId);
        }
    }

    private sealed class RecordingCoreEntityIds : ICoreEntityIdProvider
    {
        public Guid AgentId { get; } = Guid.Parse("55555555-5555-5555-5555-555555555555");
        public Guid ChannelId { get; } = Guid.Parse("66666666-6666-6666-6666-666666666666");

        public Task<List<Guid>> GetAgentIdsAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<Guid> { AgentId });

        public Task<List<Guid>> GetChannelIdsAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<Guid> { ChannelId });

        public Task<List<(Guid Id, string Name)>> GetAgentLookupItemsAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<(Guid Id, string Name)> { (AgentId, "Analyst") });

        public Task<List<(Guid Id, string Name)>> GetChannelLookupItemsAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<(Guid Id, string Name)> { (ChannelId, "Clinic") });
    }

    private sealed class RecordingQueueMetrics : IHostQueueMetrics
    {
        public Task<double> GetPendingJobCountAsync(CancellationToken ct) => Task.FromResult(2d);
        public Task<double> GetPendingTaskCountAsync(CancellationToken ct) => Task.FromResult(3d);
        public Task<double> GetSchedulerPendingJobCountAsync(CancellationToken ct) => Task.FromResult(5d);
    }

    private sealed class RecordingConversationSteering : IConversationSteering
    {
        private readonly Guid _messageId = Guid.Parse("30303030-3030-3030-3030-303030303030");

        public ConversationSteeringRequest? LastAdd { get; private set; }
        public (Guid ChannelId, Guid? ThreadId, int Limit)? LastList { get; private set; }

        public Task<ConversationSteeringResponse> AddAsync(
            ConversationSteeringRequest request,
            CancellationToken ct = default)
        {
            LastAdd = request;
            return Task.FromResult(new ConversationSteeringResponse(
                _messageId,
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
            LastList = (channelId, threadId, limit);
            IReadOnlyList<ConversationSteeringResponse> rows =
            [
                new(
                    _messageId,
                    channelId,
                    threadId,
                    "listed steering",
                    DateTimeOffset.UnixEpoch,
                    "module_dev",
                    "build"),
            ];
            return Task.FromResult(rows);
        }
    }

    private sealed class RecordingAgentManager : IAgentManager
    {
        public Guid AgentId { get; } = Guid.Parse("77777777-7777-7777-7777-777777777777");
        public Guid ChannelId { get; } = Guid.Parse("88888888-8888-8888-8888-888888888888");
        public Guid ModelId { get; } = Guid.Parse("99999999-9999-9999-9999-999999999999");
        public (string Name, Guid ModelId, string? SystemPrompt)? Created { get; private set; }
        public (Guid AgentId, string? Name, string? SystemPrompt, Guid? ModelId)? Updated { get; private set; }
        public (Guid AgentId, string? Header)? AgentHeader { get; private set; }
        public (Guid ChannelId, string? Header)? ChannelHeader { get; private set; }

        public Task<(Guid AgentId, string ModelName, string AgentName)> CreateSubAgentAsync(
            string name,
            Guid modelId,
            string? systemPrompt,
            CancellationToken ct = default)
        {
            Created = (name, modelId, systemPrompt);
            return Task.FromResult((AgentId, "gpt-test", name));
        }

        public Task<string> UpdateAgentAsync(
            Guid agentId,
            string? name,
            string? systemPrompt,
            Guid? modelId,
            CancellationToken ct = default)
        {
            Updated = (agentId, name, systemPrompt, modelId);
            return Task.FromResult("updated");
        }

        public Task SetAgentHeaderAsync(Guid agentId, string? header, CancellationToken ct = default)
        {
            AgentHeader = (agentId, header);
            return Task.CompletedTask;
        }

        public Task SetChannelHeaderAsync(Guid channelId, string? header, CancellationToken ct = default)
        {
            ChannelHeader = (channelId, header);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingModuleLifecycle : IModuleLifecycleManager
    {
        private readonly EchoModule _module = new();
        public string ExternalModulesDir => "E:/source/SharpClaw/external-modules";
        public string? LoadedDir { get; private set; }
        public string? UnloadedId { get; private set; }
        public string? ReloadedId { get; private set; }

        public bool IsModuleRegistered(string moduleId) => moduleId == "loaded_module";
        public bool IsToolPrefixRegistered(string toolPrefix) => toolPrefix == "lm";
        public (ISharpClawCoreModule Module, string ToolName)? FindToolByName(string toolName) =>
            toolName == "echo_tool" ? (_module, "echo_tool") : null;

        public Task<ModuleStateResponse> LoadExternalAsync(
            string moduleDir,
            IServiceProvider hostServices,
            CancellationToken ct = default)
        {
            LoadedDir = moduleDir;
            return Task.FromResult(State("loaded_module"));
        }

        public Task UnloadExternalAsync(string moduleId, CancellationToken ct = default)
        {
            UnloadedId = moduleId;
            return Task.CompletedTask;
        }

        public Task<ModuleStateResponse> ReloadExternalAsync(
            string moduleId,
            IServiceProvider hostServices,
            CancellationToken ct = default)
        {
            ReloadedId = moduleId;
            return Task.FromResult(State(moduleId));
        }

        private static ModuleStateResponse State(string moduleId) =>
            new(moduleId, "Loaded Module", "lm", true, "1.0.0", true, true, null, null);
    }

    private sealed class RecordingModuleInfo : IModuleInfoProvider
    {
        public IReadOnlyList<ModuleInfo> GetAllModules() =>
        [
            new("loaded_module", "lm", ["editor_bridge"])
        ];
    }

    private sealed class RecordingModuleStorageGateway : IModuleStorageGateway
    {
        public (string ModuleId, string StorageName, string Operation, JsonElement Parameters)? LastInvocation { get; private set; }

        public IReadOnlyList<ModuleStorageContractDescriptor> ListContracts() =>
        [
            new(
                "sample_module",
                "records",
                [new ModuleStorageOperationDescriptor("get")])
        ];

        public Task<JsonElement> InvokeAsync(
            string moduleId,
            string storageName,
            string operation,
            JsonElement parameters,
            CancellationToken ct = default)
        {
            LastInvocation = (moduleId, storageName, operation, parameters.Clone());
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                value = parameters.GetProperty("id").GetString() + ":" + operation,
            }));
        }
    }

    private sealed class RecordingModelRegistrar : IModelRegistrar
    {
        public Guid ProviderId { get; } = Guid.NewGuid();
        public Guid ModelId { get; } = Guid.NewGuid();
        public (string ProviderKey, string DisplayName)? EnsureProviderCall { get; private set; }
        public (string ModelName, Guid ProviderId, IReadOnlyList<string> CapabilityTags)? EnsureModelCall { get; private set; }
        public Guid? MetadataModelId { get; private set; }
        public Guid? DeletedModelId { get; private set; }

        public Task<Guid> EnsureProviderAsync(
            string providerKey,
            string displayName,
            CancellationToken ct = default)
        {
            EnsureProviderCall = (providerKey, displayName);
            return Task.FromResult(ProviderId);
        }

        public Task<Guid> EnsureModelAsync(
            string modelName,
            Guid providerId,
            IReadOnlyList<string> capabilityTags,
            CancellationToken ct = default)
        {
            EnsureModelCall = (modelName, providerId, capabilityTags);
            return Task.FromResult(ModelId);
        }

        public Task<ModelMetadata?> GetModelMetadataAsync(
            Guid modelId,
            CancellationToken ct = default)
        {
            MetadataModelId = modelId;
            return Task.FromResult<ModelMetadata?>(
                new(
                    "demo.gguf",
                    ProviderId,
                    "Local",
                    "local",
                    CustomId: null,
                    CapabilityTags: new HashSet<string>(StringComparer.Ordinal) { "chat" }));
        }

        public Task<bool> DeleteModelAsync(Guid modelId, CancellationToken ct = default)
        {
            DeletedModelId = modelId;
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingModelInfoProvider : IModelInfoProvider
    {
        public Guid? ProviderInfoModelId { get; private set; }
        public Guid? LocalPathModelId { get; private set; }

        public Task<ModelProviderInfo?> GetModelProviderInfoAsync(
            Guid modelId,
            CancellationToken ct = default)
        {
            ProviderInfoModelId = modelId;
            return Task.FromResult<ModelProviderInfo?>(
                new("capability-test-model", "groq", "secret-key"));
        }

        public Task<string?> GetLocalModelFilePathAsync(
            Guid modelId,
            CancellationToken ct = default)
        {
            LocalPathModelId = modelId;
            return Task.FromResult<string?>("E:/models/demo.gguf");
        }
    }

    private sealed class EchoModule : ISharpClawCoreModule
    {
        public string Id => "echo_module";
        public string DisplayName => "Echo Module";
        public string ToolPrefix => "echo";

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];

        public Task<string> ExecuteToolAsync(
            string toolName,
            JsonElement parameters,
            AgentJobContext job,
            IServiceProvider scopedServices,
            CancellationToken ct) =>
            Task.FromResult("echo:" + parameters.GetProperty("value").GetString());
    }
}
