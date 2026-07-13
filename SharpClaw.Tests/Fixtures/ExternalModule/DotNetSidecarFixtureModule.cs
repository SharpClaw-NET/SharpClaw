using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.TestFixtures.ExternalModule;

public sealed class DotNetSidecarFixtureModule : ISharpClawRuntimeModule, ITaskParserAware
{
    public const string ModuleId = "synthetic_dotnet_sidecar";
    public const string ToolPrefixValue = "sds";
    public const string JobTool = "dotnet_sidecar_echo";
    public const string InlineTool = "dotnet_sidecar_inline";
    public const string HeaderTag = "dotnet_sidecar_config";
    public const string ResourceType = "SharpClaw.DotNetSidecarFixture.Resource";

    public string Id => ModuleId;
    public string DisplayName => "Synthetic .NET Sidecar";
    public string ToolPrefix => ToolPrefixValue;
    public ITaskParserModuleExtension ParserExtension { get; } = new DotNetSidecarFixtureParserExtension();

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ITaskOperationExecutor, DotNetSidecarFixtureTaskOperationExecutor>();
        services.AddSingleton<ITaskTriggerSource, DotNetSidecarFixtureTriggerSource>();
        services.AddSingleton<ITaskTriggerBindingSideEffect, DotNetSidecarFixtureTriggerSideEffect>();
        services.AddSingleton<ITaskMetricProvider, DotNetSidecarFixtureMetricProvider>();
        services.AddSingleton<ISharpClawEventSink, DotNetSidecarFixtureEventSink>();
    }

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() =>
    [
        new(
            JobTool,
            ".NET sidecar echo tool.",
            EmptySchema(),
            new ModuleToolPermission(
                false,
                (_, _, _, _) => Task.FromResult(
                    AgentActionResult.Approve(
                        ".NET sidecar fixture approved.",
                        PermissionClearance.Independent))))
    ];

    public IReadOnlyList<ModuleInlineToolDefinition> GetInlineToolDefinitions() =>
    [
        new(
            InlineTool,
            ".NET sidecar inline tool.",
            EmptySchema())
    ];

    public IReadOnlyList<ModuleHeaderTag>? GetHeaderTags() =>
    [
        new(HeaderTag, async (services, ct) =>
        {
            var store = services.GetRequiredService<IModuleConfigStore>();
            return await store.GetAsync("sidecar:last", ct) ?? "missing";
        })
    ];

    public IReadOnlyList<ModuleResourceTypeDescriptor> GetResourceTypeDescriptors() =>
    [
        new(
            ResourceType,
            "DotNetSidecarResource",
            "UseDotNetSidecarFixtureAsync",
            (_, _) => Task.FromResult(new List<Guid>
            {
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            }),
            (_, _) => Task.FromResult(new List<(Guid Id, string Name)>
            {
                (Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), ".NET sidecar resource"),
            }),
            "dotnet_sidecar_fixture")
    ];

    public void MapEndpoints(object app)
    {
        var endpoints = (IEndpointRouteBuilder)app;
        endpoints.MapGet("/modules/dotnet-sidecar/ping", () => Results.Text("dotnet sidecar pong"))
            .AllowAnonymous();
    }

    public async Task<string> ExecuteToolAsync(
        string toolName,
        JsonElement parameters,
        AgentJobContext job,
        IServiceProvider scopedServices,
        CancellationToken ct)
    {
        var value = parameters.TryGetProperty("value", out var property)
            ? property.GetString() ?? "missing"
            : "missing";
        var store = scopedServices.GetRequiredService<IModuleConfigStore>();
        await store.SetAsync("sidecar:last", value, ct);
        return $"dotnet sidecar {await store.GetAsync("sidecar:last", ct)}";
    }

    public Task<string> ExecuteInlineToolAsync(
        string toolName,
        JsonElement parameters,
        InlineToolContext context,
        IServiceProvider scopedServices,
        CancellationToken ct) =>
        Task.FromResult("dotnet sidecar inline");

    private static JsonElement EmptySchema()
    {
        using var doc = JsonDocument.Parse("""{"type":"object","additionalProperties":false}""");
        return doc.RootElement.Clone();
    }
}

