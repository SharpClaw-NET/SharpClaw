using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleHost.InProcess;

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
        ArgumentNullException.ThrowIfNull(configuration);
        var root = Path.GetFullPath(modulesRoot);
        if (!Directory.Exists(root))
            return new([], []);

        var modules = new List<ISharpClawModule>();
        var contexts = new List<ModuleLoadContext>();
        var moduleIds = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (var manifestPath in Directory.EnumerateFiles(
                         root,
                         "module.json",
                         SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var manifest = ReadManifest(manifestPath);
                if (!IsEnabled(manifest, configuration) ||
                    !string.Equals(manifest.Runtime, "dotnet", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(manifest.HostMode, "inprocess", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!moduleIds.Add(manifest.Id))
                    throw new InvalidOperationException(
                        $"The in-process module id '{manifest.Id}' is declared more than once.");

                var moduleDirectory = Path.GetDirectoryName(manifestPath)!;
                var entryPath = ResolveContainedPath(
                    root,
                    moduleDirectory,
                    manifest.EntryAssembly,
                    manifest.Id,
                    "entry assembly");
                if (!File.Exists(entryPath))
                    throw new FileNotFoundException(
                        $"The in-process module '{manifest.Id}' entry assembly was not found.",
                        entryPath);

                var loadContext = new ModuleLoadContext(entryPath);
                var assembly = loadContext.LoadFromAssemblyPath(entryPath);
                var moduleType = assembly.GetType(manifest.ModuleType, throwOnError: false);
                if (moduleType is null)
                    throw new InvalidOperationException(
                        $"The in-process module '{manifest.Id}' type '{manifest.ModuleType}' was not found.");

                if (!typeof(ISharpClawModule).IsAssignableFrom(moduleType) ||
                    moduleType.IsAbstract ||
                    moduleType.GetConstructor(Type.EmptyTypes) is null)
                {
                    throw new InvalidOperationException(
                        $"The in-process module '{manifest.Id}' type '{manifest.ModuleType}' " +
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
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var id = RequiredString(root, "id", path);
        var runtime = RequiredString(root, "runtime", path);
        var hostMode = RequiredString(root, "hostMode", path);
        var entryAssembly = RequiredString(root, "entryAssembly", path);
        var moduleType = RequiredString(root, "moduleType", path);
        var enabled = root.TryGetProperty("enabled", out var enabledValue)
            ? enabledValue.ValueKind != JsonValueKind.False
            : root.TryGetProperty("defaultEnabled", out var defaultEnabledValue)
                ? defaultEnabledValue.ValueKind != JsonValueKind.False
                : true;

        return new PackagedModuleManifest(
            id,
            runtime,
            hostMode,
            entryAssembly,
            moduleType,
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

    private static string RequiredString(JsonElement root, string property, string path)
    {
        if (!root.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException(
                $"The module manifest '{path}' requires a nonblank '{property}' value.");
        }

        return value.GetString()!;
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
        string Runtime,
        string HostMode,
        string EntryAssembly,
        string ModuleType,
        bool IsEnabled);
}
