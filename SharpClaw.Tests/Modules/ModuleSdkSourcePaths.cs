namespace SharpClaw.Tests.Modules;

internal static class ModuleSdkSourcePaths
{
    public static string JavaScriptHostSourcePath =>
        Path.Combine(ModuleSdkRoot, "sdk", "javascript", "sharpclaw-module-host", "src", "index.mjs");

    public static string PythonHostSourcePath =>
        Path.Combine(
            ModuleSdkRoot,
            "sdk",
            "python",
            "sharpclaw-module-host",
            "src",
            "sharpclaw_module_host",
            "host.py");

    private static string ModuleSdkRoot
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("SHARPCLAW_MODULESDK_ROOT");
            var root = string.IsNullOrWhiteSpace(configured)
                ? Path.GetFullPath(Path.Combine(FindSharpClawRepoRoot(), "..", "SharpClaw.ModuleSDK"))
                : Path.GetFullPath(configured);

            if (!File.Exists(Path.Combine(root, "SharpClaw.ModuleSDK.slnx")))
            {
                throw new DirectoryNotFoundException(
                    "Could not locate SharpClaw.ModuleSDK. Set SHARPCLAW_MODULESDK_ROOT or place the checkout next to SharpClaw.");
            }

            return root;
        }
    }

    private static string FindSharpClawRepoRoot()
    {
        foreach (var startingPoint in new[]
                 {
                     TestContext.CurrentContext.TestDirectory,
                     AppContext.BaseDirectory,
                     Directory.GetCurrentDirectory()
                 })
        {
            var current = startingPoint;
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (File.Exists(Path.Combine(current, "Directory.Build.props"))
                    && Directory.Exists(Path.Combine(current, "SharpClaw.Tests")))
                {
                    return current;
                }

                current = Directory.GetParent(current)?.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate SharpClaw repository root.");
    }
}