public sealed class DotNetSidecarFixtureTaskOperationDescriptorProvider : ITaskOperationDescriptorProvider
{
    public const string OperationKey = "synthetic.dotnet.operation";
    public const string ParentHandlerTriggerKey = "synthetic.dotnet.parent_handler";
    public const string RegisteredHandlerTriggerKey = "synthetic.dotnet.registered_handler";
    public string ModuleId => DotNetSidecarFixtureModule.ModuleId;
    public IReadOnlyList<TaskOperationDescriptor> Descriptors { get; } =
    [
        new()
        {
            MethodName = "DotNetSidecarOperation",
            OperationKey = OperationKey,
            OwnerId = DotNetSidecarFixtureModule.ModuleId,
            FirstArgIsExpression = true,
        },
    ];
}

internal sealed class DotNetSidecarFixtureTaskOperationExecutor : ITaskOperationInvocationExecutor
{
    public string ModuleId => DotNetSidecarFixtureModule.ModuleId;

    public bool CanExecute(string operationKey) =>
        string.Equals(
            operationKey,
            DotNetSidecarFixtureTaskOperationDescriptorProvider.OperationKey,
            StringComparison.Ordinal);

    public async Task<bool> ExecuteAsync(
        string operationKey,
        ITaskOperationExecutionContext context,
        IReadOnlyList<string>? arguments,
        string? expression,
        string? resultVariable)
    {
        context.Variables["dotnetSidecarOperation"] = expression ?? arguments?.FirstOrDefault() ?? "executed";
        if (resultVariable is not null)
            context.Variables[resultVariable] = "dotnet-sidecar-operation-result";
        await context.AppendLogAsync("dotnet sidecar operation log");
        await context.WriteOutputAsync("""{"dotnetSidecar":true}""");
        return true;
    }

    public async Task<TaskStatementResult> ExecuteInvocationAsync(
        ITaskStatementInvocation statement,
        ITaskOperationExecutionContext context)
    {
        if (string.Equals(statement.RawExpression, "run-nested", StringComparison.Ordinal)
            && statement.Body is not null)
        {
            var nestedResult = await context.ExecuteStatementsAsync(statement.Body, context.CancellationToken);
            context.Variables["dotnetSidecarNestedResult"] = nestedResult.ToString();
            return nestedResult;
        }

        if (string.Equals(statement.RawExpression, "bridge-find-model", StringComparison.Ordinal))
        {
            var bridge = context.Services.GetRequiredService<IHostAgentBridge>();
            var modelId = await bridge.FindModelAsync("sidecar-model", context.CancellationToken);
            context.Variables["dotnetSidecarBridgeModelId"] = modelId?.ToString();
        }

        if (string.Equals(statement.RawExpression, "execute-parent-handler", StringComparison.Ordinal))
        {
            var handler = context.EventHandlers.First(candidate =>
                string.Equals(
                    candidate.ModuleTriggerKey,
                    DotNetSidecarFixtureTaskOperationDescriptorProvider.ParentHandlerTriggerKey,
                    StringComparison.Ordinal));
            await handler.ExecuteBodyAsync(context.CancellationToken);
            context.Variables["dotnetSidecarParentHandlerExecuted"] = "true";
        }

        if (string.Equals(statement.RawExpression, "register-handler", StringComparison.Ordinal)
            && statement.Body is not null)
        {
            context.RegisterEventHandler(
                DotNetSidecarFixtureTaskOperationDescriptorProvider.RegisteredHandlerTriggerKey,
                "evt",
                statement.Body);
        }

        context.Variables["dotnetSidecarInvocation"] = statement.RawExpression ?? "invoked";
        if (statement.ResultVariable is not null)
            context.Variables[statement.ResultVariable] = "dotnet-sidecar-invocation-result";
        await context.AppendLogAsync("dotnet sidecar invocation log");
        return TaskStatementResult.Continue;
    }
}

