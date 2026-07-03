using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Core.Modules.Foreign;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Core.Modules;
using SharpClaw.Modules.Hosting;
using SharpClaw.Providers.Common;
using SharpClaw.Utils.Security;
using SharpClaw.Core.Modules.Foreign;

var process = await DotNetSidecarHost.CreateAsync(args);
await process.RunAsync();

internal sealed class DotNetSidecarHost
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly WebApplication _app;
    private readonly ModuleLoadContext _loadContext;
    private readonly ISharpClawCoreModule _module;
    private readonly ModuleManifest _manifest;
    private readonly ModuleManifestRuntimeInfo _runtimeInfo;
    private readonly string _controlToken;

    private DotNetSidecarHost(
        WebApplication app,
        ModuleLoadContext loadContext,
        ISharpClawCoreModule module,
        ModuleManifest manifest,
        ModuleManifestRuntimeInfo runtimeInfo,
        string controlToken)
    {
        _app = app;
        _loadContext = loadContext;
        _module = module;
        _manifest = manifest;
        _runtimeInfo = runtimeInfo;
        _controlToken = controlToken;
    }

    public static async Task<DotNetSidecarHost> CreateAsync(string[] args)
    {
        var moduleDir = ReadRequiredEnv(ForeignModuleProtocol.ModuleDirectoryEnv);
        var controlAddress = new Uri(ReadRequiredEnv(ForeignModuleProtocol.ControlAddressEnv));
        var controlToken = ReadRequiredEnv(ForeignModuleProtocol.ControlTokenEnv);
        var manifestPath = PathGuard.EnsureContainedIn(
            Path.Combine(moduleDir, "module.json"),
            moduleDir);
        var manifestJson = await File.ReadAllTextAsync(manifestPath);
        var manifest = JsonSerializer.Deserialize<ModuleManifest>(manifestJson, SecureJsonOptions.Manifest)
            ?? throw new InvalidOperationException($"Failed to parse module manifest '{manifestPath}'.");
        var runtimeInfo = ModuleManifestRuntimeInfo.FromJson(manifestJson);
        runtimeInfo.EnsureDotNetEntryAssembly(manifest);

        if (!runtimeInfo.IsSidecarHostMode)
        {
            throw new InvalidOperationException(
                $"Module '{manifest.Id}' must set hostMode to '{ModuleManifestRuntimeInfo.HostModeSidecar}' " +
                "to run in the shared .NET sidecar host.");
        }

        var entryAssemblyPath = PathGuard.EnsureContainedIn(
            Path.Combine(moduleDir, manifest.EntryAssembly),
            moduleDir);
        if (!File.Exists(entryAssemblyPath))
            throw new FileNotFoundException(
                $"Entry assembly '{manifest.EntryAssembly}' not found in '{moduleDir}'.",
                entryAssemblyPath);

        var loadContext = new ModuleLoadContext(entryAssemblyPath);
        var assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(entryAssemblyPath));
        var module = DotNetModuleAssemblyLoader.CreateModuleInstance(
            assembly,
            manifest,
            runtimeInfo,
            entryAssemblyPath);

        if (!string.Equals(module.Id, manifest.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Module class Id '{module.Id}' does not match manifest id '{manifest.Id}'.");
        }

        if (!string.Equals(module.ToolPrefix, manifest.ToolPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Module class ToolPrefix '{module.ToolPrefix}' does not match manifest toolPrefix '{manifest.ToolPrefix}'.");
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = moduleDir,
        });
        builder.WebHost.UseUrls(controlAddress.ToString());
        builder.Services.AddHttpClient();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<ICliIdResolver, SidecarCliIdResolver>();
        DotNetSidecarHostCapabilityProxies.Register(builder.Services);
        module.ConfigureServices(builder.Services);
        RegisterTaskStepDescriptorProviders(builder.Services, module.GetType().Assembly);

        var app = builder.Build();
        app.UseWebSockets();
        app.Use(async (context, next) =>
        {
            if (!HasExpectedToken(context, controlToken))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" }, JsonOptions);
                return;
            }

            await next();
        });

        if (module is ISharpClawRuntimeModule runtimeModule)
            runtimeModule.MapEndpoints(app);
        var host = new DotNetSidecarHost(app, loadContext, module, manifest, runtimeInfo, controlToken);
        host.MapControlEndpoints();
        return host;
    }

    public Task RunAsync() => _app.RunAsync();

    private void MapControlEndpoints()
    {
        _app.MapPost(ForeignModuleProtocol.HandshakePath, () =>
            Json(new ForeignModuleHandshakeResponse(
                ForeignModuleProtocol.Version,
                _manifest.Id,
                _manifest.ToolPrefix,
                ModuleManifestRuntimeInfo.DotNet,
                ResolveRuntimeVersion(_module.GetType().Assembly),
                BuildCapabilities())));

        _app.MapGet(ForeignModuleProtocol.DiscoveryPath, () =>
            Json(BuildDiscovery()));

        _app.MapGet(ForeignModuleProtocol.HealthPath, async (CancellationToken ct) =>
            Json((await _module.HealthCheckAsync(ct)).ToForeignResponse()));

        _app.MapPost(ForeignModuleProtocol.InitializePath, async (CancellationToken ct) =>
        {
            await _module.InitializeAsync(_app.Services, ct);
            return Json(new ForeignModuleLifecycleResponse());
        });

        _app.MapPost(ForeignModuleProtocol.ShutdownPath, async (CancellationToken ct) =>
        {
            await _module.ShutdownAsync();
            _ = Task.Run(async () =>
            {
                await Task.Delay(50, CancellationToken.None);
                await _app.StopAsync(CancellationToken.None);
            }, CancellationToken.None);
            return Json(new ForeignModuleLifecycleResponse());
        });

        _app.MapPost(ForeignModuleProtocol.ToolExecutePath, async (
            ForeignModuleToolExecutionRequest request,
            CancellationToken ct) =>
        {
            var tool = _module.GetToolDefinitions()
                .FirstOrDefault(candidate => string.Equals(candidate.Name, request.ToolName, StringComparison.Ordinal));
            if (tool is null)
                return Results.NotFound(new { error = $"Tool '{request.ToolName}' not found." });

            using var scope = _app.Services.CreateScope();
            var result = await _module.ExecuteToolAsync(
                request.ToolName,
                NormalizeJsonObject(request.Parameters),
                request.Job.ToAgentJobContext(),
                scope.ServiceProvider,
                ct);
            var completionBehavior = _module.GetJobCompletionBehavior(
                request.ToolName,
                NormalizeJsonObject(request.Parameters),
                request.Job.ToAgentJobContext());
            return Json(new ForeignModuleToolExecutionResponse(result, completionBehavior));
        });

        _app.MapPost(ForeignModuleProtocol.ToolCompletionBehaviorPath, (
            ForeignModuleToolCompletionBehaviorRequest request) =>
            Json(new ForeignModuleToolCompletionBehaviorResponse(
                _module.GetJobCompletionBehavior(
                    request.ToolName,
                    NormalizeJsonObject(request.Parameters),
                    request.Job.ToAgentJobContext()))));

        _app.MapPost(ForeignModuleProtocol.ToolStreamPath, async (
            HttpContext context,
            ForeignModuleToolExecutionRequest request,
            CancellationToken ct) =>
        {
            using var scope = _app.Services.CreateScope();
            var stream = _module.ExecuteToolStreamingAsync(
                request.ToolName,
                NormalizeJsonObject(request.Parameters),
                request.Job.ToAgentJobContext(),
                scope.ServiceProvider,
                ct);

            if (stream is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsJsonAsync(
                    new { error = $"Tool '{request.ToolName}' is not streaming." },
                    JsonOptions,
                    ct);
                return;
            }

            context.Response.ContentType = "application/x-ndjson; charset=utf-8";
            await foreach (var chunk in stream.WithCancellation(ct))
                await WriteNdjsonAsync(context, new ForeignModuleToolStreamEvent(Delta: chunk), ct);

            await WriteNdjsonAsync(context, new ForeignModuleToolStreamEvent(IsFinal: true), ct);
        });

        _app.MapPost(ForeignModuleProtocol.InlineToolExecutePath, async (
            ForeignModuleInlineToolExecutionRequest request,
            CancellationToken ct) =>
        {
            var tool = _module.GetInlineToolDefinitions()
                .FirstOrDefault(candidate => string.Equals(candidate.Name, request.ToolName, StringComparison.Ordinal));
            if (tool is null)
                return Results.NotFound(new { error = $"Inline tool '{request.ToolName}' not found." });

            using var scope = _app.Services.CreateScope();
            var result = await _module.ExecuteInlineToolAsync(
                request.ToolName,
                NormalizeJsonObject(request.Parameters),
                request.Context.ToInlineToolContext(),
                scope.ServiceProvider,
                ct);
            return Json(new ForeignModuleToolExecutionResponse(result));
        });

        _app.MapPost(ForeignModuleProtocol.ContractInvokePath, async (
            ForeignModuleProtocolContractInvocationRequest request,
            CancellationToken ct) =>
        {
            using var scope = _app.Services.CreateScope();
            var invoker = scope.ServiceProvider
                .GetServices<IForeignModuleProtocolContractInvoker>()
                .FirstOrDefault(candidate => string.Equals(
                    candidate.ContractName,
                    request.ContractName,
                    StringComparison.Ordinal));

            if (invoker is null && _module is IForeignModuleProtocolContractExporter exporter)
                invoker = exporter.GetProtocolContractInvoker(request.ContractName);

            if (invoker is null)
                return Results.NotFound(new { error = $"Protocol contract '{request.ContractName}' was not found." });

            return Json(new ForeignModuleProtocolContractInvocationResponse(
                await invoker.InvokeAsync(request.Operation, NormalizeJsonObject(request.Parameters), ct)));
        });

        _app.MapPost(ForeignModuleProtocol.HeaderTagResolvePath, async (
            ForeignModuleHeaderTagResolveRequest request,
            CancellationToken ct) =>
        {
            var tag = _module.GetHeaderTags()?
                .FirstOrDefault(candidate => string.Equals(candidate.Name, request.Name, StringComparison.OrdinalIgnoreCase));
            if (tag is null)
                return Results.NotFound(new { error = $"Header tag '{request.Name}' not found." });

            using var scope = _app.Services.CreateScope();
            var value = request.Context is not null && tag.ResolveWithContext is not null
                ? await tag.ResolveWithContext(scope.ServiceProvider, request.Context, ct)
                : await tag.Resolve(scope.ServiceProvider, ct);
            return Json(new ForeignModuleHeaderTagResolveResponse(value));
        });

        _app.MapPost(ForeignModuleProtocol.ResourceIdsPath, async (
            ForeignModuleResourceRequest request,
            CancellationToken ct) =>
        {
            var descriptor = FindResourceDescriptor(request.ResourceType);
            using var scope = _app.Services.CreateScope();
            return Json(new ForeignModuleResourceIdsResponse(
                await descriptor.LoadAllIds(scope.ServiceProvider, ct)));
        });

        _app.MapPost(ForeignModuleProtocol.ResourceLookupPath, async (
            ForeignModuleResourceRequest request,
            CancellationToken ct) =>
        {
            var descriptor = FindResourceDescriptor(request.ResourceType);
            if (descriptor.LoadLookupItems is null)
                return Results.NotFound(new { error = $"Resource type '{request.ResourceType}' does not support lookup." });

            using var scope = _app.Services.CreateScope();
            var items = await descriptor.LoadLookupItems(scope.ServiceProvider, ct);
            return Json(new ForeignModuleResourceLookupResponse(
                [.. items.Select(item => new ForeignModuleResourceLookupItem(item.Id, item.Name))]));
        });

        _app.MapPost(ForeignModuleProtocol.CliExecutePath, async (
            ForeignModuleCliExecutionRequest request,
            CancellationToken ct) =>
        {
            if (_module is not ISharpClawRuntimeModule runtimeModule)
                return Results.NotFound(new { error = $"CLI command '{request.CommandName}' not found." });

            var command = (runtimeModule.GetCliCommands() ?? [])
                .FirstOrDefault(candidate => string.Equals(candidate.Name, request.CommandName, StringComparison.OrdinalIgnoreCase)
                                             || candidate.Aliases.Any(alias => string.Equals(alias, request.CommandName, StringComparison.OrdinalIgnoreCase)));
            if (command is null)
                return Results.NotFound(new { error = $"CLI command '{request.CommandName}' not found." });

            using var scope = _app.Services.CreateScope();
            var captured = await ConsoleCapture.RunAsync(
                () => command.Handler([.. request.Args], scope.ServiceProvider, ct));
            return Json(new ForeignModuleCliExecutionResponse(
                captured.Success,
                captured.Stdout,
                captured.Stderr));
        });

        MapProviderEndpoints();
        MapTaskRuntimeEndpoints();
    }

    private void MapProviderEndpoints()
    {
        _app.MapPost(ForeignModuleProtocol.ProviderModelsListPath, async (
            ForeignModuleProviderModelListRequest request,
            CancellationToken ct) =>
        {
            var plugin = FindProvider(request.ProviderKey);
            var client = ProviderCredentialBinding.CreateClient(
                plugin,
                new ProviderClientOptions(request.Endpoint),
                request.ApiKey);
            return Json(new ForeignModuleProviderModelListResponse(
                await client.ListModelIdsAsync(ct)));
        });

        _app.MapPost(ForeignModuleProtocol.ProviderCapabilitiesResolvePath, (
            ForeignModuleProviderCapabilitiesResolveRequest request) =>
            Json(new ForeignModuleProviderCapabilitiesResolveResponse(
                [.. FindProvider(request.ProviderKey).Capabilities.Resolve(request.ModelName)])));

        _app.MapPost(ForeignModuleProtocol.ProviderChatCompletionPath, async (
            ForeignModuleProviderChatCompletionRequest request,
            CancellationToken ct) =>
        {
            var plugin = FindProvider(request.ProviderKey);
            var client = ProviderCredentialBinding.CreateClient(
                plugin,
                new ProviderClientOptions(request.Endpoint),
                request.ApiKey);
            return Json(new ForeignModuleProviderChatCompletionResponse(
                await client.ChatCompletionAsync(
                    request.Model,
                    request.SystemPrompt,
                    request.Messages,
                    request.MaxCompletionTokens,
                    request.ProviderParameters,
                    request.CompletionParameters,
                    ct)));
        });

        _app.MapPost(ForeignModuleProtocol.ProviderChatCompletionWithToolsPath, async (
            ForeignModuleProviderChatCompletionWithToolsRequest request,
            CancellationToken ct) =>
        {
            var plugin = FindProvider(request.ProviderKey);
            var client = ProviderCredentialBinding.CreateClient(
                plugin,
                new ProviderClientOptions(request.Endpoint),
                request.ApiKey);
            return Json(new ForeignModuleProviderChatCompletionResponse(
                await client.ChatCompletionWithToolsAsync(
                    request.Model,
                    request.SystemPrompt,
                    request.Messages,
                    request.Tools,
                    request.MaxCompletionTokens,
                    request.ProviderParameters,
                    request.CompletionParameters,
                    ct)));
        });

        _app.MapPost(ForeignModuleProtocol.ProviderStreamChatCompletionWithToolsPath, async (
            HttpContext context,
            ForeignModuleProviderChatCompletionWithToolsRequest request,
            CancellationToken ct) =>
        {
            var plugin = FindProvider(request.ProviderKey);
            var client = ProviderCredentialBinding.CreateClient(
                plugin,
                new ProviderClientOptions(request.Endpoint),
                request.ApiKey);
            context.Response.ContentType = "application/x-ndjson; charset=utf-8";
            await foreach (var chunk in client.StreamChatCompletionWithToolsAsync(
                               request.Model,
                               request.SystemPrompt,
                               request.Messages,
                               request.Tools,
                               request.MaxCompletionTokens,
                               request.ProviderParameters,
                               request.CompletionParameters,
                               ct).WithCancellation(ct))
            {
                await WriteNdjsonAsync(context, chunk, ct);
            }
        });

        _app.MapPost(ForeignModuleProtocol.ProviderDeviceCodeStartPath, async (
            ForeignModuleProviderDeviceCodeStartRequest request,
            CancellationToken ct) =>
        {
            var flow = FindProvider(request.ProviderKey).DeviceCodeFlow
                ?? throw new NotSupportedException($"Provider '{request.ProviderKey}' does not support device code.");
            return Json(new ForeignModuleProviderDeviceCodeStartResponse(
                await flow.StartAsync(ct)));
        });

        _app.MapPost(ForeignModuleProtocol.ProviderDeviceCodePollPath, async (
            ForeignModuleProviderDeviceCodePollRequest request,
            CancellationToken ct) =>
        {
            var flow = FindProvider(request.ProviderKey).DeviceCodeFlow
                ?? throw new NotSupportedException($"Provider '{request.ProviderKey}' does not support device code.");
            return Json(new ForeignModuleProviderDeviceCodePollResponse(
                await flow.PollAsync(request.Session, ct)));
        });

        _app.MapPost(ForeignModuleProtocol.ProviderCostFeedPath, async (
            ForeignModuleProviderCostFeedRequest request,
            CancellationToken ct) =>
        {
            var plugin = FindProvider(request.ProviderKey);
            var costFeed = ProviderCredentialBinding.CreateCostFeed(
                plugin,
                new ProviderClientOptions(null),
                request.ApiKey)
                ?? throw new NotSupportedException($"Provider '{request.ProviderKey}' does not support costs.");
            return Json(new ForeignModuleProviderCostFeedResponse(
                await costFeed.GetCostsAsync(
                    request.StartTime,
                    request.EndTime,
                    ct)));
        });

        _app.MapPost(ForeignModuleProtocol.ProviderAgentIdentifierSuffixPath, async (
            ForeignModuleProviderAgentIdentifierSuffixRequest request,
            CancellationToken ct) =>
            Json(new ForeignModuleProviderAgentIdentifierSuffixResponse(
                await FindProvider(request.ProviderKey)
                    .GetAgentIdentifierSuffixAsync(request.ProviderName, request.ModelId, ct))));
    }

    private void MapTaskRuntimeEndpoints()
    {
        _app.MapPost(ForeignModuleProtocol.TaskStepExecutePath, async (
            ForeignModuleTaskStepExecutionRequest request,
            CancellationToken ct) =>
        {
            using var scope = _app.Services.CreateScope();
            var executor = ResolveTaskStepExecutor(scope.ServiceProvider, request.StepKey);
            var context = new DotNetSidecarTaskStepExecutionContext(
                scope.ServiceProvider,
                request.Context,
                ct);
            var shouldContinue = await executor.ExecuteAsync(
                request.StepKey,
                context,
                request.Arguments,
                request.Expression,
                request.ResultVariable);
            return Json(context.ToResponse(shouldContinue));
        });

        _app.MapPost(ForeignModuleProtocol.TaskStepInvokePath, async (
            ForeignModuleTaskStepInvocationRequest request,
            CancellationToken ct) =>
        {
            using var scope = _app.Services.CreateScope();
            var executor = ResolveTaskStepExecutor(scope.ServiceProvider, request.Step.StepKey);
            if (executor is not ITaskStepInvocationExecutor invocationExecutor)
            {
                throw new NotSupportedException(
                    $"Task step '{request.Step.StepKey}' does not support invocation execution.");
            }

            var context = new DotNetSidecarTaskStepExecutionContext(
                scope.ServiceProvider,
                request.Context,
                ct);
            var result = await invocationExecutor.ExecuteInvocationAsync(
                new DotNetSidecarTaskStepInvocation(request.Step),
                context);
            return Json(context.ToResponse(result, request.Step.ResultVariable));
        });

        _app.MapPost(ForeignModuleProtocol.TaskTriggerAttributeHandlePath, (
            ForeignModuleTaskTriggerAttributeHandleRequest request) =>
        {
            if (_module is not ITaskParserAware parserAware
                || !parserAware.ParserExtension.TriggerAttributeHandlers.TryGetValue(
                    request.HandlerName,
                    out var handler))
            {
                return Results.NotFound(new { error = $"Task trigger attribute handler '{request.HandlerName}' not found." });
            }

            var context = new DotNetSidecarTaskTriggerAttributeContext(request.Context);
            return Json(new ForeignModuleTaskTriggerAttributeHandleResponse(
                handler.Handle(context),
                context.Diagnostics));
        });

        _app.MapPost(ForeignModuleProtocol.TaskTriggerStartPath, async (
            ForeignModuleTaskTriggerStartRequest request,
            CancellationToken ct) =>
        {
            using var scope = _app.Services.CreateScope();
            var source = ResolveTaskTriggerSource(scope.ServiceProvider, request.TriggerKeys);
            var contexts = request.Contexts
                .Select(context => new DotNetSidecarTaskTriggerSourceContext(
                    context,
                    scope.ServiceProvider.GetRequiredService<ITaskInstanceLauncher>()))
                .Cast<ITaskTriggerSourceContext>()
                .ToArray();
            await source.StartAsync(contexts, ct);
            return Json(new ForeignModuleTaskAckResponse());
        });

        _app.MapPost(ForeignModuleProtocol.TaskTriggerStopPath, async (
            ForeignModuleTaskTriggerStopRequest request) =>
        {
            using var scope = _app.Services.CreateScope();
            var source = ResolveTaskTriggerSource(scope.ServiceProvider, request.TriggerKeys);
            await source.StopAsync();
            return Json(new ForeignModuleTaskAckResponse());
        });

        _app.MapPost(ForeignModuleProtocol.TaskTriggerBindingValuePath, (
            ForeignModuleTaskTriggerDefinitionRequest request) =>
        {
            using var scope = _app.Services.CreateScope();
            var source = ResolveTaskTriggerSource(scope.ServiceProvider, [request.TriggerKey]);
            return Json(new ForeignModuleTaskTriggerBindingValueResponse(
                source.GetBindingValue(request.Definition)));
        });

        _app.MapPost(ForeignModuleProtocol.TaskTriggerBindingFilterPath, (
            ForeignModuleTaskTriggerDefinitionRequest request) =>
        {
            using var scope = _app.Services.CreateScope();
            var source = ResolveTaskTriggerSource(scope.ServiceProvider, [request.TriggerKey]);
            return Json(new ForeignModuleTaskTriggerBindingValueResponse(
                source.GetBindingFilter(request.Definition)));
        });

        _app.MapPost(ForeignModuleProtocol.TaskTriggerSyncBindingsPath, async (
            ForeignModuleTaskTriggerSyncBindingsRequest request,
            CancellationToken ct) =>
        {
            using var scope = _app.Services.CreateScope();
            var source = ResolveTaskTriggerSource(scope.ServiceProvider, request.TriggerKeys);
            var changed = await source.SyncBindingsAsync(
                request.Definition,
                request.OwnedTriggers,
                ct);
            return Json(new ForeignModuleTaskTriggerSyncBindingsResponse(changed));
        });

        _app.MapPost(ForeignModuleProtocol.TaskTriggerRemoveBindingsPath, async (
            ForeignModuleTaskTriggerRemoveBindingsRequest request,
            CancellationToken ct) =>
        {
            using var scope = _app.Services.CreateScope();
            var source = ResolveTaskTriggerSource(scope.ServiceProvider, request.TriggerKeys);
            await source.RemoveBindingsAsync(request.DefinitionId, ct);
            return Json(new ForeignModuleTaskAckResponse());
        });

        _app.MapPost(ForeignModuleProtocol.TaskTriggerBindingCreatedPath, async (
            ForeignModuleTaskTriggerBindingCreatedRequest request,
            CancellationToken ct) =>
        {
            using var scope = _app.Services.CreateScope();
            var sideEffect = ResolveTaskTriggerBindingSideEffect(scope.ServiceProvider, request.TriggerKey);
            await sideEffect.OnBindingCreatedAsync(
                request.Definition,
                request.Trigger,
                request.Binding,
                ct);
            return Json(new ForeignModuleTaskAckResponse());
        });

        _app.MapPost(ForeignModuleProtocol.TaskTriggerBindingRemovedPath, async (
            ForeignModuleTaskTriggerBindingRemovedRequest request,
            CancellationToken ct) =>
        {
            using var scope = _app.Services.CreateScope();
            var sideEffect = ResolveTaskTriggerBindingSideEffect(scope.ServiceProvider, request.TriggerKey);
            await sideEffect.OnBindingRemovedAsync(request.Binding, ct);
            return Json(new ForeignModuleTaskAckResponse());
        });

        _app.MapPost(ForeignModuleProtocol.TaskMetricValuePath, async (
            ForeignModuleTaskMetricValueRequest request,
            CancellationToken ct) =>
        {
            using var scope = _app.Services.CreateScope();
            var metric = scope.ServiceProvider.GetServices<ITaskMetricProvider>()
                .FirstOrDefault(candidate => string.Equals(
                    candidate.MetricName,
                    request.MetricName,
                    StringComparison.Ordinal))
                ?? throw new NotSupportedException($"Task metric '{request.MetricName}' not found.");
            return Json(new ForeignModuleTaskMetricValueResponse(
                await metric.GetValueAsync(ct)));
        });

        _app.MapPost(ForeignModuleProtocol.TaskEventSinkPath, async (
            HttpContext context,
            CancellationToken ct) =>
        {
            var request = await ReadTaskEventSinkRequestAsync(context, ct);
            using var scope = _app.Services.CreateScope();
            foreach (var sink in scope.ServiceProvider.GetServices<ISharpClawEventSink>()
                         .Where(sink => (sink.SubscribedEvents & request.Event.Type) != 0))
            {
                await sink.OnEventAsync(request.Event, ct);
            }

            return Json(new ForeignModuleTaskAckResponse());
        });
    }

    private ForeignModuleDiscoveryResponse BuildDiscovery()
    {
        var protocolModule = _module as IForeignModuleProtocolContractModule;
        var runtimeModule = _module as ISharpClawRuntimeModule;
        using var scope = _app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var taskStepDescriptors = services.GetServices<ITaskStepDescriptorProvider>()
            .SelectMany(provider => provider.Descriptors)
            .ToArray();
        var taskStepExecutors = services.GetServices<ITaskStepExecutorExtension>()
            .Select(executor => ToTaskStepExecutorDescriptor(executor, taskStepDescriptors))
            .Where(descriptor => descriptor.StepKeys.Count > 0)
            .ToArray();
        return new ForeignModuleDiscoveryResponse(
            Endpoints: DiscoverMappedEndpoints(),
            Tools: [.. _module.GetToolDefinitions().Select(tool => ToForeignTool(
                tool,
                HasStreamingOverride(_module),
                HasCompletionBehaviorOverride(_module)))],
            InlineTools: [.. _module.GetInlineToolDefinitions().Select(ToForeignInlineTool)],
            ProtocolContracts: [.. (protocolModule?.ExportedProtocolContracts ?? []).Select(ToExportDescriptor)],
            RequiredProtocolContracts: [.. (protocolModule?.RequiredProtocolContracts ?? []).Select(ToRequirementDescriptor)],
            HeaderTags: [.. (_module.GetHeaderTags() ?? []).Select(ToHeaderTagDescriptor)],
            ResourceTypes: [.. _module.GetResourceTypeDescriptors().Select(ToResourceDescriptor)],
            GlobalFlags: [.. _module.GetGlobalFlagDescriptors().Select(flag => new ForeignModuleGlobalFlagDescriptor(
                flag.FlagKey,
                flag.DisplayName,
                flag.Description,
                flag.DelegateMethodName))],
            UiContributions: runtimeModule?.GetUiContributions() ?? [],
            FrontendContributions: runtimeModule?.GetFrontendContributions() ?? [],
            StorageContracts: _module.GetStorageContracts(),
            CliCommands: [.. (runtimeModule?.GetCliCommands() ?? []).Select(command => new ForeignModuleCliCommandDescriptor(
                command.Name,
                command.Aliases,
                command.Scope,
                command.Description,
                command.UsageLines))],
            TaskParser: ToTaskParserDescriptor(_module as ITaskParserAware),
            TaskStepDescriptors: taskStepDescriptors,
            TaskStepExecutors: taskStepExecutors,
            TaskTriggerSources: [.. services.GetServices<ITaskTriggerSource>()
                .Select(source => new ForeignModuleTaskTriggerSourceDescriptor(
                    source.TriggerKeys,
                    source.OwnsBindingPersistence))
                .Where(source => source.TriggerKeys.Count > 0)],
            TaskTriggerBindingSideEffects: [.. services.GetServices<ITaskTriggerBindingSideEffect>()
                .Select(sideEffect => new ForeignModuleTaskTriggerBindingSideEffectDescriptor(
                    sideEffect.TriggerKey))],
            TaskMetricProviders: [.. services.GetServices<ITaskMetricProvider>()
                .Select(metric => new ForeignModuleTaskMetricProviderDescriptor(
                    metric.MetricName,
                    metric.Description))],
            TaskEventSinks: [.. services.GetServices<ISharpClawEventSink>()
                .Select(sink => new ForeignModuleTaskEventSinkDescriptor(
                    sink.SubscribedEvents))],
            ProviderPlugins: [.. services.GetServices<IProviderPlugin>().Select(ToProviderDescriptor)]);
    }

    private IReadOnlyList<ForeignModuleEndpointDescriptor> DiscoverMappedEndpoints()
    {
        var descriptors = new List<ForeignModuleEndpointDescriptor>();
        var dataSources = _app.Services.GetServices<EndpointDataSource>();
        foreach (var endpoint in dataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>())
        {
            var route = endpoint.RoutePattern.RawText;
            if (string.IsNullOrWhiteSpace(route)
                || route.StartsWith("/.sharpclaw/", StringComparison.Ordinal))
            {
                continue;
            }

            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            foreach (var method in methods is { Count: > 0 } ? methods : ["GET"])
            {
                descriptors.Add(new ForeignModuleEndpointDescriptor(
                    method,
                    route,
                    IsWebSocketEndpoint(endpoint, route, method)
                        ? ForeignModuleEndpointResponseMode.WebSocket
                        : ForeignModuleEndpointResponseMode.Raw,
                    AuthPolicy: endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null
                        ? ForeignModuleEndpointAuthPolicy.Anonymous
                        : null));
            }
        }

        return descriptors
            .DistinctBy(descriptor => (descriptor.Method, descriptor.RoutePattern))
            .OrderBy(descriptor => descriptor.RoutePattern, StringComparer.Ordinal)
            .ThenBy(descriptor => descriptor.Method, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsWebSocketEndpoint(RouteEndpoint endpoint, string route, string method)
    {
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            return false;

        if (route.EndsWith("/ws", StringComparison.OrdinalIgnoreCase)
            || route.EndsWith("/websocket", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return endpoint.Metadata
            .OfType<MethodInfo>()
            .Any(info => info.Name.Contains("WebSocket", StringComparison.OrdinalIgnoreCase)
                         || info.GetParameters().Any(parameter =>
                             parameter.ParameterType.FullName == "System.Net.WebSockets.WebSocket"));
    }

    private IProviderPlugin FindProvider(string providerKey) =>
        _app.Services.GetServices<IProviderPlugin>()
            .FirstOrDefault(provider => string.Equals(provider.ProviderKey, providerKey, StringComparison.Ordinal))
        ?? throw new NotSupportedException($"Provider '{providerKey}' was not found.");

    private ModuleResourceTypeDescriptor FindResourceDescriptor(string resourceType) =>
        _module.GetResourceTypeDescriptors()
            .FirstOrDefault(descriptor => string.Equals(descriptor.ResourceType, resourceType, StringComparison.Ordinal))
        ?? throw new NotSupportedException($"Resource type '{resourceType}' was not found.");

    private IReadOnlyList<string> BuildCapabilities()
    {
        var capabilities = new HashSet<string>(StringComparer.Ordinal)
        {
            ForeignModuleCapability.LifecycleHooks,
            ForeignModuleCapability.HostCapabilities,
        };

        var runtimeModule = _module as ISharpClawRuntimeModule;
        if (runtimeModule is not null && DiscoverMappedEndpoints().Count > 0)
            capabilities.Add(ForeignModuleCapability.Endpoints);
        if (_module.GetToolDefinitions().Count > 0) capabilities.Add(ForeignModuleCapability.JobTools);
        if (_module.GetInlineToolDefinitions().Count > 0) capabilities.Add(ForeignModuleCapability.InlineTools);
        if (runtimeModule?.GetFrontendContributions().Count > 0)
            capabilities.Add(ForeignModuleCapability.FrontendContributions);
        if (runtimeModule?.GetUiContributions().Count > 0)
            capabilities.Add(ForeignModuleCapability.ModuleContributionDescriptors);
        using var scope = _app.Services.CreateScope();
        var services = scope.ServiceProvider;
        if (_module is ITaskParserAware || HasTaskRuntimeContributions(services))
            capabilities.Add(ForeignModuleCapability.TaskRuntime);
        if (services.GetServices<IProviderPlugin>().Any()) capabilities.Add(ForeignModuleCapability.ProviderPlugins);

        return [.. capabilities.Order(StringComparer.Ordinal)];
    }

    private static ForeignModuleToolDescriptor ToForeignTool(
        ModuleToolDefinition tool,
        bool supportsStreaming,
        bool supportsDynamicCompletionBehavior) =>
        new(
            tool.Name,
            tool.Description,
            tool.ParametersSchema,
            ToForeignPermission(tool.Permission),
            tool.TimeoutSeconds,
            tool.Aliases,
            SupportsStreaming: supportsStreaming,
            SupportsDynamicCompletionBehavior: supportsDynamicCompletionBehavior);

    private static ForeignModuleInlineToolDescriptor ToForeignInlineTool(ModuleInlineToolDefinition tool) =>
        new(
            tool.Name,
            tool.Description,
            tool.ParametersSchema,
            tool.Permission is null ? null : ToForeignPermission(tool.Permission),
            tool.Aliases);

    private static ForeignModulePermissionDescriptor ToForeignPermission(ModuleToolPermission permission) =>
        new(permission.IsPerResource, permission.DelegateTo);

    private static ForeignModuleProtocolContractExportDescriptor ToExportDescriptor(
        ForeignModuleProtocolContractExport export) =>
        new(export.ContractName, export.Schema, export.Operations, export.Description);

    private static ForeignModuleProtocolContractRequirementDescriptor ToRequirementDescriptor(
        ForeignModuleProtocolContractRequirement requirement) =>
        new(requirement.ContractName, requirement.Schema, requirement.Optional, requirement.Description);

    private static ForeignModuleTaskParserDescriptor? ToTaskParserDescriptor(ITaskParserAware? parserAware)
    {
        if (parserAware is null)
            return null;

        var extension = parserAware.ParserExtension;
        return new ForeignModuleTaskParserDescriptor(
            StepKeyMappings: [.. extension.StepKeyMappings.Select(mapping =>
                new ForeignModuleTaskParserStepMapping(
                    mapping.Key,
                    mapping.Value.StepKey,
                    mapping.Value.ModuleId))],
            EventTriggerMappings: [.. extension.EventTriggerMappings.Select(mapping =>
                new ForeignModuleTaskParserEventMapping(
                    mapping.Key,
                    mapping.Value.TriggerKey,
                    mapping.Value.ModuleId))],
            SingleArgExpressionMethods: [.. extension.SingleArgExpressionMethods],
            TriggerAttributeHandlers: [.. extension.TriggerAttributeHandlers.Keys.Select(name =>
                new ForeignModuleTaskTriggerAttributeHandlerDescriptor(
                    name,
                    NamedStringArgs:
                    [
                        "Name",
                        "Timezone",
                        "Filter",
                        "Pattern",
                        "Direction",
                        "Events",
                    ],
                    NamedIntArgs:
                    [
                        "PollInterval",
                        "Count",
                        "Interval",
                        "Seconds",
                    ],
                    NamedDoubleArgs:
                    [
                        "Threshold",
                    ]))]);
    }

    private static ForeignModuleTaskStepExecutorDescriptor ToTaskStepExecutorDescriptor(
        ITaskStepExecutorExtension executor,
        IReadOnlyList<TaskStepDescriptor> descriptors) =>
        new(
            executor.ModuleId,
            [.. descriptors
                .Where(descriptor => string.Equals(descriptor.OwnerId, executor.ModuleId, StringComparison.Ordinal)
                                     && executor.CanExecute(descriptor.StepKey))
                .Select(descriptor => descriptor.StepKey)
                .Distinct(StringComparer.Ordinal)],
            SupportsInvocation: executor is ITaskStepInvocationExecutor);

    private static ForeignModuleHeaderTagDescriptor ToHeaderTagDescriptor(ModuleHeaderTag tag) =>
        new(tag.Name, SupportsContext: tag.ResolveWithContext is not null);

    private static ForeignModuleResourceTypeDescriptor ToResourceDescriptor(ModuleResourceTypeDescriptor descriptor) =>
        new(
            descriptor.ResourceType,
            descriptor.GrantLabel,
            descriptor.DelegateMethodName,
            descriptor.DefaultResourceKey,
            SupportsLookupItems: descriptor.LoadLookupItems is not null);

    private static ForeignModuleProviderPluginDescriptor ToProviderDescriptor(IProviderPlugin plugin)
    {
        var supportsNativeToolCalling = false;
        try
        {
            supportsNativeToolCalling = plugin
                .CreateClient(ProviderClientOptions.Empty)
                .SupportsNativeToolCalling;
        }
        catch when (plugin.RequiresEndpoint)
        {
        }

        return new ForeignModuleProviderPluginDescriptor(
            plugin.ProviderKey,
            plugin.DisplayName,
            string.IsNullOrWhiteSpace(plugin.OwnerModuleId) ? null : plugin.OwnerModuleId,
            plugin.RequiresEndpoint,
            plugin.SupportsAutomaticEndpointDiscovery,
            plugin.IsSeedable,
            plugin.RequiresApiKey,
            supportsNativeToolCalling,
            plugin.CostSeeds,
            ForeignModuleCompletionParameterSpecDescriptor.From(plugin.ParameterSpec),
            SupportsDeviceCodeFlow: plugin.DeviceCodeFlow is not null,
            SupportsCostFeed: plugin.SupportsCostFeed,
            CostFeedPermissionDeniedNote: plugin.SupportsCostFeed
                ? plugin.CostFeedPermissionDeniedNote
                : null);
    }

    private static bool HasStreamingOverride(ISharpClawCoreModule module) =>
        module.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Any(method => method.Name == nameof(ISharpClawCoreModule.ExecuteToolStreamingAsync));

    private static bool HasTaskRuntimeContributions(IServiceProvider services) =>
        services.GetServices<ITaskStepDescriptorProvider>().Any()
        || services.GetServices<ITaskStepExecutorExtension>().Any()
        || services.GetServices<ITaskTriggerSource>().Any()
        || services.GetServices<ITaskTriggerBindingSideEffect>().Any()
        || services.GetServices<ITaskMetricProvider>().Any()
        || services.GetServices<ISharpClawEventSink>().Any();

    private static ITaskStepExecutorExtension ResolveTaskStepExecutor(
        IServiceProvider services,
        string stepKey) =>
        services.GetServices<ITaskStepExecutorExtension>()
            .FirstOrDefault(executor => executor.CanExecute(stepKey))
        ?? throw new NotSupportedException($"Task step '{stepKey}' was not found.");

    private static ITaskTriggerSource ResolveTaskTriggerSource(
        IServiceProvider services,
        IReadOnlyList<string> triggerKeys) =>
        services.GetServices<ITaskTriggerSource>()
            .FirstOrDefault(source => source.TriggerKeys.Any(triggerKey =>
                triggerKeys.Contains(triggerKey, StringComparer.Ordinal)))
        ?? throw new NotSupportedException(
            $"Task trigger source '{string.Join(", ", triggerKeys)}' was not found.");

    private static ITaskTriggerBindingSideEffect ResolveTaskTriggerBindingSideEffect(
        IServiceProvider services,
        string triggerKey) =>
        services.GetServices<ITaskTriggerBindingSideEffect>()
            .FirstOrDefault(sideEffect => string.Equals(
                sideEffect.TriggerKey,
                triggerKey,
                StringComparison.Ordinal))
        ?? throw new NotSupportedException($"Task trigger binding side effect '{triggerKey}' was not found.");

    private static void RegisterTaskStepDescriptorProviders(IServiceCollection services, Assembly assembly)
    {
        var providerTypes = assembly.GetTypes()
            .Where(type => !type.IsAbstract
                           && !type.IsInterface
                           && typeof(ITaskStepDescriptorProvider).IsAssignableFrom(type)
                           && type.GetConstructor(Type.EmptyTypes) is not null);

        foreach (var providerType in providerTypes)
            services.AddSingleton(typeof(ITaskStepDescriptorProvider), providerType);
    }

    private static bool HasCompletionBehaviorOverride(ISharpClawCoreModule module) =>
        module.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Any(method => method.Name == nameof(ISharpClawCoreModule.GetJobCompletionBehavior));

    private static string ResolveRuntimeVersion(Assembly moduleAssembly)
    {
        var targetFramework = moduleAssembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
        return string.IsNullOrWhiteSpace(targetFramework)
            ? Environment.Version.ToString()
            : $"{targetFramework}; runtime={Environment.Version}";
    }

    private static bool HasExpectedToken(HttpContext context, string controlToken) =>
        context.Request.Headers.TryGetValue(ForeignModuleProtocol.TokenHeaderName, out var values)
        && values.Count == 1
        && string.Equals(values[0], controlToken, StringComparison.Ordinal);

    private static string ReadRequiredEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Required environment variable '{name}' is not set.");

    private static JsonElement NormalizeJsonObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Undefined)
            return element;

        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    private static async Task<ForeignModuleTaskEventSinkRequest> ReadTaskEventSinkRequestAsync(
        HttpContext context,
        CancellationToken ct)
    {
        using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
        var root = document.RootElement;
        var eventElement = root.GetProperty("event");
        var eventType = eventElement.GetProperty("type").Deserialize<SharpClawEventType>(JsonOptions);
        var timestamp = eventElement.GetProperty("timestamp").GetDateTimeOffset();
        var data = eventElement.TryGetProperty("data", out var dataElement)
                   && dataElement.ValueKind == JsonValueKind.Object
            ? dataElement.EnumerateObject().ToDictionary(
                property => property.Name,
                property => (object)property.Value.Clone(),
                StringComparer.Ordinal)
            : null;

        return new ForeignModuleTaskEventSinkRequest(
            root.GetProperty("protocolVersion").GetInt32(),
            root.GetProperty("moduleId").GetString() ?? string.Empty,
            new SharpClawEvent(
                eventType,
                timestamp,
                ReadNullableGuid(eventElement, "entityId"),
                ReadNullableGuid(eventElement, "secondaryEntityId"),
                ReadNullableString(eventElement, "sourceId"),
                ReadNullableString(eventElement, "summary"),
                data));
    }

    private static Guid? ReadNullableGuid(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
               && Guid.TryParse(property.GetString(), out var parsed)
            ? parsed
            : property.GetGuid();
    }

    private static string? ReadNullableString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static IResult Json<T>(T value) => Results.Json(value, JsonOptions);

    private static async Task WriteNdjsonAsync<T>(HttpContext context, T value, CancellationToken ct)
    {
        await context.Response.WriteAsync(JsonSerializer.Serialize(value, JsonOptions), ct);
        await context.Response.WriteAsync("\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }

    private sealed record ConsoleCaptureResult(bool Success, string? Stdout, string? Stderr);

    private sealed class DotNetSidecarTaskStepExecutionContext : ITaskStepExecutionContext
    {
        private readonly ForeignModuleTaskStepExecutionContextSnapshot _snapshot;
        private readonly Guid _initialChannelId;
        private readonly List<string> _logs = [];
        private readonly List<string?> _outputs = [];
        private readonly List<ForeignModuleTaskRegisteredEventHandlerDescriptor> _registeredEventHandlers = [];
        private readonly List<ITaskEventHandler> _eventHandlers = [];

        public DotNetSidecarTaskStepExecutionContext(
            IServiceProvider services,
            ForeignModuleTaskStepExecutionContextSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            _snapshot = snapshot;
            _initialChannelId = snapshot.ChannelId;
            InstanceId = snapshot.InstanceId;
            ChannelId = snapshot.ChannelId;
            CancellationToken = cancellationToken;
            Services = services;
            Variables = (snapshot.Variables ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal))
                .ToDictionary(
                    pair => pair.Key,
                    pair => ConvertJsonValue(pair.Value),
                    StringComparer.Ordinal);

            foreach (var handler in snapshot.EventHandlers ?? [])
            {
                _eventHandlers.Add(new DotNetSidecarTaskEventHandler(
                    handler.ModuleTriggerKey,
                    handler.ParameterName,
                    handler.HandlerCallbackId,
                    services.GetService<DotNetSidecarHostCapabilityClient>(),
                    () => ChannelId,
                    SnapshotVariables,
                    ApplyHostContextResponse));
            }
        }

        public Guid InstanceId { get; }
        public Guid ChannelId { get; private set; }
        public CancellationToken CancellationToken { get; }
        public IServiceProvider Services { get; }
        public IDictionary<string, object?> Variables { get; }
        public IReadOnlyList<ITaskEventHandler> EventHandlers => _eventHandlers;

        public string ResolveExpression(string expression) =>
            Variables.TryGetValue(expression, out var value)
                ? value?.ToString() ?? string.Empty
                : expression;

        public Task AppendLogAsync(string message)
        {
            _logs.Add(message);
            return Task.CompletedTask;
        }

        public Task WriteOutputAsync(string? outputJson)
        {
            _outputs.Add(outputJson);
            return Task.CompletedTask;
        }

        public void SetChannelId(Guid channelId) => ChannelId = channelId;

        public async Task<TaskStepResult> ExecuteStepsAsync(
            IReadOnlyList<ITaskStepInvocation> steps,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_snapshot.ContextCallbackId))
                throw new NotSupportedException("Nested task step execution requires an active host context callback.");

            var client = Services.GetService<DotNetSidecarHostCapabilityClient>()
                ?? throw new NotSupportedException("Nested task step execution requires host capability access.");
            var response = await client.PostAsync<
                ForeignModuleTaskContextExecuteStepsRequest,
                ForeignModuleTaskContextExecutionResponse>(
                ForeignModuleHostCapabilityProtocol.TaskContextExecuteStepsPath,
                new ForeignModuleTaskContextExecuteStepsRequest
                {
                    ContextId = _snapshot.ContextCallbackId,
                    ChannelId = ChannelId,
                    Variables = SnapshotVariables(),
                    Steps = [.. steps.Select(ForeignModuleTaskStepInvocationDescriptor.From)],
                },
                cancellationToken);
            ApplyHostContextResponse(response);
            return response.Result;
        }

        public bool EvaluateCondition(string? expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
                return false;

            var resolved = ResolveExpression(expression);
            return bool.TryParse(resolved, out var value) && value;
        }

        public void RegisterEventHandler(
            string moduleTriggerKey,
            string? parameterName,
            IReadOnlyList<ITaskStepInvocation> body)
        {
            var descriptorBody = body
                .Select(ForeignModuleTaskStepInvocationDescriptor.From)
                .ToArray();
            _registeredEventHandlers.Add(new ForeignModuleTaskRegisteredEventHandlerDescriptor(
                moduleTriggerKey,
                parameterName,
                descriptorBody));
            _eventHandlers.Add(new DotNetSidecarLocalTaskEventHandler(
                moduleTriggerKey,
                parameterName,
                this,
                body));
        }

        public Task WaitIfPausedAsync() => Task.CompletedTask;

        public ForeignModuleTaskStepExecutionResponse ToResponse(
            bool shouldContinue,
            string? resultVariable = null) =>
            ToResponse(
                shouldContinue ? TaskStepResult.Continue : TaskStepResult.Return,
                shouldContinue,
                resultVariable);

        public ForeignModuleTaskStepExecutionResponse ToResponse(
            TaskStepResult result,
            string? resultVariable = null) =>
            ToResponse(result, null, resultVariable);

        private ForeignModuleTaskStepExecutionResponse ToResponse(
            TaskStepResult result,
            bool? shouldContinue,
            string? resultVariable)
        {
            var variableUpdates = Variables.ToDictionary(
                pair => pair.Key,
                pair => SerializeVariableValue(pair.Value),
                StringComparer.Ordinal);
            JsonElement? resultVariableValue = resultVariable is not null
                                                && variableUpdates.TryGetValue(resultVariable, out var value)
                ? value
                : null;

            return new ForeignModuleTaskStepExecutionResponse(
                result,
                shouldContinue,
                variableUpdates,
                resultVariableValue,
                _logs,
                _outputs.LastOrDefault(),
                ChannelId == _initialChannelId ? null : ChannelId,
                _registeredEventHandlers);
        }

        private IReadOnlyDictionary<string, JsonElement> SnapshotVariables() =>
            Variables.ToDictionary(
                pair => pair.Key,
                pair => SerializeVariableValue(pair.Value),
                StringComparer.Ordinal);

        private void ApplyHostContextResponse(ForeignModuleTaskContextExecutionResponse response)
        {
            ChannelId = response.ChannelId;
            Variables.Clear();
            foreach (var (key, value) in response.Variables)
                Variables[key] = ConvertJsonValue(value);
        }

        private static object? ConvertJsonValue(JsonElement value) =>
            value.ValueKind switch
            {
                JsonValueKind.Undefined or JsonValueKind.Null => null,
                JsonValueKind.String => value.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when value.TryGetInt64(out var longValue) => longValue,
                JsonValueKind.Number => value.GetDouble(),
                _ => value.Clone(),
            };

        private static JsonElement SerializeVariableValue(object? value)
        {
            if (value is JsonElement element)
                return element.Clone();

            try
            {
                return value is null
                    ? JsonSerializer.SerializeToElement((string?)null, JsonOptions)
                    : JsonSerializer.SerializeToElement(value, value.GetType(), JsonOptions);
            }
            catch (NotSupportedException)
            {
                return JsonSerializer.SerializeToElement(value?.ToString(), JsonOptions);
            }
        }
    }

    private sealed record DotNetSidecarTaskEventHandler(
        string? ModuleTriggerKey,
        string? ParameterName,
        string? HandlerCallbackId,
        DotNetSidecarHostCapabilityClient? Client,
        Func<Guid> GetChannelId,
        Func<IReadOnlyDictionary<string, JsonElement>> SnapshotVariables,
        Action<ForeignModuleTaskContextExecutionResponse> ApplyResponse) : ITaskEventHandler
    {
        public async Task ExecuteBodyAsync(CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(HandlerCallbackId) || Client is null)
                throw new NotSupportedException(
                    "Executing a parent task event handler from a .NET sidecar requires an active host callback.");

            var response = await Client.PostAsync<
                ForeignModuleTaskContextExecuteEventHandlerRequest,
                ForeignModuleTaskContextExecutionResponse>(
                ForeignModuleHostCapabilityProtocol.TaskContextExecuteEventHandlerPath,
                new ForeignModuleTaskContextExecuteEventHandlerRequest
                {
                    HandlerId = HandlerCallbackId,
                    ChannelId = GetChannelId(),
                    Variables = SnapshotVariables(),
                },
                ct);
            ApplyResponse(response);
        }
    }

    private sealed record DotNetSidecarLocalTaskEventHandler(
        string? ModuleTriggerKey,
        string? ParameterName,
        DotNetSidecarTaskStepExecutionContext Context,
        IReadOnlyList<ITaskStepInvocation> Body) : ITaskEventHandler
    {
        public async Task ExecuteBodyAsync(CancellationToken ct) =>
            _ = await Context.ExecuteStepsAsync(Body, ct);
    }

    private sealed class DotNetSidecarTaskStepInvocation(
        ForeignModuleTaskStepInvocationDescriptor descriptor) : ITaskStepInvocation
    {
        public string StepKey => descriptor.StepKey;
        public string? VariableName => descriptor.VariableName;
        public string? TypeName => descriptor.TypeName;
        public string? ResultVariable => descriptor.ResultVariable;
        public string? RawExpression => descriptor.RawExpression;
        public IReadOnlyList<string>? Arguments => descriptor.Arguments;
        public string? ModuleTriggerKey => descriptor.ModuleTriggerKey;
        public string? HandlerParameter => descriptor.HandlerParameter;
        public IReadOnlyList<ITaskStepInvocation>? Body =>
            descriptor.Body is null ? null : [.. descriptor.Body.Select(step => new DotNetSidecarTaskStepInvocation(step))];
        public IReadOnlyList<ITaskStepInvocation>? ElseBody =>
            descriptor.ElseBody is null ? null : [.. descriptor.ElseBody.Select(step => new DotNetSidecarTaskStepInvocation(step))];
    }

    private sealed class DotNetSidecarTaskTriggerSourceContext(
        ForeignModuleTaskTriggerSourceContextDescriptor descriptor,
        ITaskInstanceLauncher launcher) : ITaskTriggerSourceContext
    {
        public TaskTriggerDefinition Definition => descriptor.Definition;
        public Guid TaskDefinitionId => descriptor.TaskDefinitionId;

        public async Task FireAsync(
            IReadOnlyDictionary<string, string>? parameters = null,
            CancellationToken ct = default) =>
            _ = await launcher.LaunchAsync(
                TaskDefinitionId,
                parameters,
                callerAgentId: null,
                channelId: null,
                contextId: null,
                ct);
    }

    private sealed class DotNetSidecarTaskTriggerAttributeContext(
        ForeignModuleTaskTriggerAttributeContextDescriptor descriptor) : TaskTriggerAttributeContext
    {
        private readonly List<ForeignModuleTaskTriggerAttributeDiagnostic> _diagnostics = [];

        public override string AttributeName => descriptor.AttributeName;
        public override int Line => descriptor.Line;
        public override int ArgumentCount => descriptor.ArgumentCount;
        public IReadOnlyList<ForeignModuleTaskTriggerAttributeDiagnostic> Diagnostics => _diagnostics;

        public override string? GetStringArg(int index) =>
            index >= 0 && index < descriptor.StringArgs.Count
                ? descriptor.StringArgs[index]
                : null;

        public override int? GetIntArg(int index) =>
            index >= 0 && index < descriptor.IntArgs.Count
                ? descriptor.IntArgs[index]
                : null;

        public override string? GetNamedStringArg(string name) =>
            descriptor.NamedStringArgs.TryGetValue(name, out var value)
                ? value
                : null;

        public override int? GetNamedIntArg(string name) =>
            descriptor.NamedIntArgs.TryGetValue(name, out var value)
                ? value
                : null;

        public override double? GetNamedDoubleArg(string name) =>
            descriptor.NamedDoubleArgs.TryGetValue(name, out var value)
                ? value
                : null;

        public override T? GetNamedEnumArg<T>(string name) where T : struct
        {
            if (!descriptor.NamedStringArgs.TryGetValue(name, out var value)
                || string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return TryParseEnum<T>(value, out var parsed) ? parsed : null;
        }

        public override string? GetRawArgText(int index) =>
            index >= 0 && index < descriptor.RawArgs.Count
                ? descriptor.RawArgs[index]
                : null;

        public override void Report(
            TaskTriggerAttributeDiagnosticSeverity severity,
            string code,
            string message) =>
            _diagnostics.Add(new ForeignModuleTaskTriggerAttributeDiagnostic(severity, code, message));

        private static bool TryParseEnum<T>(string value, out T parsed)
            where T : struct
        {
            var normalized = string.Join(
                ",",
                value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(part =>
                    {
                        var dot = part.LastIndexOf('.');
                        return dot >= 0 ? part[(dot + 1)..] : part;
                    }));
            return Enum.TryParse(normalized, ignoreCase: true, out parsed);
        }
    }

    private static class ConsoleCapture
    {
        private static readonly SemaphoreSlim Gate = new(1, 1);

        public static async Task<ConsoleCaptureResult> RunAsync(Func<Task> action)
        {
            await Gate.WaitAsync();
            var oldOut = Console.Out;
            var oldErr = Console.Error;
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                await action();
                return new ConsoleCaptureResult(true, stdout.ToString(), stderr.ToString());
            }
            catch (Exception ex)
            {
                await stderr.WriteLineAsync(ex.ToString());
                return new ConsoleCaptureResult(false, stdout.ToString(), stderr.ToString());
            }
            finally
            {
                Console.SetOut(oldOut);
                Console.SetError(oldErr);
                Gate.Release();
            }
        }
    }
}

internal static class DotNetSidecarHostConversions
{
    public static ForeignModuleHealthResponse ToForeignResponse(this ModuleHealthStatus status)
    {
        var details = status.Details?.ToDictionary(
            kv => kv.Key,
            kv => JsonSerializer.SerializeToElement(kv.Value));
        return new ForeignModuleHealthResponse(status.IsHealthy, status.Message, details);
    }

    public static AgentJobContext ToAgentJobContext(this ForeignModuleAgentJobContext context) =>
        new(
            context.JobId,
            context.AgentId,
            context.ChannelId,
            context.ResourceId,
            context.ActionKey);

    public static InlineToolContext ToInlineToolContext(this ForeignModuleInlineToolContext context) =>
        new(
            context.AgentId,
            context.ChannelId,
            context.ThreadId,
            context.ToolCallId);
}
