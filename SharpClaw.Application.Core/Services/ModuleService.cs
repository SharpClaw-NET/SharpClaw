using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Infrastructure.Models;
using SharpClaw.Contracts.Modules;
using SharpClaw.Infrastructure.Persistence;
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
    ModuleEventDispatcher eventDispatcher,
    ILoggerFactory loggerFactory,
    ILogger<ModuleService> logger)
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

                if (manifest is not null)
                    registry.CacheManifest(moduleId, manifest);

                // Check unsatisfied dependencies before init
                var unsatisfied = registry.GetUnsatisfiedRequirements(moduleId);
                if (unsatisfied.Count > 0)
                {
                    var names = string.Join(", ", unsatisfied.Select(r => r.ContractName));
                    registry.Unregister(moduleId);
                    throw new InvalidOperationException(
                        $"Module '{moduleId}' has unsatisfied contract dependencies: {names}");
                }

                await module.InitializeAsync(rootServices, ct);
            }
            catch
            {
                // Rollback: unregister if init failed
                registry.Unregister(moduleId);
                throw;
            }
        }

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
            Path.Combine(canonicalModuleDir, "module.json"), canonicalModuleDir);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"No module.json found in '{canonicalModuleDir}'.", manifestPath);

        var json = await File.ReadAllTextAsync(manifestPath, ct);
        var manifest = System.Text.Json.JsonSerializer.Deserialize<ModuleManifest>(json, SecureJsonOptions.Manifest)
            ?? throw new InvalidOperationException($"Failed to parse manifest in '{canonicalModuleDir}'.");

        if (registry.GetModule(manifest.Id) is not null)
            throw new InvalidOperationException($"Module '{manifest.Id}' is already loaded.");

        var host = ExternalModuleHost.Load(canonicalModuleDir, manifest, hostServices, loggerFactory);
        try
        {
            registry.Register(host.Module, host);
            registry.CacheManifest(manifest.Id, manifest);
            await host.Module.InitializeAsync(host.Services, ct);

            logger.LogInformation("External module '{ModuleId}' loaded from {Dir}",
                PathGuard.SanitizeForLog(manifest.Id), PathGuard.SanitizeForLog(canonicalModuleDir));
            return ToResponse(host.Module, state: null, manifest, isExternal: true);
        }
        catch
        {
            registry.Unregister(manifest.Id);
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
        registry.Unregister(moduleId);
        await host.DisposeAsync();

        var unloaded = host.VerifyUnloaded();
        logger.LogInformation(
            "External module '{ModuleId}' unloaded (GC verified: {Verified})", moduleId, unloaded);
    }

    /// <summary>Unload then re-load an external module from its original directory.</summary>
    public async Task<ModuleStateResponse> ReloadExternalAsync(
        string moduleId, IServiceProvider hostServices, CancellationToken ct = default)
    {
        var host = registry.GetExternalHost(moduleId)
            ?? throw new ArgumentException($"Module '{moduleId}' is not an external module.");

        var dir = host.SourceDirectory;
        await UnloadExternalAsync(moduleId, ct);
        return await LoadExternalAsync(dir, hostServices, ct);
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
            var manifestPath = Path.Combine(canonicalSubDir, "module.json");
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

    public static string ResolveExternalModulesDir()
    {
        return Path.Combine(
            Path.GetDirectoryName(typeof(ModuleService).Assembly.Location)!,
            "external-modules");
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
