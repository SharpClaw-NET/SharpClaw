using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.AgentOrchestration;

/// <summary>
/// Fires once on application startup
/// (<see cref="TaskScriptingTriggerKeys.Startup"/>) and registers a shutdown
/// callback (<see cref="TaskScriptingTriggerKeys.Shutdown"/>) via
/// <see cref="IHostApplicationLifetime"/>.
/// <para>
/// Moved out of <c>SharpClaw.Application.Core</c> by the trigger-extraction
/// plan; behavior is preserved verbatim.
/// </para>
/// </summary>
public sealed class LifecycleTriggerSource(
    IHostApplicationLifetime lifetime,
    ILogger<LifecycleTriggerSource> logger) : ITaskTriggerSource
{
    private IReadOnlyList<ITaskTriggerSourceContext> _contexts = [];
    private CancellationTokenRegistration _startReg;
    private CancellationTokenRegistration _stopReg;

    public IReadOnlyList<string> TriggerKeys { get; } =
        [TaskScriptingTriggerKeys.Startup, TaskScriptingTriggerKeys.Shutdown];

    public Task StartAsync(IReadOnlyList<ITaskTriggerSourceContext> contexts, CancellationToken ct)
    {
        _contexts = contexts;

        _startReg = lifetime.ApplicationStarted.Register(() =>
            _ = Task.Run(() => FireMatchingAsync(TaskScriptingTriggerKeys.Startup)));

        _stopReg = lifetime.ApplicationStopping.Register(() =>
            _ = Task.Run(() => FireMatchingAsync(TaskScriptingTriggerKeys.Shutdown)));

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _startReg.Dispose();
        _stopReg.Dispose();
        _contexts = [];
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Lifecycle bindings have no per-definition discriminator — every binding
    /// of a given <see cref="TriggerKeys"/> kind fires on every host event.
    /// Returning <see langword="null"/> matches the legacy registrar fallback.
    /// </remarks>
    public string? GetBindingValue(TaskTriggerDefinition def) => null;

    private async Task FireMatchingAsync(string key)
    {
        foreach (var ctx in _contexts.Where(c => c.Definition.TriggerKey == key))
        {
            try { await ctx.FireAsync(); }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "LifecycleTriggerSource failed to fire {Key} context for definition {Id}.",
                    key, ctx.TaskDefinitionId);
            }
        }
    }
}
