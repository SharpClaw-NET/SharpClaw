using System.Runtime.Loader;
using SharpClaw.Runtime.Host;

using var processCancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancelKeyPress = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    processCancellation.Cancel();
};
Action<AssemblyLoadContext> unloading = _ => processCancellation.Cancel();
Console.CancelKeyPress += cancelKeyPress;
AssemblyLoadContext.Default.Unloading += unloading;
try
{
    if (!await RuntimeLauncher.TryRunEarlyAsync(args, processCancellation.Token))
        await LocalRuntimeHost.RunAsync(args, processCancellation.Token);
}
finally
{
    Console.CancelKeyPress -= cancelKeyPress;
    AssemblyLoadContext.Default.Unloading -= unloading;
}
