using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using FileAttributes = System.IO.FileAttributes;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Services;

internal sealed partial record InstalledModuleIdentity(string Id, string DisplayName, string Version,
    bool DefaultEnabled = true, bool Bundled = false);
internal sealed record ModulePayloadFile(string Path, long Length, string Sha256);
internal sealed record InstalledModuleReceipt(
    int SchemaVersion, string InstallationId, IReadOnlyList<InstalledModuleIdentity> Modules,
    IReadOnlyList<ModulePayloadFile> Files);

/// <summary>An inspected, non-executed candidate whose owned scratch is retired when the view leaves.</summary>
internal sealed class PreparedModulePackage : IDisposable
{
    internal PreparedModulePackage(string root, IReadOnlyList<InstalledModuleIdentity> modules,
        IReadOnlyList<ModulePayloadFile> files)
    { Root = root; Modules = modules; Files = files; }

    internal string Root { get; }
    public IReadOnlyList<InstalledModuleIdentity> Modules { get; }
    internal IReadOnlyList<ModulePayloadFile> Files { get; }
    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}

/// <summary>Imports data only. Activation always goes through ExternalRegistrations and the production loader.</summary>
internal sealed class ModulePackageStore : IDisposable
{
    public const long MaximumArchiveBytes = 256L * 1024 * 1024;
    private const long MaximumExpandedBytes = 512L * 1024 * 1024;
    private const int MaximumFiles = 5_000;
    private readonly ModulePackageSources _sources;
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    internal string ActiveRoot => Path.Combine(_root, "registrations");

    public ModulePackageStore(FrontendInstanceService frontend)
        : this(Path.Combine(frontend.Paths.DataDirectory, "modules"), new ModulePackageSources()) { }

    internal ModulePackageStore(string root, ModulePackageSources sources)
    {
        _sources = sources;
        try
        {
            _root = Path.GetFullPath(root);
            Directory.CreateDirectory(_root);
            RequireNoLinks(_root);
            Directory.CreateDirectory(ActiveRoot);
        }
        catch { _sources.Dispose(); throw; }
    }

    public Task<IReadOnlyList<ModulePackageSource>> ResolveAsync(
        string source, string? githubToken, CancellationToken cancellationToken) =>
        _sources.ResolveAsync(source, githubToken, cancellationToken);

    public async Task<PreparedModulePackage> PrepareAsync(
        ModulePackageSource source, string? githubToken, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var stage = Path.Combine(_root, "inspection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var payload = Path.Combine(stage, "payload");
            Directory.CreateDirectory(payload);
            if (source.LocalPath is { } local && Directory.Exists(local))
            {
                RequireNoLinks(local);
                await CopyDirectoryAsync(local, payload, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var archivePath = Path.Combine(stage, "source.zip");
                {
                    var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await using (output.ConfigureAwait(true))
                    {
                        if (source.LocalPath is { } file)
                        {
                            RequireNoLinks(file);
                            var input = File.OpenRead(file);
                            await using var inputAsyncDisposal = input.ConfigureAwait(true);
                            await ModulePackageSources.CopyBoundedAsync(input, output, MaximumArchiveBytes, cancellationToken).ConfigureAwait(false);
                        }
                        else await _sources.DownloadAsync(source, output, githubToken, cancellationToken).ConfigureAwait(false);
                    }
                }
                await ExtractAsync(archivePath, payload, cancellationToken).ConfigureAwait(false);
                VerifyNuGetIdentity(payload, source);
                File.Delete(archivePath); // Owned temporary input; the installed payload retains all package legal files.
            }
            var modules = ValidateModules(payload);
            var files = await HashFilesAsync(payload, cancellationToken).ConfigureAwait(false);
            return new PreparedModulePackage(stage, modules, files);
        }
        catch
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
            throw;
        }
        finally { _gate.Release(); }
    }

