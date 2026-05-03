using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.Metrics;

/// <summary>
/// Default module that owns the <c>MetricThreshold</c> task trigger and the
/// built-in <see cref="ITaskMetricProvider"/> implementations. Scaffolded in
/// Phase 1 of the trigger-extraction plan; <c>MetricTriggerSource</c> and the
/// three providers from <c>BuiltInMetricProviders.cs</c> move here in Phase 4.
/// </summary>
public sealed class MetricsModule : ISharpClawModule, ITaskParserAware
{
    public ITaskParserModuleExtension ParserExtension => MetricsParserExtension.Instance;

    public string Id => "sharpclaw_metrics";
    public string DisplayName => "Metrics";
    public string ToolPrefix => "metric";

    public void ConfigureServices(IServiceCollection services)
    {
        // Built-in providers consume IHostQueueMetrics (forwarded from the host)
        // so the module does not depend on SharpClawDbContext directly.
        services.AddSingleton<ITaskMetricProvider, PendingJobCountMetricProvider>();
        services.AddSingleton<ITaskMetricProvider, PendingTaskCountMetricProvider>();
        services.AddSingleton<ITaskMetricProvider, SchedulerPendingJobCountMetricProvider>();
        services.AddSingleton<ITaskTriggerSource, MetricTriggerSource>();
    }

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];

    public Task<string> ExecuteToolAsync(
        string toolName,
        JsonElement parameters,
        AgentJobContext job,
        IServiceProvider scopedServices,
        CancellationToken ct) =>
        throw new InvalidOperationException(
            $"Metrics module has no job-pipeline tools. Unknown: '{toolName}'.");
}
