using System.Text;
using System.Text.Json;
using SharpClaw.Shared.Logging;

namespace SharpClaw.Services;


internal enum ClientStartupStage
{
    DesktopHostStarting,
    DuplicateActivation,
    AppInitializing,
    AppInitialized,
    LaunchStarting,
    InstanceReady,
    BuilderReady,
    NavigationScheduled,
    WindowActivated,
    InitialNavigationStarting,
    BootLoaded,
    NavigationReady,
    StartupFailed,
    UnhandledException,
    DesktopHostStopped,
}