    internal async Task<string> CommitAsync(PreparedModulePackage candidate, string bundledRoot,
        Func<CancellationToken, Task> stopOwnedRuntime,
        Func<string, IReadOnlyList<InstalledModuleIdentity>, CancellationToken, Task> configure,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var destination = Path.Combine(ActiveRoot, Guid.NewGuid().ToString("N"));
        try
        {
            RequireContained(_root, candidate.Root);
            RequireNoLinks(candidate.Root);
            var payload = Path.Combine(candidate.Root, "payload");
            var actual = await HashFilesAsync(payload, cancellationToken).ConfigureAwait(false);
            if (!actual.SequenceEqual(candidate.Files)) throw new InvalidDataException("The inspected module payload changed; inspect it again.");
            var existing = ReadManifestIds(ActiveRoot).Concat(ReadManifestIds(bundledRoot))
                .ToHashSet(StringComparer.Ordinal);
            if (candidate.Modules.Any(module => existing.Contains(module.Id)))
                throw new InvalidDataException("A module with this identity is already installed or bundled. Different versions are not silently replaced.");
            // Stop first. Do not alter any external Runtime or activate a partially copied contribution.
            await stopOwnedRuntime(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(payload, destination);
            var receipt = new InstalledModuleReceipt(1, Path.GetFileName(destination), candidate.Modules, candidate.Files);
            await File.WriteAllTextAsync(Path.Combine(destination, "sharpclaw-module-installation.json"),
                JsonSerializer.Serialize(receipt), cancellationToken).ConfigureAwait(false);
            await configure(ActiveRoot, candidate.Modules, cancellationToken).ConfigureAwait(false);
            return destination;
        }
        catch
        {
            // This GUID destination was created by this operation, never a pre-existing installation.
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            throw;
        }
        finally { _gate.Release(); }
    }

    internal IReadOnlyList<InstalledModuleIdentity> ReadInstalled() => ReadIdentities(ActiveRoot, false);

    internal static IReadOnlyList<InstalledModuleIdentity> ReadIdentities(string root, bool bundled)
    {
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, "package.json", SearchOption.AllDirectories).Select(path =>
        {
            var json = File.ReadAllText(path);
            var manifest = PackageManifestLoader.Parse(json, path).Manifest;
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            var enabled = doc.RootElement.TryGetProperty("enabled", out _) ? manifest.Enabled : manifest.DefaultEnabled;
            return new InstalledModuleIdentity(manifest.Id, manifest.DisplayName, manifest.Version, enabled, bundled);
        }).OrderBy(module => module.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static List<InstalledModuleIdentity> ValidateModules(string root)
    {
        var paths = Directory.EnumerateFiles(root, "package.json", SearchOption.AllDirectories).ToArray();
        if (File.Exists(Path.Combine(root, "sharpclaw-module-installation.json")))
            throw new InvalidDataException("The module payload contains a reserved installation receipt.");
        if (paths.Length is 0 or > 32) throw new InvalidDataException("A module payload must contain 1–32 package.json registrations, not just a library package.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<InstalledModuleIdentity>();
        foreach (var path in paths)
        {
            if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("Module manifest exceeds its limit.");
            var loaded = PackageManifestLoader.Load(path);
            var manifest = loaded.Manifest;
            if (!SharpClawModuleSettings.IsIdentifier(manifest.Id) || !ids.Add(manifest.Id) ||
                !SharpClawModuleSettings.IsLabel(manifest.DisplayName))
                throw new InvalidDataException("Invalid or duplicate module identity.");
            loaded.Runtime.EnsureDotNetEntryAssembly(manifest);
            if (!loaded.Runtime.IsInProcessHostMode && !loaded.Runtime.IsSidecarHostMode)
                throw new InvalidDataException("The module must select in-process or sidecar hosting.");
            if (!Version.TryParse(manifest.MinHostVersion?.Split('-')[0], out var minimum) ||
                minimum > new Version(0, 5, 0))
                throw new InvalidDataException("The module requires an unsupported host version.");
            var entry = Path.Combine(Path.GetDirectoryName(path)!, manifest.EntryAssembly);
            if (!File.Exists(entry)) throw new InvalidDataException("Module entry assembly is missing beside its manifest.");
            using var stream = File.OpenRead(entry);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) throw new InvalidDataException("The module entry must be a managed .NET assembly.");
            // Parse settings as data; neither reflection-loading nor discovery/sidecar launch happens here.
            _ = SharpClawModuleSettings.ReadManifest(File.ReadAllText(path), manifest.Id, manifest.DisplayName);
            using var json = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            var enabled = json.RootElement.TryGetProperty("enabled", out _) ? manifest.Enabled : manifest.DefaultEnabled;
            result.Add(new(manifest.Id, manifest.DisplayName, manifest.Version, enabled));
        }
        return result;
    }

    private static IEnumerable<string> ReadManifestIds(string root) => Directory.Exists(root)
        ? Directory.EnumerateFiles(root, "package.json", SearchOption.AllDirectories)
            .Select(path => PackageManifestLoader.Load(path).Manifest.Id)
        : [];

    private static void VerifyNuGetIdentity(string root, ModulePackageSource source)
    {
        var specs = Directory.GetFiles(root, "*.nuspec", SearchOption.TopDirectoryOnly);
        if (source.PackageId is null && specs.Length == 0 &&
            !source.Name.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Path.GetExtension(source.LocalPath ?? source.Download?.AbsolutePath), ".nupkg", StringComparison.OrdinalIgnoreCase)) return;
        if (specs.Length != 1) throw new InvalidDataException("A NuGet module must have one root nuspec.");
        using var reader = XmlReader.Create(specs[0], new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 512 * 1024 });
        var metadata = XDocument.Load(reader).Root?.Elements().Single(element => element.Name.LocalName == "metadata")
            ?? throw new InvalidDataException("Missing NuGet metadata.");
        var id = metadata.Elements().Single(element => element.Name.LocalName == "id").Value;
        var version = metadata.Elements().Single(element => element.Name.LocalName == "version").Value;
        if (source.PackageId is not null && (!string.Equals(source.PackageId, id, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(source.Version, version, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The downloaded NuGet identity does not match the selected package/version.");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5389",
        Justification = "Each entry passes RequireRelativePath, link/case-collision checks, GetFullPath and RequireContained before CreateNew; traversal and link regressions execute this path.")]
    private static async Task ExtractAsync(string archivePath, string destination, CancellationToken cancellationToken)
    {
        var archive = await ZipFile.OpenReadAsync(archivePath, cancellationToken).ConfigureAwait(false);
        await using var archiveAsyncDisposal = archive.ConfigureAwait(true);
        if (archive.Entries.Count > MaximumFiles) throw new InvalidDataException("Too many module archive entries.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = entry.FullName.TrimEnd('/');
            RequireRelativePath(relative);
            var type = (entry.ExternalAttributes >> 16) & 0xF000;
            if (type is not (0 or 0x8000 or 0x4000) ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || !paths.Add(relative))
                throw new InvalidDataException("Links, special files and colliding archive paths are forbidden.");
            var outputPath = Path.GetFullPath(Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar)));
            RequireContained(destination, outputPath);
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(outputPath); continue; }
            total = checked(total + entry.Length);
            if (entry.Length < 0 || total > MaximumExpandedBytes) throw new InvalidDataException("Expanded module exceeds its size limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using var outputAsyncDisposal = output.ConfigureAwait(true);
            var input = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var inputAsyncDisposal2 = input.ConfigureAwait(true);
            await ModulePackageSources.CopyBoundedAsync(input, output, entry.Length, cancellationToken).ConfigureAwait(false);
            if (output.Length != entry.Length) throw new InvalidDataException("Module archive entry length mismatch.");
        }
    }

    private static async Task CopyDirectoryAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var queue = new Queue<string>();
        queue.Enqueue(source);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        var count = 0;
        while (queue.TryDequeue(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireNoLinks(entry);
                var relative = Path.GetRelativePath(source, entry).Replace(Path.DirectorySeparatorChar, '/');
                RequireRelativePath(relative);
                if (!paths.Add(relative) || ++count > MaximumFiles) throw new InvalidDataException("Too many or colliding module paths.");
                var target = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
                RequireContained(destination, target);
                if (Directory.Exists(entry)) { Directory.CreateDirectory(target); queue.Enqueue(entry); continue; }
                var length = new FileInfo(entry).Length;
                total = checked(total + length);
                if (total > MaximumExpandedBytes) throw new InvalidDataException("Module directory exceeds its size limit.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var input = File.OpenRead(entry);
                await using var inputAsyncDisposal_ = input.ConfigureAwait(true);
                var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await using var outputAsyncDisposal_ = output.ConfigureAwait(true);
                await ModulePackageSources.CopyBoundedAsync(input, output, length, cancellationToken).ConfigureAwait(false);
                if (output.Length != length) throw new InvalidDataException("Module source changed while copying.");
            }
        }
    }

    private static async Task<IReadOnlyList<ModulePayloadFile>> HashFilesAsync(string root, CancellationToken cancellationToken)
    {
        var result = new List<ModulePayloadFile>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            RequireNoLinks(path);
            var input = File.OpenRead(path);
            await using var inputAsyncDisposal__ = input.ConfigureAwait(true);
            result.Add(new(Path.GetRelativePath(root, path), input.Length,
                Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false))));
        }
        return result;
    }

    internal static void RequireRelativePath(string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Length > 512 || relative[0] == '/' ||
            relative.Contains('\\', StringComparison.Ordinal) || relative.Contains(':', StringComparison.Ordinal) || relative.Any(char.IsControl) ||
            relative.Split('/').Any(part => part.Length is 0 or > 180 || part is "." or ".." ||
                part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 ||
                IsDeviceName(part)))
            throw new InvalidDataException("Unsafe or nonportable module path.");
    }

    private static bool IsDeviceName(string part)
    {
        var name = part.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" ||
            (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) &&
                name[3] is >= '0' and <= '9');
    }

    private static void RequireContained(string root, string path)
    {
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Module path escapes its owned root.");
    }

    internal static void RequireNoLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Module paths cannot traverse symbolic links or reparse points.");
            current = Path.GetDirectoryName(current);
        }
    }

    public void Dispose() { _sources.Dispose(); _gate.Dispose(); }
}