internal sealed class DotNetSidecarFixtureParserExtension : ITaskParserModuleExtension
{
    public IReadOnlyDictionary<string, (string OperationKey, string ModuleId)> OperationKeyMappings { get; } =
        new Dictionary<string, (string OperationKey, string ModuleId)>(StringComparer.Ordinal)
        {
            ["DotNetSidecarOperation"] =
                (DotNetSidecarFixtureTaskOperationDescriptorProvider.OperationKey, DotNetSidecarFixtureModule.ModuleId),
        };

    public IReadOnlyDictionary<string, (string TriggerKey, string ModuleId)> EventTriggerMappings { get; } =
        new Dictionary<string, (string TriggerKey, string ModuleId)>(StringComparer.Ordinal)
        {
            ["OnDotNetSidecar"] =
                (DotNetSidecarFixtureTriggerSource.TriggerKeyValue, DotNetSidecarFixtureModule.ModuleId),
        };

    public IReadOnlySet<string> SingleArgExpressionMethods { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "DotNetSidecarOperation" };

    public IReadOnlyDictionary<string, ITaskTriggerAttributeHandler> TriggerAttributeHandlers { get; } =
        new Dictionary<string, ITaskTriggerAttributeHandler>(StringComparer.Ordinal)
        {
            ["DotNetSidecarTrigger"] = new DotNetSidecarFixtureTriggerAttributeHandler(),
        };
}

internal sealed class DotNetSidecarFixtureTriggerAttributeHandler : ITaskTriggerAttributeHandler
{
    public TaskTriggerDefinition? Handle(TaskTriggerAttributeContext context) =>
        new()
        {
            TriggerKey = DotNetSidecarFixtureTriggerSource.TriggerKeyValue,
            Line = context.Line,
            Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["name"] = context.GetNamedStringArg("Name") ?? context.GetStringArg(0),
            },
        };
}

public sealed class DotNetSidecarFixtureTriggerSource : ITaskTriggerSource
{
    public const string TriggerKeyValue = "synthetic.dotnet.trigger";
    public IReadOnlyList<string> TriggerKeys => [TriggerKeyValue];
    public bool OwnsBindingPersistence => true;

    public Task StartAsync(IReadOnlyList<ITaskTriggerSourceContext> contexts, CancellationToken ct) =>
        Task.CompletedTask;

    public Task StopAsync() => Task.CompletedTask;

    public string? GetBindingValue(TaskTriggerDefinition def) =>
        def.Parameters.TryGetValue("name", out var value) ? value : null;

    public string? GetBindingFilter(TaskTriggerDefinition def) => "dotnet-filter";

    public Task<bool> SyncBindingsAsync(
        TaskDefinitionDescriptor definition,
        IReadOnlyList<TaskTriggerDefinition> ownedTriggers,
        CancellationToken ct) =>
        Task.FromResult(ownedTriggers.Count > 0);

    public Task RemoveBindingsAsync(Guid definitionId, CancellationToken ct) =>
        Task.CompletedTask;
}

internal sealed class DotNetSidecarFixtureTriggerSideEffect : ITaskTriggerBindingSideEffect
{
    public string TriggerKey => DotNetSidecarFixtureTriggerSource.TriggerKeyValue;

    public Task OnBindingCreatedAsync(
        TaskDefinitionDescriptor definition,
        TaskTriggerDefinition trigger,
        TaskTriggerBindingDescriptor binding,
        CancellationToken ct) =>
        Task.CompletedTask;

    public Task OnBindingRemovedAsync(TaskTriggerBindingDescriptor binding, CancellationToken ct) =>
        Task.CompletedTask;
}

public sealed class DotNetSidecarFixtureMetricProvider : ITaskMetricProvider
{
    public const string MetricNameValue = "synthetic.dotnet.metric";
    public string MetricName => MetricNameValue;
    public string Description => "Synthetic .NET sidecar metric.";
    public Task<double> GetValueAsync(CancellationToken ct) => Task.FromResult(13.5);
}

internal sealed class DotNetSidecarFixtureEventSink : ISharpClawEventSink
{
    public SharpClawEventType SubscribedEvents => SharpClawEventType.AllModuleEvents;
    public Task OnEventAsync(SharpClawEvent evt, CancellationToken ct) => Task.CompletedTask;
}
