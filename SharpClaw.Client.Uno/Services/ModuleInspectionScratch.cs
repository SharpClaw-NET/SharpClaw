namespace SharpClaw.Services;

/// <summary>Owns one acquired inspection directory, independently of the store lifetime.</summary>
internal sealed class ModuleInspectionScratch(string root, string path, Action<string> deleteDirectory)
{
    private readonly Lock _gate = new();
    private bool _retired;

    internal string Path { get; } = path;

    internal void Retire()
    {
        lock (_gate)
        {
            if (_retired) return;
            try
            {
                ModulePackageStore.RequireContained(root, Path);
                // Validate even missing descendants: an existing ancestor can have
                // been replaced. Never follow a redirected remembered pathname.
                ModulePackageStore.RequireNoLinks(Path, allowMissing: true);
            }
            catch (InvalidDataException)
            {
                // Refusal retires this pathname claim, not the unknown moved directory.
                _retired = true;
                return;
            }
            if (Directory.Exists(Path)) deleteDirectory(Path);
            _retired = true;
        }
    }
}
