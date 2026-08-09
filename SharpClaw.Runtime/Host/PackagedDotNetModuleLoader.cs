using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleHost.InProcess;
using SharpClaw.Runtime.BLL.Modules;

namespace SharpClaw.Runtime.Host;

/// <summary>Loads enabled .NET modules whose manifest selects in-process hosting.</summary>
internal sealed class PackagedDotNetModuleSet : IDisposable
{
    private readonly IReadOnlyList<ModuleLoadContext> _loadContexts;

    private PackagedDotNetModuleSet(
        IReadOnlyList<ISharpClawModule> modules,
        IReadOnlyList<ModuleLoadContext> loadContexts)
    {
        Modules = modules;
        _loadContexts = loadContexts;
    }

    public IReadOnlyList<ISharpClawModule> Modules { get; }

    public static PackagedDotNetModuleSet Load(
        string modulesRoot,
        IConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modulesRoot);
        return Load([modulesRoot], configuration);
    }

    internal static PackagedDotNetModuleSet Load(
        IReadOnlyList<string> moduleRoots,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(moduleRoots);
        ArgumentNullException.ThrowIfNull(configuration);

        var modules = new List<ISharpClawModule>();
        var contexts = new List<ModuleLoadContext>();
        var moduleIds = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (var (root, manifestPath) in moduleRoots
                         .Where(path => !string.IsNullOrWhiteSpace(path))
                         .Select(Path.GetFullPath)
                         .Where(Directory.Exists)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .SelectMany(root => Directory.EnumerateFiles(
                             root,
                             "module.json",
                             SearchOption.AllDirectories)
                             .Select(path => (root, path)))
                         .OrderBy(item => item.path, StringComparer.OrdinalIgnoreCase))
            {
                var manifest = ReadManifest(manifestPath);
                if (!moduleIds.Add(manifest.Id))
                    throw new InvalidOperationException(
                        $"The module id '{manifest.Id}' is declared more than once.");

                if (!IsEnabled(manifest, configuration))
                    continue;

                if (!manifest.RuntimeInfo.IsDotNet)
                    throw new NotSupportedException(
                        $"The module '{manifest.Id}' declares unsupported runtime '{manifest.RuntimeInfo.Runtime}'. " +
                        "SharpClaw supports only .NET module runtimes.");

                if (!manifest.RuntimeInfo.IsInProcessHostMode)
                    continue;

                manifest.RuntimeInfo.EnsureDotNetEntryAssembly(manifest.Manifest);

                var moduleDirectory = Path.GetDirectoryName(manifestPath)!;
                var entryPath = ResolveContainedPath(
                    root,
                    moduleDirectory,
                    manifest.Manifest.EntryAssembly,
                    manifest.Id,
                    "entry assembly");
                if (!File.Exists(entryPath))
                    throw new FileNotFoundException(
                        $"The in-process module '{manifest.Id}' entry assembly was not found.",
                        entryPath);

                if (string.IsNullOrWhiteSpace(manifest.Manifest.ModuleType))
                    throw new InvalidOperationException(
                        $"The in-process module '{manifest.Id}' must declare moduleType.");

                var loadContext = new ModuleLoadContext(entryPath);
                var assembly = loadContext.LoadFromAssemblyPath(entryPath);
                var moduleType = assembly.GetType(manifest.Manifest.ModuleType, throwOnError: false);
                if (moduleType is null)
                    throw new InvalidOperationException(
                        $"The in-process module '{manifest.Id}' type '{manifest.Manifest.ModuleType}' was not found.");

                if (!typeof(ISharpClawModule).IsAssignableFrom(moduleType) ||
                    moduleType.IsAbstract ||
                    moduleType.GetConstructor(Type.EmptyTypes) is null)
                {
                    throw new InvalidOperationException(
                        $"The in-process module '{manifest.Id}' type '{manifest.Manifest.ModuleType}' " +
                        "must be a concrete ISharpClawModule with a public parameterless constructor.");
                }

                var module = Activator.CreateInstance(moduleType) as ISharpClawModule
                    ?? throw new InvalidOperationException(
                        $"The in-process module '{manifest.Id}' could not be created.");
                if (!string.Equals(module.Identity.Id, manifest.Id, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"The in-process module '{manifest.Id}' identity is '{module.Identity.Id}'.");

                contexts.Add(loadContext);
                modules.Add(module);
            }

            return new PackagedDotNetModuleSet(modules, contexts);
        }
        catch
        {
            foreach (var context in contexts)
                context.Unload();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var context in _loadContexts)
            context.Unload();
    }

    private static PackagedModuleManifest ReadManifest(string path)
    {
        var json = File.ReadAllText(path);
        var manifest = SecureJsonOptions.DeserializeManifest(json);
        var runtimeInfo = ModuleManifestRuntimeInfo.FromJson(json);
        using var document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = false,
            });
        var enabled = document.RootElement.TryGetProperty("enabled", out _)
            ? manifest.Enabled
            : manifest.DefaultEnabled;

        return new PackagedModuleManifest(
            manifest.Id,
            manifest,
            runtimeInfo,
            enabled);
    }

    private static bool IsEnabled(
        PackagedModuleManifest manifest,
        IConfiguration configuration)
    {
        var configuredValue = configuration[$"Modules:{manifest.Id}"];
        if (configuredValue is null)
            return manifest.IsEnabled;

        if (!bool.TryParse(configuredValue, out var enabled))
        {
            throw new InvalidOperationException(
                $"The module setting 'Modules:{manifest.Id}' must be true or false.");
        }

        return enabled;
    }

    private static string ResolveContainedPath(
        string root,
        string moduleDirectory,
        string relativePath,
        string moduleId,
        string description)
    {
        var fullRoot = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(moduleDirectory, relativePath));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"The in-process module '{moduleId}' {description} is outside the module root.");
        return path;
    }

    private sealed record PackagedModuleManifest(
        string Id,
        ModuleManifest Manifest,
        ModuleManifestRuntimeInfo RuntimeInfo,
        bool IsEnabled);
}
