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
