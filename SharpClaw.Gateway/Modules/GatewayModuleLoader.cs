using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using SharpClaw.Gateway.Abstractions;
using SharpClaw.ModuleHost.InProcess;
using SharpClaw.Utils.Security;

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
    /// Scan the application base directory for <c>SharpClaw.Modules.*.dll</c>
    /// assemblies, load them through <see cref="PathGuard.EnsureContainedIn"/>,
    /// and instantiate every concrete <see cref="IGatewayModuleExtension"/>
    /// found. The probe runs in the default <c>AssemblyLoadContext</c>
    /// (assemblies that ship a gateway extension stay loaded in the default
    /// context for the lifetime of the gateway), but the DLL path is also
    /// recorded so the host manager can spin up a fresh collectible
    /// <see cref="ModuleLoadContext"/> per enable when hot-reload is on.
    /// Duplicate <c>ModuleId</c>s are logged and both contributions are
    /// dropped.
    /// </summary>
    public static GatewayModuleLoader DiscoverBundled(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var baseDir = AppContext.BaseDirectory;
        // Map each loaded assembly back to the DLL path that produced it so
        // we can stash that path in the entry alongside the discovered
        // extension instance.
        var dllByAssembly = new Dictionary<Assembly, string>();
        foreach (var dll in Directory.GetFiles(baseDir, "SharpClaw.Modules.*.dll"))
        {
            try
            {
                var safeDll = PathGuard.EnsureContainedIn(dll, baseDir);
                var asm = Assembly.LoadFrom(safeDll);
                dllByAssembly[asm] = safeDll;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Skipping module assembly {Dll}", PathGuard.SanitizeForLog(dll));
            }
        }

        var extensionType = typeof(IGatewayModuleExtension);
        var entries = new List<ModuleEntry>();
        foreach (var (asm, dllPath) in dllByAssembly)
        {
            foreach (var t in SafeGetTypes(asm))
            {
                if (t is not { IsClass: true, IsAbstract: false, IsPublic: true }
                    || !extensionType.IsAssignableFrom(t)
                    || t.GetConstructor(Type.EmptyTypes) is null)
                {
                    continue;
                }

                try
                {
                    var instance = (IGatewayModuleExtension)Activator.CreateInstance(t)!;
                    entries.Add(new ModuleEntry(instance.ModuleId, dllPath, instance));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to instantiate gateway extension {Type}", t.FullName);
                }
            }
        }

        var keep = new List<ModuleEntry>(entries.Count);
        foreach (var group in entries.GroupBy(e => e.ModuleId, StringComparer.Ordinal))
        {
            if (group.Count() > 1)
            {
                logger.LogError(
                    "Duplicate gateway module id {ModuleId}; dropping all {Count} contributions.",
                    group.Key,
                    group.Count());
                continue;
            }
            keep.Add(group.Single());
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
        LoadFromDisk(string dllPath, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dllPath);
        ArgumentNullException.ThrowIfNull(logger);

        var safe = PathGuard.EnsureContainedIn(dllPath, AppContext.BaseDirectory);
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

    /// <summary>
    /// One entry in the loader's table. Either an in-memory extension
    /// (test path) or a DLL path (production path). Disk entries do not
    /// carry an extension instance — the host manager loads it lazily
    /// through <see cref="LoadFromDisk"/>.
    /// </summary>
    public sealed record ModuleEntry(
        string ModuleId,
        string? DllPath,
        IGatewayModuleExtension? Extension)
    {
        internal static ModuleEntry FromExtension(IGatewayModuleExtension ext)
            => new(ext.ModuleId, DllPath: null, Extension: ext);
    }
}
