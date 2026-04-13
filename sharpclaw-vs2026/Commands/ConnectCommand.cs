using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace SharpClaw.VS2026Extension;

internal sealed class ConnectCommand
{
    public const int CommandId = 0x0100;
    public static readonly Guid CommandSet = new("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

    private readonly SharpClawPackage _package;

    private ConnectCommand(SharpClawPackage package, OleMenuCommandService commandService)
    {
        _package = package ?? throw new ArgumentNullException(nameof(package));
        commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

        var menuCommandID = new CommandID(CommandSet, CommandId);
        var menuItem = new MenuCommand(Execute, menuCommandID);
        commandService.AddCommand(menuItem);
    }

    public static ConnectCommand? Instance { get; private set; }

    public static async Task InitializeAsync(SharpClawPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        Instance = new ConnectCommand(package, commandService!);
    }

    private void Execute(object? sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _ = _package.JoinableTaskFactory.RunAsync(async () =>
        {
            await _package.ConnectAsync();
        });
    }
}
