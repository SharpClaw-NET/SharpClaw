using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using SharpClaw.Contracts.Modules;
using SharpClaw.Utils.Security;

namespace SharpClaw.Application.Core.Modules;

public sealed record NuGetModulePackageReference(
    string PackageId,
    string Version,
    string? Source = null,
    string? ModulePath = null)
{
    public const string DefaultSource = "https://api.nuget.org/v3-flatcontainer/";
}

internal static partial class NuGetModulePackageResolver
{
    public static async Task<string> ResolveAsync(
        NuGetModulePackageReference package,
        string packagesRoot,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(packagesRoot);

        ValidatePackageId(package.PackageId);
        ValidateVersion(package.Version);
        ValidateModulePath(package.ModulePath);

        var source = string.IsNullOrWhiteSpace(package.Source)
            ? NuGetModulePackageReference.DefaultSource
            : package.Source.Trim();
        var canonicalRoot = Directory.CreateDirectory(packagesRoot).FullName;
        var packageDir = ResolvePackageDirectory(canonicalRoot, package.PackageId, package.Version, source);

        if (HasLoadableModule(packageDir))
            return packageDir;

        var scratchRoot = Directory.CreateDirectory(
            Path.Combine(canonicalRoot, ".install")).FullName;
        var scratchDir = Path.Combine(scratchRoot, Guid.NewGuid().ToString("N"));
        var extractDir = Path.Combine(scratchDir, "extract");
        var materializedDir = Path.Combine(scratchDir, "module");
        Directory.CreateDirectory(extractDir);
        Directory.CreateDirectory(materializedDir);

        try
        {
            var packageFile = await ResolvePackageFileAsync(package.PackageId, package.Version, source, scratchDir, ct);
            ExtractPackage(packageFile, extractDir);
            MaterializeModuleDirectory(extractDir, materializedDir, package.ModulePath);

            if (Directory.Exists(packageDir))
                Directory.Delete(packageDir, recursive: true);

            Directory.CreateDirectory(Path.GetDirectoryName(packageDir)!);
            Directory.Move(materializedDir, packageDir);
            return packageDir;
        }
        finally
        {
            if (Directory.Exists(scratchDir))
                Directory.Delete(scratchDir, recursive: true);
        }
    }

