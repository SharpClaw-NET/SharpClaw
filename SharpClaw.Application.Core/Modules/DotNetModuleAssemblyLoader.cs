using System.Reflection;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Application.Core.Modules;

internal static class DotNetModuleAssemblyLoader
{
    public static ISharpClawCoreModule CreateModuleInstance(
        Assembly assembly,
        ModuleManifest manifest,
        ModuleManifestRuntimeInfo runtimeInfo,
        string dllPath)
    {
        var moduleTypes = assembly.GetTypes()
            .Where(t => t.IsAssignableTo(typeof(ISharpClawCoreModule)) && !t.IsAbstract)
            .ToArray();

        if (moduleTypes.Length == 0)
        {
            throw new InvalidOperationException(
                $"No ISharpClawCoreModule implementation found in '{Path.GetFileName(dllPath)}'.");
        }

        if (!string.IsNullOrWhiteSpace(runtimeInfo.ModuleType))
        {
            var explicitType = moduleTypes.FirstOrDefault(t =>
                string.Equals(t.FullName, runtimeInfo.ModuleType, StringComparison.Ordinal)
                || string.Equals(t.AssemblyQualifiedName, runtimeInfo.ModuleType, StringComparison.Ordinal)
                || string.Equals(t.Name, runtimeInfo.ModuleType, StringComparison.Ordinal));

            if (explicitType is null)
            {
                throw new InvalidOperationException(
                    $"Module '{manifest.Id}' declares moduleType '{runtimeInfo.ModuleType}', " +
                    $"but that type was not found in '{Path.GetFileName(dllPath)}'.");
            }

            return (ISharpClawCoreModule)Activator.CreateInstance(explicitType)!;
        }

        foreach (var moduleType in moduleTypes)
        {
            var candidate = (ISharpClawCoreModule)Activator.CreateInstance(moduleType)!;
            if (string.Equals(candidate.Id, manifest.Id, StringComparison.Ordinal))
                return candidate;
        }

        throw new InvalidOperationException(
            $"No ISharpClawCoreModule implementation in '{Path.GetFileName(dllPath)}' " +
            $"declares module id '{manifest.Id}'. Add moduleType to module.json when an assembly contains multiple modules.");
    }
}
