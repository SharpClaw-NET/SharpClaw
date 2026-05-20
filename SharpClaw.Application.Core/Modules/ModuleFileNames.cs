namespace SharpClaw.Application.Core.Modules;

/// <summary>
/// Centralised filesystem names used by the module subsystem and the Core
/// <c>.env</c> file resolver. These names are de-facto policy and are
/// referenced from multiple services; collecting them here prevents the
/// individual literals from drifting (audit section 3.1).
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

    /// <summary>Environment subdirectory containing the Core <c>.env</c> file.</summary>
    public const string EnvironmentDir = "Environment";

    /// <summary>Core environment file name.</summary>
    public const string EnvFile = ".env";

    // ── .env section markers used by ModuleService.AddExternalModuleToEnv ──
    // The .env template is JSON-with-comments. The writer needs to find the
    // ExternalModules array (active or commented-out) and, failing that,
    // splice a new section just before the Modules block. Centralising the
    // markers means the writer cannot drift from the template.

    /// <summary>JSON key that introduces the active <c>ExternalModules</c> array.</summary>
    public const string ExternalModulesArrayHeader = "\"ExternalModules\": [";

    /// <summary>JSON key that introduces the <c>Modules</c> object.</summary>
    public const string ModulesObjectKey = "\"Modules\"";

    /// <summary>Comment header above the <c>Modules</c> section in the template.</summary>
    public const string ModulesSectionComment = "// ── Modules";
}