    private static async Task<string> ResolvePackageFileAsync(
        string packageId,
        string version,
        string source,
        string scratchDir,
        CancellationToken ct)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return await DownloadPackageAsync(packageId, version, uri, scratchDir, ct);
        }

        var localSource = Path.GetFullPath(source);
        if (Directory.Exists(localSource))
        {
            var expectedName = $"{packageId}.{version}.nupkg";
            var match = Directory.EnumerateFiles(localSource, "*.nupkg")
                .FirstOrDefault(f => string.Equals(
                    Path.GetFileName(f),
                    expectedName,
                    StringComparison.OrdinalIgnoreCase));

            if (match is not null)
                return match;

            throw new FileNotFoundException(
                $"NuGet package '{expectedName}' was not found in '{localSource}'.");
        }

        if (File.Exists(localSource))
        {
            PathGuard.EnsureExtension(localSource, ".nupkg");
            return localSource;
        }

        throw new DirectoryNotFoundException(
            $"NuGet package source '{source}' was not found as a local file, local directory, or HTTP source.");
    }

    private static async Task<string> DownloadPackageAsync(
        string packageId,
        string version,
        Uri source,
        string scratchDir,
        CancellationToken ct)
    {
        var packageUri = ResolvePackageUri(packageId, version, source);
        var destination = Path.Combine(scratchDir, $"{packageId}.{version}.nupkg");

        using var client = new HttpClient();
        using var response = await client.GetAsync(packageUri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, ct);

        return destination;
    }

    private static Uri ResolvePackageUri(string packageId, string version, Uri source)
    {
        if (source.AbsolutePath.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
            return source;

        if (string.Equals(source.Host, "api.nuget.org", StringComparison.OrdinalIgnoreCase)
            && source.AbsolutePath.EndsWith("/v3/index.json", StringComparison.OrdinalIgnoreCase))
        {
            source = new Uri("https://api.nuget.org/v3-flatcontainer/");
        }

        var baseUri = source.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? source.AbsoluteUri
            : source.AbsoluteUri + "/";
        var id = packageId.ToLowerInvariant();
        var normalizedVersion = version.ToLowerInvariant();
        return new Uri($"{baseUri}{id}/{normalizedVersion}/{id}.{normalizedVersion}.nupkg");
    }

    private static void ExtractPackage(string packageFile, string extractDir)
    {
        using var archive = ZipFile.OpenRead(packageFile);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            var destination = PathGuard.EnsureContainedIn(
                Path.Combine(extractDir, entry.FullName),
                extractDir);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private static void MaterializeModuleDirectory(
        string extractDir,
        string destinationDir,
        string? modulePath)
    {
        var moduleRoot = ResolveModuleRoot(extractDir, modulePath);
        CopyDirectory(moduleRoot, destinationDir);

        var manifest = ReadManifest(Path.Combine(destinationDir, ModuleFileNames.ManifestFile));
        var entryAssemblyPath = Path.Combine(destinationDir, manifest.EntryAssembly);
        if (!File.Exists(entryAssemblyPath))
        {
            var candidate = FindBestEntryAssembly(destinationDir, manifest.EntryAssembly)
                ?? throw new FileNotFoundException(
                    $"NuGet module package contains {ModuleFileNames.ManifestFile}, but entry assembly '{manifest.EntryAssembly}' was not found.");
            CopyDirectory(Path.GetDirectoryName(candidate)!, destinationDir);
        }

        if (!File.Exists(Path.Combine(destinationDir, manifest.EntryAssembly)))
        {
            throw new FileNotFoundException(
                $"NuGet module package did not materialize entry assembly '{manifest.EntryAssembly}' at the module root.");
        }
    }

    private static string ResolveModuleRoot(string extractDir, string? modulePath)
    {
        if (!string.IsNullOrWhiteSpace(modulePath))
        {
            var explicitRoot = PathGuard.EnsureContainedIn(Path.Combine(extractDir, modulePath), extractDir);
            var explicitManifest = Path.Combine(explicitRoot, ModuleFileNames.ManifestFile);
            if (!File.Exists(explicitManifest))
                throw new FileNotFoundException(
                    $"NuGet module path '{modulePath}' does not contain {ModuleFileNames.ManifestFile}.",
                    explicitManifest);

            return explicitRoot;
        }

        var candidates = Directory.EnumerateFiles(
                extractDir,
                ModuleFileNames.ManifestFile,
                SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .Where(d => d is not null)
            .Select(d => d!)
            .OrderBy(d => DirectoryDepth(extractDir, d))
            .ThenBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var candidate in candidates)
        {
            var manifest = ReadManifest(Path.Combine(candidate, ModuleFileNames.ManifestFile));
            if (File.Exists(Path.Combine(candidate, manifest.EntryAssembly))
                || Directory.EnumerateFiles(candidate, manifest.EntryAssembly, SearchOption.AllDirectories).Any())
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"NuGet package does not contain a loadable SharpClaw {ModuleFileNames.ManifestFile}.");
    }

    private static ModuleManifest ReadManifest(string manifestPath)
    {
        var json = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<ModuleManifest>(json, SecureJsonOptions.Manifest)
            ?? throw new InvalidOperationException($"Failed to parse module manifest '{manifestPath}'.");
        PathGuard.EnsureFileName(manifest.EntryAssembly, nameof(manifest.EntryAssembly));
        PathGuard.EnsureExtension(manifest.EntryAssembly, ".dll");
        return manifest;
    }

    private static string? FindBestEntryAssembly(string moduleDir, string entryAssembly)
    {
        return Directory.EnumerateFiles(moduleDir, entryAssembly, SearchOption.AllDirectories)
            .OrderBy(path => TargetFrameworkScore(path))
            .ThenBy(path => DirectoryDepth(moduleDir, Path.GetDirectoryName(path)!))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static int TargetFrameworkScore(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.Contains("/net10.0/", StringComparison.OrdinalIgnoreCase)) return 0;
        if (normalized.Contains("/net9.0/", StringComparison.OrdinalIgnoreCase)) return 1;
        if (normalized.Contains("/net8.0/", StringComparison.OrdinalIgnoreCase)) return 2;
        if (normalized.Contains("/netstandard2.1/", StringComparison.OrdinalIgnoreCase)) return 3;
        if (normalized.Contains("/netstandard2.0/", StringComparison.OrdinalIgnoreCase)) return 4;
        return 10;
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var directory in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, directory);
            Directory.CreateDirectory(Path.Combine(destinationDir, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var destination = Path.Combine(destinationDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static string ResolvePackageDirectory(
        string packagesRoot,
        string packageId,
        string version,
        string source)
    {
        var idSegment = packageId.ToLowerInvariant();
        var versionSegment = version.ToLowerInvariant();
        var sourceHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(source)))
            .ToLowerInvariant()[..12];

        return PathGuard.EnsureContainedIn(
            Path.Combine(packagesRoot, idSegment, versionSegment, sourceHash),
            packagesRoot);
    }

    private static bool HasLoadableModule(string moduleDir)
    {
        var manifestPath = Path.Combine(moduleDir, ModuleFileNames.ManifestFile);
        if (!File.Exists(manifestPath))
            return false;

        try
        {
            var manifest = ReadManifest(manifestPath);
            return File.Exists(Path.Combine(moduleDir, manifest.EntryAssembly));
        }
        catch
        {
            return false;
        }
    }

    private static int DirectoryDepth(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "."
            ? 0
            : relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static void ValidatePackageId(string packageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        if (!PackageIdPattern().IsMatch(packageId))
            throw new ArgumentException(
                $"NuGet package id '{packageId}' is invalid.",
                nameof(packageId));
    }

    private static void ValidateVersion(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (!PackageVersionPattern().IsMatch(version))
            throw new ArgumentException(
                $"NuGet package version '{version}' is invalid.",
                nameof(version));
    }

    private static void ValidateModulePath(string? modulePath)
    {
        if (string.IsNullOrWhiteSpace(modulePath))
            return;

        if (modulePath.Contains('\0') || Path.IsPathRooted(modulePath))
            throw new ArgumentException("NuGet module path must be a relative package path.", nameof(modulePath));
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.+-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageVersionPattern();
}
