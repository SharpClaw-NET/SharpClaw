using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.FilesystemTriggers;

/// <summary>
/// Default module that owns the <c>FileChanged</c> task trigger.
/// Scaffolded in Phase 1 of the trigger-extraction plan; the
/// <c>FileChangedTriggerSource</c> implementation is moved into this
/// module in Phase 3.
/// </summary>
public sealed class FilesystemTriggersModule : ISharpClawModule, ITaskParserAware
{
    public ITaskParserModuleExtension ParserExtension => FilesystemTriggersParserExtension.Instance;

    public string Id => "sharpclaw_filesystem_triggers";
    public string DisplayName => "Filesystem Triggers";
    public string ToolPrefix => "fstrigger";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ITaskTriggerSource, FileChangedTriggerSource>();
    }

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];

    public Task<string> ExecuteToolAsync(
        string toolName,
        JsonElement parameters,
        AgentJobContext job,
        IServiceProvider scopedServices,
        CancellationToken ct) =>
        throw new InvalidOperationException(
            $"FilesystemTriggers module has no job-pipeline tools. Unknown: '{toolName}'.");
}
