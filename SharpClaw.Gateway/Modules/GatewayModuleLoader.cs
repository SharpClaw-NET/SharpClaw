using System.Reflection;
using Microsoft.Extensions.Logging;
using SharpClaw.Gateway.Abstractions;
using SharpClaw.Utils.Security;

namespace SharpClaw.Gateway.Modules;

/// <summary>
/// Discovers and instantiates <see cref="IGatewayModuleExtension"/>
/// implementations from <c>SharpClaw.Modules.*.dll</c> assemblies sitting
/// next to the gateway executable. Mirrors the API-side
/// <c>ModuleLoader.DiscoverBundled</c> shape but targets the gateway-only
/// extension contract.
/// </summary>
public sealed class GatewayModuleLoader
{
    private readonly Dictionary<string, IGatewayModuleExtension> _extensions;

    private GatewayModuleLoader(IEnumerable<IGatewayModuleExtension> extensions)
    {
        _extensions = new Dictionary<string, IGatewayModuleExtension>(StringComparer.Ordinal);
        foreach (var ext in extensions)
            _extensions[ext.ModuleId] = ext;
    }

    /// <summary>
    /// Test-friendly factory that bypasses disk scanning and seeds the loader
    /// with the supplied extensions. Production code uses
    /// <see cref="DiscoverBundled"/>; tests use this overload to wire a
    /// synthetic <see cref="IGatewayModuleExtension"/> into the pipeline.
    /// </summary>
    public static GatewayModuleLoader FromExtensions(IEnumerable<IGatewayModuleExtension> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        return new GatewayModuleLoader(extensions);
    }

    /// <summary>
    /// Scan the application base directory for <c>SharpClaw.Modules.*.dll</c>
    /// assemblies, load them through <see cref="PathGuard.EnsureContainedIn"/>,
    /// then enumerate concrete public-parameterless-constructor types that
    /// implement <see cref="IGatewayModuleExtension"/>. Duplicate
    /// <c>ModuleId</c>s are logged and both contributions are dropped.
    /// </summary>
    public static GatewayModuleLoader DiscoverBundled(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var baseDir = AppContext.BaseDirectory;
        foreach (var dll in Directory.GetFiles(baseDir, "SharpClaw.Modules.*.dll"))
        {
            try
            {
                var safeDll = PathGuard.EnsureContainedIn(dll, baseDir);
                Assembly.LoadFrom(safeDll);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Skipping module assembly {Dll}", PathGuard.SanitizeForLog(dll));
            }
        }

        var extensionType = typeof(IGatewayModuleExtension);
        var instances = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic)
            .SelectMany(SafeGetTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true }
                        && extensionType.IsAssignableFrom(t)
                        && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t =>
            {
                try { return (IGatewayModuleExtension?)Activator.CreateInstance(t); }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to instantiate gateway extension {Type}", t.FullName);
                    return null;
                }
            })
            .Where(e => e is not null)
            .Cast<IGatewayModuleExtension>()
            .ToArray();

        var keep = new List<IGatewayModuleExtension>(instances.Length);
        foreach (var group in instances.GroupBy(e => e.ModuleId, StringComparer.Ordinal))
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

    /// <summary>All discovered extensions, regardless of enabled state.</summary>
    public IReadOnlyCollection<IGatewayModuleExtension> All => _extensions.Values;

    /// <summary>Resolve an extension by its <see cref="IGatewayModuleExtension.ModuleId"/>.</summary>
    public IGatewayModuleExtension? Get(string moduleId)
        => _extensions.GetValueOrDefault(moduleId);

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}
