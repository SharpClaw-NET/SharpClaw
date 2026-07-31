using SharpClaw.Runtime.Host;

if (!await RuntimeLauncher.TryRunEarlyAsync(args))
{
    await LocalRuntimeHost.RunAsync(args);
}
