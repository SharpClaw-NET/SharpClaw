using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.Http;

/// <summary>
/// Default module that owns the task-script HTTP request step
/// (HttpGet/HttpPost/HttpPut/HttpDelete → http_request).
/// </summary>
public sealed class HttpModule : ISharpClawModule, ITaskParserAware
{
    public ITaskParserModuleExtension ParserExtension => HttpParserExtension.Instance;

    public string Id => "sharpclaw_http";
    public string DisplayName => "HTTP";
    public string ToolPrefix => "http";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<ITaskStepExecutorExtension, HttpTaskStepExecutor>();
        services.AddSingleton<WebhookTriggerSource>();
        services.AddSingleton<ITaskTriggerSource>(sp => sp.GetRequiredService<WebhookTriggerSource>());
        services.AddSingleton<IWebhookTriggerHost>(sp => sp.GetRequiredService<WebhookTriggerSource>());
    }

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];

    public Task<string> ExecuteToolAsync(
        string toolName,
        JsonElement parameters,
        AgentJobContext job,
        IServiceProvider scopedServices,
        CancellationToken ct) =>
        throw new InvalidOperationException(
            $"HTTP module has no job-pipeline tools. Unknown: '{toolName}'.");
}
