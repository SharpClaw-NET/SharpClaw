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
                SeedTemplateContents(source, destination);
        }

        return resolved;
    }

    private static void SeedTemplateContents(string source, string destination)
    {
        // AppX-protected files are readable, but File.Copy also propagates their
        // encryption metadata, which cannot be applied to ordinary profile files.
        // Publish complete contents without copying attributes or replacing state.
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        var ownsTemporary = false;
        try
        {
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemporary = true;
                input.CopyTo(output);
            }
            File.Move(temporary, destination, overwrite: false);
            ownsTemporary = false;
        }
        finally
        {
            if (ownsTemporary)
                File.Delete(temporary);
        }
    }
}
