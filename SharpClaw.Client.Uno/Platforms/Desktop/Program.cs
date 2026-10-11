using Uno.UI.Hosting;
using SharpClaw.Services;

namespace SharpClaw;

internal static class Program
{
    [STAThread]
    public static async Task Main(string[] args)
    {
        var diagnostics = ClientStartupDiagnostics.Current;
        diagnostics.Record(ClientStartupStage.DesktopHostStarting);
        try
        {
            using var instance = InstalledClientInstanceGuard.Acquire();
            if (!instance.IsPrimary)
            {
                diagnostics.Record(ClientStartupStage.DuplicateActivation);
                return;
            }
            var desktop = new ClientDesktopLifetime();
            App? application = null;
            var host = UnoPlatformHostBuilder.Create()
                .App(() => application = new App(desktop))
                .UseX11()
                .UseLinuxFrameBuffer()
                .UseMacOS()
                .UseWin32()
                .Build();

            // The native loop can finish before Window.Closed's async handler.
            // Keep the instance guard until that same actual shutdown settles.
            await desktop.RunHostAsync(async () => await host.RunAsync().ConfigureAwait(false),
                () => application?.ShutdownAsync() ?? Task.CompletedTask).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            diagnostics.Record(ClientStartupStage.StartupFailed, exception);
            throw;
        }
        finally
        {
            diagnostics.Record(ClientStartupStage.DesktopHostStopped);
        }
    }
}
