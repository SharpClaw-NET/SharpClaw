namespace SharpClaw.Shared.Security;

/// <summary>Allows installed processes to keep protected configuration outside read-only binaries.</summary>
public static class SharpClawEnvironmentDirectory
{
    public const string OverrideVariable = "SHARPCLAW_ENVIRONMENT_DIR";

    public static string Resolve(string assemblyDirectory)
    {
        var configured = Environment.GetEnvironmentVariable(OverrideVariable);
        if (string.IsNullOrWhiteSpace(configured))
            return assemblyDirectory;

        return Prepare(assemblyDirectory, configured);
    }

    public static string Prepare(string templateDirectory, string writableDirectory)
    {
        if (!Path.IsPathFullyQualified(writableDirectory))
            throw new InvalidOperationException($"{OverrideVariable} must be an absolute directory.");

        var resolved = Path.GetFullPath(writableDirectory);
        Directory.CreateDirectory(resolved);
        foreach (var name in new[] { ".env.template", ".dev.env.template" })
        {
            var source = Path.Combine(templateDirectory, name);
            var destination = Path.Combine(resolved, name);
            if (!File.Exists(destination) && File.Exists(source))
                File.Copy(source, destination, overwrite: false);
        }

        return resolved;
    }
}
