using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Infrastructure.Persistence;
// Infrastructure-facing: ModuleJsonPersistenceService coordinates module persistence
// registration for JSON/InMemory mode.  This import does not introduce cold-entity
// query coupling and is acceptable per the cold-storage-ef-query-integration-plan.
using SharpClaw.Infrastructure.Persistence.JSON;
using SharpClaw.Infrastructure.Persistence.Modules;
using SharpClaw.Utils.Security;

namespace SharpClaw.Application.Services;

/// <summary>
/// Manages module lifecycle: listing, enabling, and disabling bundled modules,
/// and loading / unloading / reloading external (hot-loaded) modules.
/// Updates the database to track module state.
/// </summary>
public sealed class ModuleService(
    SharpClawDbContext db,
    ModuleLoader loader,
    ModuleRegistry registry,
    RuntimeModuleDbContextRegistry moduleDbContextRegistry,
    ModulePersistenceRegistrationFactory modulePersistenceRegistrationFactory,
    ModuleDbContextOptions moduleDbContextOptions,
    ModuleJsonPersistenceService? moduleJsonPersistence,
    ModuleEventDispatcher eventDispatcher,
    ILoggerFactory loggerFactory,
    ILogger<ModuleService> logger,
    ChatCache chatCache,
    IConfiguration? configuration = null)
{
    // ═══════════════════════════════════════════════════════════════
    // Queries
    // ═══════════════════════════════════════════════════════════════

    /// <summary>List all modules (bundled + external) with their current state.</summary>
    public async Task<IReadOnlyList<ModuleStateResponse>> ListAsync(CancellationToken ct = default)
    {
        var states = await db.ModuleStates
            .ToDictionaryAsync(s => s.ModuleId, StringComparer.Ordinal, ct);

        var result = new List<ModuleStateResponse>();
        foreach (var module in loader.GetAllBundled())
        {
            states.TryGetValue(module.Id, out var state);
            var manifest = loader.GetManifest(module.Id);
            result.Add(ToResponse(module, state, manifest));
        }

        // External (hot-loaded) modules — always enabled while loaded.
        foreach (var module in registry.GetAllModules())
        {
            if (result.Any(r => r.ModuleId == module.Id)) continue;
            var manifest = registry.GetManifest(module.Id);
            result.Add(ToResponse(module, state: null, manifest, isExternal: true));
        }

        return result.OrderBy(r => r.DisplayName).ToList();
    }

    /// <summary>Get state for a single module (bundled or external).</summary>
    public async Task<ModuleStateResponse?> GetStateAsync(string moduleId, CancellationToken ct = default)
    {
        var module = loader.GetBundledModule(moduleId);
        if (module is not null)
        {
            var state = await db.ModuleStates.FirstOrDefaultAsync(s => s.ModuleId == moduleId, ct);
            var manifest = loader.GetManifest(moduleId);
            return ToResponse(module, state, manifest);
        }

        // External (hot-loaded) module — not in DB, always "enabled" while loaded.
        var ext = registry.GetModule(moduleId);
        if (ext is not null)
        {
            var manifest = registry.GetManifest(moduleId);
            return ToResponse(ext, state: null, manifest, isExternal: true);
        }

        return null;
    }

    /// <summary>Get enriched detail for a single module, including manifest and tool/contract info.</summary>
    public async Task<ModuleDetailResponse?> GetDetailAsync(string moduleId, CancellationToken ct = default)
    {
        ISharpClawModule? module = loader.GetBundledModule(moduleId);
        ModuleStateDB? state = null;
        ModuleManifest? manifest;
        bool isExternal = false;

        if (module is not null)
        {
            state = await db.ModuleStates.FirstOrDefaultAsync(s => s.ModuleId == moduleId, ct);
            manifest = loader.GetManifest(moduleId);
        }
        else
        {
            module = registry.GetModule(moduleId);
            if (module is null) return null;
            manifest = registry.GetManifest(moduleId);
            isExternal = true;
        }

        var enabled = isExternal || (state?.Enabled ?? false);
        var toolCount = module.GetToolDefinitions().Count;
        var inlineToolCount = module.GetInlineToolDefinitions().Count;
        var exportedContracts = module.ExportedContracts.Select(e => e.ContractName).ToArray();
        var requiredContracts = module.RequiredContracts.Select(r => r.ContractName).ToArray();

        var allSatisfied = !module.RequiredContracts
            .Where(r => !r.Optional)
            .Any(r => registry.ResolveContract(r.ContractName) is null);

        return new ModuleDetailResponse(
            ModuleId: module.Id,
            DisplayName: module.DisplayName,
            ToolPrefix: module.ToolPrefix,
            Enabled: enabled,
            Version: manifest?.Version ?? state?.Version,
            Registered: isExternal || state is not null,
            IsExternal: isExternal,
            CreatedAt: state?.CreatedAt,
            UpdatedAt: state?.UpdatedAt,
            Author: manifest?.Author,
            Description: manifest?.Description,
            License: manifest?.License,
            Platforms: manifest?.Platforms,
            ExecutionTimeoutSeconds: manifest?.ExecutionTimeoutSeconds ?? 60,
            ToolCount: toolCount,
            InlineToolCount: inlineToolCount,
            ExportedContracts: exportedContracts,
            RequiredContracts: requiredContracts,
            AllRequirementsSatisfied: allSatisfied);
    }

    // ═══════════════════════════════════════════════════════════════
    // Enable / Disable
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Enable a module. Registers it with the <see cref="ModuleRegistry"/>,
    /// runs initialization, and updates DB.
    /// </summary>
    public async Task<ModuleStateResponse> EnableAsync(
        string moduleId, IServiceProvider rootServices, CancellationToken ct = default)
    {
        var module = loader.GetBundledModule(moduleId)
            ?? throw new ArgumentException($"Unknown module: {moduleId}");

        // Update DB
        var state = await db.ModuleStates.FirstOrDefaultAsync(s => s.ModuleId == moduleId, ct);
        var manifest = loader.GetManifest(moduleId);

        if (state is null)
        {
            state = new ModuleStateDB
            {
                ModuleId = moduleId,
                Enabled = true,
                Version = manifest?.Version
            };
            db.ModuleStates.Add(state);
        }
        else
        {
            state.Enabled = true;
            state.Version = manifest?.Version;
        }
        await db.SaveChangesAsync(ct);

        // Register + initialize if not already active
        if (registry.GetModule(moduleId) is null)
        {
            try
            {
                registry.Register(module);
                RegisterModulePersistence(module);
                await LoadModulePersistenceAsync(module, ct);

                if (manifest is not null)
                    registry.CacheManifest(moduleId, manifest);

                // Check unsatisfied dependencies before init
                var unsatisfied = registry.GetUnsatisfiedRequirements(moduleId);
                if (unsatisfied.Count > 0)
                {
                    var names = string.Join(", ", unsatisfied.Select(r => r.ContractName));
                    moduleDbContextRegistry.UnregisterModule(moduleId);
                    registry.Unregister(moduleId);
                    throw new InvalidOperationException(
                        $"Module '{moduleId}' has unsatisfied contract dependencies: {names}");
                }

                await module.InitializeAsync(rootServices, ct);
            }
            catch
            {
                // Rollback: unregister if init failed
                moduleDbContextRegistry.UnregisterModule(moduleId);
                registry.Unregister(moduleId);
                throw;
            }

            // Reconcile: backfill wildcard grants for newly registered
            // resource types and global flags into existing permission sets.
            await ReconcilePermissionsForModuleAsync(module, ct);
        }

        InvalidateModuleRuntimeState();
        eventDispatcher.InvalidateSinkCache();
        eventDispatcher.Dispatch(new SharpClawEvent(
            SharpClawEventType.ModuleEnabled,
            DateTimeOffset.UtcNow,
            SourceId: moduleId,
            Summary: $"Module '{moduleId}' enabled"));

        return ToResponse(module, state, manifest);
    }

    /// <summary>
    /// Disable a module. Shuts it down, unregisters from <see cref="ModuleRegistry"/>,
    /// and updates DB.
    /// </summary>
    public async Task<ModuleStateResponse> DisableAsync(string moduleId, CancellationToken ct = default)
    {
        var module = loader.GetBundledModule(moduleId)
            ?? throw new ArgumentException($"Unknown module: {moduleId}");

        // Check that no other enabled module depends on this module's contracts
        var exportedNames = module.ExportedContracts.Select(e => e.ContractName).ToHashSet(StringComparer.Ordinal);
        if (exportedNames.Count > 0)
        {
            foreach (var other in registry.GetAllModules())
            {
                if (other.Id == moduleId) continue;
                var deps = other.RequiredContracts
                    .Where(r => !r.Optional && exportedNames.Contains(r.ContractName))
                    .Select(r => r.ContractName)
                    .ToList();
                if (deps.Count > 0)
                    throw new InvalidOperationException(
                        $"Cannot disable '{moduleId}': module '{other.Id}' depends on contract(s) {string.Join(", ", deps)}.");
            }
        }

        // Shutdown + unregister
        if (registry.GetModule(moduleId) is not null)
        {
            try { await module.ShutdownAsync(); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Module '{ModuleId}' shutdown error during disable", moduleId);
            }
            moduleDbContextRegistry.UnregisterModule(moduleId);
            registry.Unregister(moduleId);
        }

        // Update DB
        var state = await db.ModuleStates.FirstOrDefaultAsync(s => s.ModuleId == moduleId, ct);
        var manifest = loader.GetManifest(moduleId);

        if (state is null)
        {
            state = new ModuleStateDB
            {
                ModuleId = moduleId,
                Enabled = false,
                Version = manifest?.Version
            };
            db.ModuleStates.Add(state);
        }
        else
        {
            state.Enabled = false;
        }
        await db.SaveChangesAsync(ct);

        InvalidateModuleRuntimeState();
        eventDispatcher.InvalidateSinkCache();
        eventDispatcher.Dispatch(new SharpClawEvent(
            SharpClawEventType.ModuleDisabled,
            DateTimeOffset.UtcNow,
            SourceId: moduleId,
            Summary: $"Module '{moduleId}' disabled"));

        return ToResponse(module, state, manifest);
    }

    // ═══════════════════════════════════════════════════════════════
    // External module lifecycle (hot-load / unload / reload)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Load an external module from a directory containing <c>module.json</c>
    /// and its entry assembly. Registers it with the <see cref="ModuleRegistry"/>.
    /// </summary>
    public async Task<ModuleStateResponse> LoadExternalAsync(
        string moduleDir, IServiceProvider hostServices, CancellationToken ct = default)
    {
        // Validate that moduleDir resolves strictly inside the external-modules root.
        var externalRoot = ResolveExternalModulesDir();
        var combinedModuleDir = Path.Combine(externalRoot, moduleDir);
        var canonicalModuleDir = PathGuard.EnsureContainedIn(combinedModuleDir, externalRoot);

        var manifestPath = PathGuard.EnsureContainedIn(
            Path.Combine(canonicalModuleDir, ModuleFileNames.ManifestFile), canonicalModuleDir);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"No {ModuleFileNames.ManifestFile} found in '{canonicalModuleDir}'.", manifestPath);

        var json = await File.ReadAllTextAsync(manifestPath, ct);
        var manifest = System.Text.Json.JsonSerializer.Deserialize<ModuleManifest>(json, SecureJsonOptions.Manifest)
            ?? throw new InvalidOperationException($"Failed to parse manifest in '{canonicalModuleDir}'.");
        var runtimeInfo = ModuleManifestRuntimeInfo.FromJson(json);

        if (registry.GetModule(manifest.Id) is not null)
            throw new InvalidOperationException($"Module '{manifest.Id}' is already loaded.");

        var host = ExternalModuleHost.Load(canonicalModuleDir, manifest, runtimeInfo, hostServices, loggerFactory);
        try
        {
            registry.Register(host.Module, host);
            RegisterModulePersistence(host.Module);
            registry.CacheManifest(manifest.Id, manifest);
            await LoadModulePersistenceAsync(host.Module, ct);
            await host.Module.InitializeAsync(host.Services, ct);

            // Reconcile: backfill wildcard grants for newly registered
            // resource types and global flags into existing permission sets.
            // Mirrors EnableAsync. SeedingService cannot do this for external
            // modules because it runs at StartingAsync, before module load.
            await ReconcilePermissionsForModuleAsync(host.Module, ct);

            logger.LogInformation("External module '{ModuleId}' loaded from {Dir}",
                PathGuard.SanitizeForLog(manifest.Id), PathGuard.SanitizeForLog(canonicalModuleDir));
            InvalidateModuleRuntimeState();
            return ToResponse(host.Module, state: null, manifest, isExternal: true);
        }
        catch
        {
            registry.Unregister(manifest.Id);
            moduleDbContextRegistry.UnregisterModule(manifest.Id);
            await host.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Drain in-flight executions, shut down, unregister, and unload an external module.
    /// </summary>
    public async Task UnloadExternalAsync(string moduleId, CancellationToken ct = default)
    {
        var host = registry.GetExternalHost(moduleId)
            ?? throw new ArgumentException($"Module '{moduleId}' is not an external module.");

        await host.DrainAsync(TimeSpan.FromSeconds(30), ct);
        await host.Module.ShutdownAsync();
        moduleDbContextRegistry.UnregisterModule(moduleId);
        registry.Unregister(moduleId);
        await host.DisposeAsync();
        InvalidateModuleRuntimeState();

        var (attempts, delay) = ResolveUnloadVerifyOptions();
        var unloaded = await host.VerifyUnloadedAsync(attempts, delay, ct);
        logger.LogInformation(
            "External module '{ModuleId}' unloaded (GC verified: {Verified})", moduleId, unloaded);
    }

    private (int Attempts, TimeSpan Delay) ResolveUnloadVerifyOptions()
    {
        var attempts = configuration?.GetValue("Modules:UnloadVerifyMaxAttempts", 10) ?? 10;
        var delayMs = configuration?.GetValue("Modules:UnloadVerifyDelayMs", 100) ?? 100;
        if (attempts < 1) attempts = 1;
        if (delayMs < 0) delayMs = 0;
        return (attempts, TimeSpan.FromMilliseconds(delayMs));
    }

    /// <summary>Unload then re-load an external module from its original directory.</summary>
    public async Task<ModuleStateResponse> ReloadExternalAsync(
        string moduleId, IServiceProvider hostServices, CancellationToken ct = default)
    {
        var host = registry.GetExternalHost(moduleId)
            ?? throw new ArgumentException($"Module '{moduleId}' is not an external module.");

        var dir = host.SourceDirectory;
        await UnloadExternalAsync(moduleId, ct);
        return await LoadExternalFromAbsolutePathAsync(
            dir,
            hostServices,
            ct,
            persistDisabledEnvEntry: false);
    }

    /// <summary>
    /// Scan the <c>external-modules/</c> directory for new module directories
    /// and load any that are not already registered.
    /// </summary>
    public async Task<IReadOnlyList<ModuleStateResponse>> ScanExternalModulesAsync(
        IServiceProvider hostServices, CancellationToken ct = default)
    {
        var dir = ResolveExternalModulesDir();
        if (!Directory.Exists(dir)) return [];

        var loaded = new List<ModuleStateResponse>();
        foreach (var subDir in Directory.EnumerateDirectories(dir))
        {
            // Validate that the enumerated subdirectory is actually inside the
            // external-modules root (guards against symlink / junction escapes).
            var canonicalSubDir = PathGuard.EnsureContainedIn(subDir, dir);
            var manifestPath = Path.Combine(canonicalSubDir, ModuleFileNames.ManifestFile);
            if (!File.Exists(manifestPath)) continue;

            try
            {
                var json = await File.ReadAllTextAsync(manifestPath, ct);
                var manifest = System.Text.Json.JsonSerializer.Deserialize<ModuleManifest>(json, SecureJsonOptions.Manifest);
                if (manifest is null || registry.GetModule(manifest.Id) is not null)
                    continue;

                var result = await LoadExternalAsync(canonicalSubDir, hostServices, ct);
                loaded.Add(result);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load external module from {Dir}", subDir);
            }
        }

        return loaded;
    }

    /// <summary>
    /// Load an external module from an absolute path on disk (not confined to the
    /// <c>external-modules/</c> directory). Used for .env-configured external modules.
    /// </summary>
    public async Task<ModuleStateResponse> LoadExternalFromAbsolutePathAsync(
        string absoluteDir,
        IServiceProvider hostServices,
        CancellationToken ct = default,
        bool persistDisabledEnvEntry = true)
    {
        var canonicalDir = Path.GetFullPath(absoluteDir);
        if (persistDisabledEnvEntry)
            AddExternalModuleToEnv(canonicalDir);

        if (!Directory.Exists(canonicalDir))
            throw new DirectoryNotFoundException($"External module directory not found: '{canonicalDir}'.");

        var manifestPath = Path.Combine(canonicalDir, ModuleFileNames.ManifestFile);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"No {ModuleFileNames.ManifestFile} found in '{canonicalDir}'.", manifestPath);

        var json = await File.ReadAllTextAsync(manifestPath, ct);
        var manifest = System.Text.Json.JsonSerializer.Deserialize<ModuleManifest>(json, SecureJsonOptions.Manifest)
            ?? throw new InvalidOperationException($"Failed to parse manifest in '{canonicalDir}'.");
        var runtimeInfo = ModuleManifestRuntimeInfo.FromJson(json);

        if (registry.GetModule(manifest.Id) is { } existingModule)
        {
            var existingHost = registry.GetExternalHost(manifest.Id);
            if (existingHost is not null
                && string.Equals(
                    Path.GetFullPath(existingHost.SourceDirectory),
                    canonicalDir,
                    StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "External module '{ModuleId}' from absolute path {Dir} is already loaded - skipping duplicate .env entry",
                    PathGuard.SanitizeForLog(manifest.Id), PathGuard.SanitizeForLog(canonicalDir));
                return ToResponse(existingModule, state: null, manifest, isExternal: true);
            }

            throw new InvalidOperationException($"Module '{manifest.Id}' is already loaded.");
        }

        var host = ExternalModuleHost.Load(canonicalDir, manifest, runtimeInfo, hostServices, loggerFactory);
        try
        {
            registry.Register(host.Module, host);
            RegisterModulePersistence(host.Module);
            registry.CacheManifest(manifest.Id, manifest);
            await LoadModulePersistenceAsync(host.Module, ct);
            await host.Module.InitializeAsync(host.Services, ct);

            // Reconcile: backfill wildcard grants for newly registered
            // resource types and global flags into existing permission sets.
            // Mirrors EnableAsync. SeedingService cannot do this for external
            // modules because it runs at StartingAsync, before module load.
            await ReconcilePermissionsForModuleAsync(host.Module, ct);

            logger.LogInformation("External module '{ModuleId}' loaded from absolute path {Dir}",
                PathGuard.SanitizeForLog(manifest.Id), PathGuard.SanitizeForLog(canonicalDir));

            InvalidateModuleRuntimeState();
            return ToResponse(host.Module, state: null, manifest, isExternal: true);
        }
        catch
        {
            registry.Unregister(manifest.Id);
            moduleDbContextRegistry.UnregisterModule(manifest.Id);
            await host.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Restore a NuGet package that contains a complete SharpClaw module payload,
    /// materialize it into the local package-module cache, and load it through
    /// the same external-module lifecycle as directory-backed modules.
    /// </summary>
    public async Task<ModuleStateResponse> LoadExternalPackageAsync(
        NuGetModulePackageReference package,
        IServiceProvider hostServices,
        CancellationToken ct = default)
    {
        var moduleDir = await NuGetModulePackageResolver.ResolveAsync(
            package,
            ResolveNuGetModulesDir(),
            ct);

        return await LoadExternalFromAbsolutePathAsync(
            moduleDir,
            hostServices,
            ct,
            persistDisabledEnvEntry: false);
    }

    public void RegisterModulePersistence(ISharpClawModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        var registrations = modulePersistenceRegistrationFactory.CreateRegistrations(
            module.Id,
            module.GetType().Assembly);

        foreach (var registration in registrations)
            moduleDbContextRegistry.Register(registration);
    }

    public async Task LoadModulePersistenceAsync(ISharpClawModule module, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(module);

        if (moduleDbContextOptions.StorageMode != StorageMode.JsonFile || moduleJsonPersistence is null)
            return;

        foreach (var registration in moduleDbContextRegistry.GetAll()
                     .Where(r => string.Equals(r.ModuleId, module.Id, StringComparison.Ordinal)))
        {
            await moduleJsonPersistence.LoadModuleAsync(registration, ct);
        }
    }

    private void InvalidateModuleRuntimeState()
    {
        chatCache.RemoveByPrefix(ChatCache.PrefixHeaderUser);
        chatCache.RemoveByPrefix(ChatCache.PrefixHeaderAgentSuffix);
        chatCache.RemoveByPrefix(ChatCache.PrefixEffectiveTools);
    }

    /// <summary>
    /// Load external modules defined in the <c>ExternalModules</c> configuration section.
    /// Each entry may use either a <c>Path</c> or a NuGet <c>PackageId</c> plus
    /// <c>Version</c>; optional <c>Enabled</c> defaults to <c>true</c>.
    /// Failed enabled entries crash startup by default; set
    /// <c>Modules:CrashOnExternalModuleLoadFailure</c> to <c>false</c> to keep warning-only behavior.
    /// </summary>
    public async Task<IReadOnlyList<ModuleStateResponse>> LoadExternalModulesFromConfigAsync(
        IConfiguration config, IServiceProvider hostServices, CancellationToken ct = default)
    {
        var section = config.GetSection("ExternalModules");
        if (!section.Exists()) return [];

        var crashOnFailure = config.GetValue("Modules:CrashOnExternalModuleLoadFailure", defaultValue: true);
        var loaded = new List<ModuleStateResponse>();
        foreach (var entry in section.GetChildren())
        {
            var path = entry["Path"];
            var packageId = entry["PackageId"];
            var version = entry["Version"];
            var entryLabel = !string.IsNullOrWhiteSpace(path)
                ? path
                : !string.IsNullOrWhiteSpace(packageId)
                    ? $"{packageId}/{version ?? "(missing-version)"}"
                    : $"index {entry.Key}";
            if (string.IsNullOrWhiteSpace(path)
                && string.IsNullOrWhiteSpace(packageId)
                && string.IsNullOrWhiteSpace(version))
            {
                logger.LogWarning(
                    "ExternalModules entry at index {Index} has neither Path nor PackageId/Version - skipped",
                    entry.Key);

                continue;
            }

            var enabled = true;
            if (entry["Enabled"] is { } enabledStr && bool.TryParse(enabledStr, out var e))
                enabled = e;

            if (!enabled)
            {
                logger.LogInformation("ExternalModules entry '{Entry}' is disabled - skipped",
                    PathGuard.SanitizeForLog(entryLabel));
                continue;
            }

            try
            {
                ModuleStateResponse result;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    result = await LoadExternalFromAbsolutePathAsync(
                        path,
                        hostServices,
                        ct,
                        persistDisabledEnvEntry: false);
                }
                else if (!string.IsNullOrWhiteSpace(packageId)
                         || !string.IsNullOrWhiteSpace(version))
                {
                    if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(version))
                    {
                        throw new InvalidOperationException(
                            "NuGet-backed ExternalModules entries require both PackageId and Version.");
                    }

                    result = await LoadExternalPackageAsync(
                        new NuGetModulePackageReference(
                            packageId,
                            version,
                            entry["Source"],
                            entry["ModulePath"]),
                        hostServices,
                        ct);
                }
                else
                {
                    logger.LogWarning(
                        "ExternalModules entry at index {Index} has neither Path nor PackageId/Version - skipped",
                        entry.Key);
                    continue;
                }

                loaded.Add(result);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load .env external module from '{Entry}'",
                    PathGuard.SanitizeForLog(entryLabel));

                if (crashOnFailure)
                {
                    throw new InvalidOperationException(
                        $"Failed to load enabled external module configured at ExternalModules:{entry.Key}. " +
                        "Fix the module path or package reference, or set ExternalModules entry Enabled=false. " +
                        "Set Modules:CrashOnExternalModuleLoadFailure=false to allow startup to continue.",
                        ex);
                }
            }
        }

        return loaded;
    }

    public static string ResolveExternalModulesDir()
    {
        return Path.Combine(
            Path.GetDirectoryName(typeof(ModuleService).Assembly.Location)!,
            ModuleFileNames.ExternalModulesDir);
    }

    public static string ResolveNuGetModulesDir()
    {
        return Path.Combine(
            Path.GetDirectoryName(typeof(ModuleService).Assembly.Location)!,
            ModuleFileNames.NuGetModulesDir);
    }

    /// <summary>
    /// Appends an external module path to the <c>ExternalModules</c> array in the
    /// Core <c>.env</c> file so it persists across restarts. Skips if the path is
    /// already present. Uses direct file I/O (no auth check) because this is an
    /// internal server-side operation triggered by module loading.
    /// </summary>
    private void AddExternalModuleToEnv(string absoluteDir)
    {
        try
        {
            var envPath = ResolveEnvFilePath();
            if (!File.Exists(envPath)) return;

            var content = File.ReadAllText(envPath);
            var canonical = Path.GetFullPath(absoluteDir);

            // Duplicate detection — parse the .env as JSON-with-comments and
            // walk the typed ExternalModules array. This survives reformatting
            // and comment changes that the legacy raw-text Contains check
            // would have missed (audit section 3.2).
            if (IsExternalModuleAlreadyRegistered(content, canonical))
                return;

            var normalised = canonical.Replace("\\", "\\\\");
            var entry = $"    {{ \"Path\": \"{normalised}\", \"Enabled\": false }}";

            // Case 1: ExternalModules array already exists (commented-out or active).
            //   – Active array: insert before the closing ']'.
            //   – Commented-out array: uncomment it and insert the entry.
            var activeArrayIdx = content.IndexOf(
                ModuleFileNames.ExternalModulesArrayHeader, StringComparison.Ordinal);
            if (activeArrayIdx >= 0)
            {
                // Find the matching ']'.
                var closeIdx = content.IndexOf(']', activeArrayIdx);
                if (closeIdx < 0) return;

                // Check if there's already at least one entry (non-empty array).
                var slice = content[activeArrayIdx..closeIdx];
                var hasEntries = slice.Contains('{');
                var insertion = hasEntries
                    ? $",\n{entry}"
                    : $"\n{entry}\n  ";

                content = string.Concat(content.AsSpan(0, closeIdx), insertion, content.AsSpan(closeIdx));
            }
            else
            {
                // Case 2: No ExternalModules section at all — insert before "Modules".
                var modulesIdx = content.IndexOf(
                    ModuleFileNames.ModulesObjectKey, StringComparison.Ordinal);
                var commentIdx = modulesIdx >= 0
                    ? content.LastIndexOf(
                        ModuleFileNames.ModulesSectionComment, modulesIdx, StringComparison.Ordinal)
                    : -1;

                var insertionPoint = commentIdx >= 0
                    ? commentIdx
                    : content.LastIndexOf('}');

                if (insertionPoint < 0) return;

                var block = $"\"ExternalModules\": [\n{entry}\n  ],\n\n  ";
                content = string.Concat(content.AsSpan(0, insertionPoint), block, content.AsSpan(insertionPoint));
            }

            File.WriteAllText(envPath, content);
            logger.LogInformation("Added external module path to .env: {Path}",
                PathGuard.SanitizeForLog(absoluteDir));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist external module path to .env");
        }
    }

    /// <summary>
    /// Parses the <c>.env</c> JSON-with-comments and returns <c>true</c> when
    /// the canonical path already appears as an entry in the
    /// <c>ExternalModules</c> array. Returns <c>false</c> on parse failure so
    /// the caller falls back to text-based mutation.
    /// </summary>
    private static bool IsExternalModuleAlreadyRegistered(string content, string canonicalPath)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(content,
                new System.Text.Json.JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                });

            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("ExternalModules", out var arr)
                || arr.ValueKind != System.Text.Json.JsonValueKind.Array) return false;

            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if (!item.TryGetProperty("Path", out var pathProp)) continue;
                if (pathProp.ValueKind != System.Text.Json.JsonValueKind.String) continue;

                var existing = pathProp.GetString();
                if (string.IsNullOrEmpty(existing)) continue;

                if (string.Equals(
                        Path.GetFullPath(existing),
                        canonicalPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Malformed .env or unexpected shape — let the caller try the
            // text-splice path. The duplicate check is best-effort.
        }

        return false;
    }

    private static string ResolveEnvFilePath()
    {
        return Path.Combine(
            Path.GetDirectoryName(typeof(ModuleService).Assembly.Location)!,
            ModuleFileNames.EnvironmentDir, ModuleFileNames.EnvFile);
    }

    // ═══════════════════════════════════════════════════════════════
    // Startup helpers (called from Program.cs via a scope)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Synchronise DB state from the current <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>.
    /// Called once at startup. The <c>.env</c> configuration is the authoritative
    /// source; DB records are created or updated to match.
    /// Returns the set of module IDs that should be enabled.
    /// </summary>
    public async Task<HashSet<string>> SyncStateFromConfigAsync(
        Microsoft.Extensions.Configuration.IConfiguration config, CancellationToken ct = default)
    {
        var enabledSet = new HashSet<string>(StringComparer.Ordinal);

        foreach (var module in loader.GetAllBundled())
        {
            var enabled = ModuleLoader.IsEnabledInConfig(module.Id, config);
            var manifest = loader.GetManifest(module.Id);

            var state = await db.ModuleStates.FirstOrDefaultAsync(
                s => s.ModuleId == module.Id, ct);

            if (state is null)
            {
                state = new ModuleStateDB
                {
                    ModuleId = module.Id,
                    Enabled = enabled,
                    Version = manifest?.Version
                };
                db.ModuleStates.Add(state);
            }
            else
            {
                state.Enabled = enabled;
                state.Version = manifest?.Version;
            }

            if (enabled)
                enabledSet.Add(module.Id);
        }

        await db.SaveChangesAsync(ct);
        return enabledSet;
    }

    // ═══════════════════════════════════════════════════════════════
    // Permission reconciliation
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Backfills wildcard resource grants and global flags introduced by a
    /// newly enabled module into every existing permission set that already
    /// uses the <see cref="WellKnownIds.AllResources"/> wildcard for at least
    /// one resource type. This is the runtime counterpart of
    /// <c>SeedingService.ReconcileAdminPermissionsAsync</c> and ensures that
    /// roles created before the module was enabled automatically gain access
    /// to its resource types and global flags.
    /// </summary>
    private async Task ReconcilePermissionsForModuleAsync(
        ISharpClawModule module, CancellationToken ct)
    {
        var newResourceTypes = module.GetResourceTypeDescriptors()
            .Select(d => d.ResourceType)
            .ToList();
        var newFlagKeys = module.GetGlobalFlagDescriptors()
            .Select(d => d.FlagKey)
            .ToList();

        if (newResourceTypes.Count == 0 && newFlagKeys.Count == 0)
            return;

        // Find permission sets that have at least one wildcard grant — these are
        // the "broad access" sets (typically admin roles) that should be reconciled.
        var permissionSets = await db.PermissionSets
            .Include(p => p.ResourceAccesses)
            .Include(p => p.GlobalFlags)
            .AsSplitQuery()
            .Where(p => p.ResourceAccesses.Any(a => a.ResourceId == WellKnownIds.AllResources))
            .ToListAsync(ct);

        if (permissionSets.Count == 0)
            return;

        var changed = false;

        foreach (var ps in permissionSets)
        {
            foreach (var rt in newResourceTypes)
            {
                if (!ps.ResourceAccesses.Any(a =>
                        a.ResourceType == rt && a.ResourceId == WellKnownIds.AllResources))
                {
                    // Clearance MUST be Independent — Unset (the DB default)
                    // is treated as "grant is inert, deny" by
                    // EvaluateResourceAccessAsync. See the matching guard in
                    // SeedingService.CreateAdminPermissions.
                    ps.ResourceAccesses.Add(new ResourceAccessDB
                    {
                        PermissionSetId = ps.Id,
                        ResourceType = rt,
                        ResourceId = WellKnownIds.AllResources,
                        Clearance = PermissionClearance.Independent,
                    });
                    changed = true;
                }
            }

            foreach (var flagKey in newFlagKeys)
            {
                if (!ps.GlobalFlags.Any(f => f.FlagKey == flagKey))
                {
                    ps.GlobalFlags.Add(new GlobalFlagDB
                    {
                        PermissionSetId = ps.Id,
                        FlagKey = flagKey,
                        Clearance = PermissionClearance.Independent,
                    });
                    changed = true;
                }
            }
        }

        if (changed)
        {
            logger.LogInformation(
                "Reconciled permissions for module '{ModuleId}' — backfilled grants into {Count} permission set(s).",
                module.Id, permissionSets.Count);
            await db.SaveChangesAsync(ct);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Mapping
    // ═══════════════════════════════════════════════════════════════

    private static ModuleStateResponse ToResponse(
        ISharpClawModule module, ModuleStateDB? state, ModuleManifest? manifest,
        bool isExternal = false)
    {
        return new ModuleStateResponse(
            ModuleId: module.Id,
            DisplayName: module.DisplayName,
            ToolPrefix: module.ToolPrefix,
            Enabled: isExternal || (state?.Enabled ?? false),
            Version: manifest?.Version ?? state?.Version,
            Registered: isExternal || state is not null,
            IsExternal: isExternal,
            CreatedAt: state?.CreatedAt,
            UpdatedAt: state?.UpdatedAt);
    }
}

/// <summary>Module state as returned by the API.</summary>
public sealed record ModuleStateResponse(
    string ModuleId,
    string DisplayName,
    string ToolPrefix,
    bool Enabled,
    string? Version,
    bool Registered,
    bool IsExternal,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt);

/// <summary>Extended module detail as returned by <c>GET /modules/{id}</c>.</summary>
public sealed record ModuleDetailResponse(
    string ModuleId,
    string DisplayName,
    string ToolPrefix,
    bool Enabled,
    string? Version,
    bool Registered,
    bool IsExternal,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    string? Author,
    string? Description,
    string? License,
    string[]? Platforms,
    int ExecutionTimeoutSeconds,
    int ToolCount,
    int InlineToolCount,
    string[] ExportedContracts,
    string[] RequiredContracts,
    bool AllRequirementsSatisfied);
