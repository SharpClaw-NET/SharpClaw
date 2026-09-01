using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Serilog;
using SharpClaw.Gateway.Contracts;
using SharpClaw.ModuleHost.InProcess;
using SharpClaw.Shared.Security;

namespace SharpClaw.Gateway.Modules;

/// <summary>
/// Discovers and instantiates <see cref="IGatewayModuleExtension"/>
/// implementations from <c>SharpClaw.Modules.*.dll</c> assemblies sitting
/// next to the gateway executable. Mirrors the API-side
/// <c>ModuleLoader.DiscoverBundled</c> shape but targets the gateway-only
/// extension contract.
/// </summary>
/// <remarks>
/// Phase 5b: discovery records the DLL path for each module so the host
/// manager can spin up a collectible <see cref="ModuleLoadContext"/> per
/// module and reload it when the file hash changes. The legacy
/// <see cref="FromExtensions"/> factory remains for unit tests that wire a
/// pre-built extension instance with no on-disk DLL.
/// </remarks>
public sealed class GatewayModuleLoader
{
    private readonly Dictionary<string, ModuleEntry> _entries;

    private GatewayModuleLoader(IEnumerable<ModuleEntry> entries)
    {
        _entries = new Dictionary<string, ModuleEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
            _entries[entry.ModuleId] = entry;
    }

    /// <summary>
    /// Test-friendly factory that bypasses disk scanning and seeds the loader
    /// with the supplied extensions. Production code uses
    /// <see cref="DiscoverBundled"/>; tests use this overload to wire a
    /// synthetic <see cref="IGatewayModuleExtension"/> into the pipeline.
    /// Entries created this way carry no DLL path, so Phase 5b ALC reload
    /// is unavailable for them — they always use the <c>InProcess</c>
    /// loader strategy.
    /// </summary>
    public static GatewayModuleLoader FromExtensions(IEnumerable<IGatewayModuleExtension> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        return new GatewayModuleLoader(extensions.Select(ModuleEntry.FromExtension));
    }

    /// <summary>
    /// Reads module manifests before it loads module code. Disabled modules
    /// remain inert metadata entries. Enabled modules load into collectible
    /// contexts that the module host owns.
    /// </summary>
    public static GatewayModuleLoader DiscoverBundled(
        Serilog.ILogger logger,
        GatewayModuleOptions options,
        string? modulesRoot = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);

        var baseDir = Path.GetFullPath(modulesRoot
            ?? Path.Combine(AppContext.BaseDirectory, "modules"));
        if (!Directory.Exists(baseDir))
            return new GatewayModuleLoader([]);

        var discovered = new List<ModuleEntry>();
        foreach (var manifestPath in Directory.EnumerateFiles(
                     baseDir,
                     "module.json",
                     SearchOption.AllDirectories)
                 .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var safeManifest = PathGuard.EnsureContainedIn(manifestPath, baseDir);
                using var document = JsonDocument.Parse(File.ReadAllText(safeManifest));
                var root = document.RootElement;
                var moduleId = RequiredString(root, "id", safeManifest);
                var entryAssembly = RequiredString(root, "entryAssembly", safeManifest);
                if (!string.Equals(entryAssembly, Path.GetFileName(entryAssembly), StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Module manifest '{safeManifest}' has an invalid entryAssembly value.");
                }

                var moduleDirectory = Path.GetDirectoryName(safeManifest)!;
                var dllPath = PathGuard.EnsureContainedIn(
                    Path.Combine(moduleDirectory, entryAssembly),
                    moduleDirectory);
                if (!File.Exists(dllPath))
                {
                    throw new FileNotFoundException(
                        $"Module entry assembly '{entryAssembly}' does not exist.",
                        dllPath);
                }

                discovered.Add(new ModuleEntry(moduleId, dllPath));
            }
            catch (Exception ex)
            {
                logger.Warning(
                    ex,
                    "Skipping module manifest {Manifest}",
                    PathGuard.SanitizeForLog(manifestPath));
            }
        }

        var keep = new List<ModuleEntry>(discovered.Count);
        foreach (var group in discovered.GroupBy(entry => entry.ModuleId, StringComparer.Ordinal))
        {
            if (group.Count() > 1)
            {
                logger.Error(
                    "Duplicate gateway module id {ModuleId}; dropping all {Count} contributions.",
                    group.Key,
                    group.Count());
                continue;
            }

            var entry = group.Single();
            if (options.IsModuleEnabled(entry.ModuleId))
            {
                try
                {
                    var loaded = LoadFromDisk(entry.DllPath!, baseDir);
                    if (!string.Equals(loaded.Extension.ModuleId, entry.ModuleId, StringComparison.Ordinal))
                    {
                        loaded.Context.Unload();
                        throw new InvalidDataException(
                            $"Module '{entry.ModuleId}' loaded extension '{loaded.Extension.ModuleId}'.");
                    }

                    entry.SetLoaded(loaded.Context, loaded.Extension);
                }
                catch (Exception ex)
                {
                    logger.Warning(
                        ex,
                        "Failed to load enabled gateway module {ModuleId}",
                        PathGuard.SanitizeForLog(entry.ModuleId));
                    continue;
                }
            }

            keep.Add(entry);
        }

