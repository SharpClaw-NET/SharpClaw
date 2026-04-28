using System.Reflection;
using System.Runtime.Loader;

namespace SharpClaw.Application.Core.Modules;

/// <summary>
/// Collectible <see cref="AssemblyLoadContext"/> for external modules.
/// Each external module directory gets its own context, enabling assembly
/// unloading when the module is removed or replaced at runtime.
/// <para>
/// The resolver prefers the module's own dependencies (next to its DLL),
/// falling back to the default context for shared types
/// (<c>SharpClaw.Contracts</c>, <c>Microsoft.Extensions.*</c>, etc.).
/// </para>
/// </summary>
internal sealed class ModuleLoadContext : AssemblyLoadContext
{
    /// <summary>
    /// Names/prefixes that must always resolve from the default ALC so the host and
    /// every module share the same <see cref="Type"/> identity. If any of these were
    /// resolved by <see cref="AssemblyDependencyResolver"/> from the module directory,
    /// the runtime would load a second copy and casts like
    /// <c>obj is ISharpClawModule</c> would silently fail with a type mismatch.
    /// </summary>
    private static readonly string[] HostSharedPrefixes =
    {
        "SharpClaw.Contracts",
        "SharpClaw.Utils",
        "SharpClaw.Application.Core",
        "SharpClaw.Application.Infrastructure",
        "Microsoft.Extensions.",
        "Microsoft.AspNetCore.",
        "Microsoft.EntityFrameworkCore",
        "System.",
        "netstandard",
        "mscorlib",
    };

    private readonly AssemblyDependencyResolver _resolver;

    public ModuleLoadContext(string mainDllPath)
        : base(name: Path.GetFileNameWithoutExtension(mainDllPath), isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainDllPath);
    }

    protected override Assembly? Load(AssemblyName name)
    {
        // Always delegate host-shared assemblies to the default ALC. Without this
        // guard, AssemblyDependencyResolver would happily return a copy that the
        // module ships next to itself (e.g. SharpClaw.Contracts.dll), causing an
        // ALC collision: the host's ISharpClawModule and the module's
        // ISharpClawModule would be two distinct types.
        if (name.Name is { Length: > 0 } shortName)
        {
            for (var i = 0; i < HostSharedPrefixes.Length; i++)
            {
                var prefix = HostSharedPrefixes[i];
                if (prefix.EndsWith('.')
                    ? shortName.StartsWith(prefix, StringComparison.Ordinal)
                    : shortName.Equals(prefix, StringComparison.Ordinal)
                      || shortName.StartsWith(prefix + ".", StringComparison.Ordinal))
                {
                    return null; // delegate to default ALC for shared identity
                }
            }
        }

        // Prefer the module's own dependencies (e.g. ClosedXML, NAudio).
        var path = _resolver.ResolveAssemblyToPath(name);
        if (path is not null)
            return LoadFromAssemblyPath(path);

        // Fall back to the default context for anything else.
        return null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : 0;
    }
}
