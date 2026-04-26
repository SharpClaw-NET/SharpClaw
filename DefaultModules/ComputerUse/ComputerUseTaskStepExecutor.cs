using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Modules.ComputerUse.Services;

namespace SharpClaw.Modules.ComputerUse;

/// <summary>
/// Handles Computer Use module task steps in the task orchestrator.
/// Currently provides <c>ListDisplayDevices</c>, which returns a
/// newline-separated list of registered display device names for use
/// in a ForEach loop.
/// </summary>
internal sealed class ComputerUseTaskStepExecutor(
    IServiceScopeFactory scopeFactory) : ITaskStepExecutorExtension
{
    public string ModuleId => "sharpclaw_computer_use";

    public bool CanExecute(string moduleStepKey) =>
        moduleStepKey is "ListDisplayDevices";

    public async Task<bool> ExecuteAsync(
        string moduleStepKey,
        ITaskStepExecutionContext context,
        IReadOnlyList<string>? arguments,
        string? expression,
        string? resultVariable)
    {
        if (moduleStepKey is "ListDisplayDevices")
        {
            await ExecuteListDisplayDevicesAsync(context, resultVariable);
        }
        return true;
    }

    private async Task ExecuteListDisplayDevicesAsync(
        ITaskStepExecutionContext context,
        string? resultVariable)
    {
        using var scope = scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<DisplayDeviceService>();
        var devices = await svc.ListAsync(context.CancellationToken);

        var nameList = string.Join("\n", devices.Select(d => d.Name));

        if (resultVariable is not null)
            context.Variables[resultVariable] = nameList;

        await context.AppendLogAsync(
            $"ListDisplayDevices → {devices.Count} device(s): {string.Join(", ", devices.Select(d => d.Name))}");
    }
}
