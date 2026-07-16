namespace SharpClaw.Runtime.BLL.Modules;

/// <summary>
/// Centralised filesystem names used by the module subsystem. These names are
/// de-facto policy and are referenced from multiple services; collecting them
/// here prevents the individual literals from drifting.
/// </summary>
internal static class ModuleFileNames
{
    /// <summary>Per-module manifest file name.</summary>
    public const string ManifestFile = "module.json";

    /// <summary>Bundled-modules subdirectory (next to the host assembly).</summary>
    public const string BundledModulesDir = "modules";

    /// <summary>External (hot-loaded) modules subdirectory.</summary>
    public const string ExternalModulesDir = "external-modules";

    /// <summary>Resolved NuGet package modules subdirectory.</summary>
    public const string NuGetModulesDir = "nuget-modules";

}
