using Uno.UI.Hosting;
using SharpClaw.Services;

namespace SharpClaw;

internal class Program
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
            var host = UnoPlatformHostBuilder.Create()
                .App(() => new App())
                .UseX11()
                .UseLinuxFrameBuffer()
                .UseMacOS()
                .UseWin32()
                .Build();

            await host.RunAsync().ConfigureAwait(true);
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
