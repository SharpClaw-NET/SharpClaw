using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Tests.ExternalModule;

public sealed class DotNetSidecarFixtureModule : ISharpClawModule, ITaskParserAware
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
        services.AddSingleton<ITaskStepExecutorExtension, DotNetSidecarFixtureTaskStepExecutor>();
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
        endpoints.MapGet("/modules/dotnet-sidecar/ping", () => Results.Text("dotnet sidecar pong"));
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

public sealed class DotNetSidecarFixtureTaskStepDescriptorProvider : ITaskStepDescriptorProvider
{
    public const string StepKey = "synthetic.dotnet.step";
    public string ModuleId => DotNetSidecarFixtureModule.ModuleId;
    public IReadOnlyList<TaskStepDescriptor> Descriptors { get; } =
    [
        new()
        {
            MethodName = "DotNetSidecarStep",
            StepKey = StepKey,
            OwnerId = DotNetSidecarFixtureModule.ModuleId,
            FirstArgIsExpression = true,
        },
    ];
}

internal sealed class DotNetSidecarFixtureTaskStepExecutor : ITaskStepInvocationExecutor
{
    public string ModuleId => DotNetSidecarFixtureModule.ModuleId;

    public bool CanExecute(string moduleStepKey) =>
        string.Equals(
            moduleStepKey,
            DotNetSidecarFixtureTaskStepDescriptorProvider.StepKey,
            StringComparison.Ordinal);

    public async Task<bool> ExecuteAsync(
        string moduleStepKey,
        ITaskStepExecutionContext context,
        IReadOnlyList<string>? arguments,
        string? expression,
        string? resultVariable)
    {
        context.Variables["dotnetSidecarStep"] = expression ?? arguments?.FirstOrDefault() ?? "executed";
        if (resultVariable is not null)
            context.Variables[resultVariable] = "dotnet-sidecar-step-result";
        await context.AppendLogAsync("dotnet sidecar step log");
        await context.WriteOutputAsync("""{"dotnetSidecar":true}""");
        return true;
    }

    public async Task<TaskStepResult> ExecuteInvocationAsync(
        ITaskStepInvocation step,
        ITaskStepExecutionContext context)
    {
        context.Variables["dotnetSidecarInvocation"] = step.RawExpression ?? "invoked";
        if (step.ResultVariable is not null)
            context.Variables[step.ResultVariable] = "dotnet-sidecar-invocation-result";
        await context.AppendLogAsync("dotnet sidecar invocation log");
        return TaskStepResult.Continue;
    }
}

internal sealed class DotNetSidecarFixtureParserExtension : ITaskParserModuleExtension
{
    public IReadOnlyDictionary<string, (string StepKey, string ModuleId)> StepKeyMappings { get; } =
        new Dictionary<string, (string StepKey, string ModuleId)>(StringComparer.Ordinal)
        {
            ["DotNetSidecarStep"] =
                (DotNetSidecarFixtureTaskStepDescriptorProvider.StepKey, DotNetSidecarFixtureModule.ModuleId),
        };

    public IReadOnlyDictionary<string, (string TriggerKey, string ModuleId)> EventTriggerMappings { get; } =
        new Dictionary<string, (string TriggerKey, string ModuleId)>(StringComparer.Ordinal)
        {
            ["OnDotNetSidecar"] =
                (DotNetSidecarFixtureTriggerSource.TriggerKeyValue, DotNetSidecarFixtureModule.ModuleId),
        };

    public IReadOnlySet<string> SingleArgExpressionMethods { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "DotNetSidecarStep" };

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
