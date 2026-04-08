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
    private readonly AssemblyDependencyResolver _resolver;

    public ModuleLoadContext(string mainDllPath)
        : base(name: Path.GetFileNameWithoutExtension(mainDllPath), isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainDllPath);
    }

    protected override Assembly? Load(AssemblyName name)
    {
        // Prefer the module's own dependencies (e.g. ClosedXML, NAudio).
        var path = _resolver.ResolveAssemblyToPath(name);
        if (path is not null)
            return LoadFromAssemblyPath(path);

        // Fall back to the default context. This ensures shared contract
        // types (ISharpClawModule, ModuleToolDefinition, etc.) have the
        // same identity across all modules and the host.
        return null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : 0;
    }
}
