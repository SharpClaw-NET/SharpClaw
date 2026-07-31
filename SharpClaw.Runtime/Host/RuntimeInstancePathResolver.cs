using SharpClaw.Shared.Instances;

namespace SharpClaw.Runtime.Host;

internal static class RuntimeInstancePathResolver
{
    public static SharpClawInstancePaths CreateBackend()
    {
        var dataDirectory = Environment.GetEnvironmentVariable("SHARPCLAW_DATA_DIR");
        var instanceRoot = Environment.GetEnvironmentVariable("SHARPCLAW_INSTANCE_ROOT");
        if (string.IsNullOrWhiteSpace(instanceRoot) && !string.IsNullOrWhiteSpace(dataDirectory))
            instanceRoot = Path.GetDirectoryName(Path.GetFullPath(dataDirectory));

        return new SharpClawInstancePaths(
            SharpClawInstanceKind.Backend,
            instanceRoot);
    }
}