        return new GatewayModuleLoader(keep);
    }

    /// <summary>All discovered module ids, regardless of enabled state.</summary>
    public IReadOnlyCollection<string> AllModuleIds => _entries.Keys;

    /// <summary>
    /// All entries the loader knows about. Each entry carries a module id
    /// and either an in-memory extension (for tests) or a DLL path (for
    /// disk-discovered modules).
    /// </summary>
    public IReadOnlyCollection<ModuleEntry> AllEntries => _entries.Values;

    /// <summary>
    /// Pre-instantiated extensions, when available. Phase 5b populates this
    /// only for entries created via <see cref="FromExtensions"/>; disk
    /// entries return their extension lazily through <see cref="Get"/>
    /// after the host manager loads them.
    /// </summary>
    public IReadOnlyCollection<IGatewayModuleExtension> All
        => _entries.Values
            .Where(e => e.Extension is not null)
            .Select(e => e.Extension!)
            .ToArray();

    /// <summary>Resolve an entry by its module id.</summary>
    public ModuleEntry? GetEntry(string moduleId)
        => _entries.GetValueOrDefault(moduleId);

    /// <summary>
    /// Resolve an extension by its module id. Returns the cached in-memory
    /// extension when one exists; otherwise returns <c>null</c> — the host
    /// manager is the sole owner of disk-loaded extension instances.
    /// </summary>
    public IGatewayModuleExtension? Get(string moduleId)
        => _entries.GetValueOrDefault(moduleId)?.Extension;

    /// <summary>
    /// Loads the module's main DLL into a fresh collectible ALC, finds the
    /// first concrete <see cref="IGatewayModuleExtension"/> implementation
    /// with a public parameterless constructor, and returns the loaded
    /// host. The caller owns the returned context and is responsible for
    /// unloading it.
    /// </summary>
    public static (ModuleLoadContext Context, IGatewayModuleExtension Extension)
        LoadFromDisk(
            string dllPath,
            string? allowedRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dllPath);

        var safe = PathGuard.EnsureContainedIn(
            dllPath,
            allowedRoot ?? AppContext.BaseDirectory);
        var context = new ModuleLoadContext(safe);
        var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(safe));

        var extensionType = typeof(IGatewayModuleExtension);
        var concrete = assembly.GetTypes()
            .FirstOrDefault(t => t is { IsClass: true, IsAbstract: false, IsPublic: true }
                                 && extensionType.IsAssignableFrom(t)
                                 && t.GetConstructor(Type.EmptyTypes) is not null)
            ?? throw new InvalidOperationException(
                $"No IGatewayModuleExtension implementation found in '{Path.GetFileName(safe)}'.");

        var instance = (IGatewayModuleExtension)Activator.CreateInstance(concrete)!;
        return (context, instance);
    }

    /// <summary>
    /// SHA-256 hash of the file at <paramref name="dllPath"/>. Used by the
    /// sync poller and the host manager to decide whether a reload is
    /// required. Returns <c>null</c> when the file is missing.
    /// </summary>
    public static string? ComputeDllHash(string dllPath)
    {
        if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath))
            return null;
        using var stream = File.OpenRead(dllPath);
        var bytes = SHA256.HashData(stream);
        return Convert.ToHexString(bytes);
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    private static string RequiredString(
        JsonElement root,
        string propertyName,
        string manifestPath)
    {
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException(
                $"Module manifest '{manifestPath}' requires string property '{propertyName}'.");
        }

        return property.GetString()!;
    }

    /// <summary>
    /// One entry in the loader table. An enabled disk entry can transfer its
    /// prepared collectible context to the host manager. A disabled disk
    /// entry keeps metadata only until the manager enables it.
    /// </summary>
    public sealed class ModuleEntry
    {
        private ModuleLoadContext? _context;

        public ModuleEntry(string moduleId, string? dllPath, IGatewayModuleExtension? extension = null)
        {
            ModuleId = moduleId;
            DllPath = dllPath;
            Extension = extension;
        }

        public string ModuleId { get; }

        public string? DllPath { get; }

        public IGatewayModuleExtension? Extension { get; private set; }

        internal static ModuleEntry FromExtension(IGatewayModuleExtension ext)
            => new(ext.ModuleId, dllPath: null, extension: ext);

        internal void SetLoaded(ModuleLoadContext context, IGatewayModuleExtension extension)
        {
            _context = context;
            Extension = extension;
        }

        internal bool TryTakeLoaded(
            out ModuleLoadContext? context,
            out IGatewayModuleExtension? extension)
        {
            context = _context;
            extension = Extension;
            if (context is null || extension is null)
                return false;

            _context = null;
            Extension = null;
            return true;
        }
    }
}
