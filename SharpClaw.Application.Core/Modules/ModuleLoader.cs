using System.Reflection;
using System.Text.Json;

using SharpClaw.Contracts.Modules;

namespace SharpClaw.Application.Core.Modules;

/// <summary>
/// Singleton that holds all bundled (default) module instances and provides
/// manifest loading. Modules are always instantiated, but only registered
/// with <see cref="ModuleRegistry"/> when enabled.
/// </summary>
public sealed class ModuleLoader
{
    private readonly Dictionary<string, ISharpClawModule> _bundled;
    private Dictionary<string, ModuleManifest>? _manifestsCache;
    private IServiceProvider? _rootServices;

    public ModuleLoader(params ISharpClawModule[] modules)
    {
        _bundled = modules.ToDictionary(m => m.Id, StringComparer.Ordinal);
    }

    /// <summary>
    /// Discover bundled <see cref="ISharpClawModule"/> implementations by loading all
    /// <c>SharpClaw.Modules.*.dll</c> assemblies from the application base directory,
    /// then scanning for concrete types with a public parameterless constructor.
    /// Returns a new <see cref="ModuleLoader"/> populated with one instance of each
    /// discovered module.
    /// </summary>
    public static ModuleLoader DiscoverBundled()
    {
        // Module assemblies are only present in the output directory via ProjectReference
        // but are NOT loaded into the AppDomain unless something forces type resolution.
        // Explicitly load all matching DLLs so the type scan below can find them.
        var baseDir = AppContext.BaseDirectory;
        foreach (var dll in Directory.GetFiles(baseDir, "SharpClaw.Modules.*.dll"))
        {
            try
            {
                Assembly.LoadFrom(dll);
            }
            catch
            {
                // Skip assemblies that fail to load (e.g. native-only, already loaded
                // under a different identity, or missing transitive dependencies).
            }
        }

        var moduleType = typeof(ISharpClawModule);
        var modules = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic)
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
            })
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && moduleType.IsAssignableFrom(t)
                        && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (ISharpClawModule)Activator.CreateInstance(t)!)
            .ToArray();

        return new ModuleLoader(modules);
    }

    /// <summary>
    /// The root <see cref="IServiceProvider"/> set after the host is built.
    /// Used by <see cref="ModuleService.EnableAsync"/> to run module initialization.
    /// </summary>
    public IServiceProvider RootServices =>
        _rootServices ?? throw new InvalidOperationException("RootServices not yet set — call SetRootServices after Build.");

    /// <summary>Assign the built <see cref="IServiceProvider"/>. Called once from Program.cs after Build.</summary>
    public void SetRootServices(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _rootServices = services;
    }

    /// <summary>Get a bundled module by its ID, or <c>null</c> if unknown.</summary>
    public ISharpClawModule? GetBundledModule(string moduleId)
        => _bundled.GetValueOrDefault(moduleId);

    /// <summary>Get all bundled module instances (regardless of enabled state).</summary>
    public IReadOnlyList<ISharpClawModule> GetAllBundled() => [.. _bundled.Values];

    /// <summary>Whether a module ID refers to a known default module.</summary>
    public bool IsDefaultModule(string moduleId) => _bundled.ContainsKey(moduleId);

    /// <summary>
    /// Check whether a module is enabled in the current <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>.
    /// Returns <c>true</c> only when the key <c>Modules:{moduleId}</c> is explicitly <c>"true"</c>.
    /// Missing or any other value means disabled.
    /// </summary>
    public static bool IsEnabledInConfig(string moduleId, Microsoft.Extensions.Configuration.IConfiguration config)
    {
        var value = config[$"Modules:{moduleId}"];
        return bool.TryParse(value, out var enabled) && enabled;
    }

    // ═══════════════════════════════════════════════════════════════
    // Manifest loading
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Scan the <c>modules/</c> subdirectories next to the host assembly for
    /// <c>module.json</c> manifests. Results are cached after the first call.
    /// </summary>
    public IReadOnlyDictionary<string, ModuleManifest> LoadAllManifests()
    {
        if (_manifestsCache is not null) return _manifestsCache;

        _manifestsCache = new Dictionary<string, ModuleManifest>(StringComparer.Ordinal);

        var baseDir = Path.GetDirectoryName(typeof(ModuleLoader).Assembly.Location)!;
        var modulesDir = Path.Combine(baseDir, "modules");

        if (!Directory.Exists(modulesDir)) return _manifestsCache;

        foreach (var dir in Directory.EnumerateDirectories(modulesDir))
        {
            var manifestPath = Path.Combine(dir, "module.json");
            if (!File.Exists(manifestPath)) continue;

            try
            {
                var json = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize<ModuleManifest>(json, SecureJsonOptions.Manifest);
                if (manifest is not null)
                    _manifestsCache[manifest.Id] = manifest;
            }
            catch
            {
                // Skip malformed manifests — logged at a higher level if needed.
            }
        }

        return _manifestsCache;
    }

    /// <summary>Get a cached manifest for a specific module, or <c>null</c>.</summary>
    public ModuleManifest? GetManifest(string moduleId)
    {
        var all = LoadAllManifests();
        return all.GetValueOrDefault(moduleId);
    }
}
