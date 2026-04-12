using System.Text.RegularExpressions;

using SharpClaw.Application.Core.Clients;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Application.Core.Modules;

/// <summary>
/// Singleton registry of all loaded modules. Provides thread-safe access
/// to module instances, tool definitions, manifests, permission descriptors,
/// and contract-based dependency graph. Registration happens at startup
/// (single-threaded); reads happen concurrently from HTTP request threads.
/// A <see cref="ReaderWriterLockSlim"/> protects all mutable state.
/// </summary>
public sealed class ModuleRegistry
{
    private readonly Dictionary<string, ISharpClawModule> _modules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string ModuleId, string ToolName)> _toolIndex = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inlineToolIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ModuleManifest> _manifestCache = new(StringComparer.Ordinal);

    // Contract name → (providing module ID, service type).
    // Only one module may export a given contract at a time.
    private readonly Dictionary<string, (string ModuleId, Type ServiceType)> _contractProviders = new(StringComparer.Ordinal);

    // CLI command name/alias → (module ID, command definition).
    private readonly Dictionary<string, (string ModuleId, ModuleCliCommand Command)> _cliTopLevel = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string ModuleId, ModuleCliCommand Command)> _cliResourceTypes = new(StringComparer.OrdinalIgnoreCase);

    // Resource type string → descriptor provided by a module.
    // Only one module may own a given resource type at a time.
    private readonly Dictionary<string, ModuleResourceTypeDescriptor> _resourceTypeDescriptors = new(StringComparer.Ordinal);

    // DelegateMethodName → resource type string. Reverse index for fast
    // permission evaluation: AgentActionService resolves DelegateTo strings
    // to resource types through this registry instead of a static class.
    private readonly Dictionary<string, string> _delegateToResourceType = new(StringComparer.Ordinal);

    // FlagKey → descriptor provided by a module (e.g. "CanClickDesktop" → descriptor).
    // Only one module may own a given global flag at a time.
    private readonly Dictionary<string, ModuleGlobalFlagDescriptor> _globalFlagDescriptors = new(StringComparer.Ordinal);

    // DelegateMethodName → FlagKey. Reverse index for fast permission evaluation:
    // AgentActionService resolves DelegateTo strings to flag keys through this registry.
    private readonly Dictionary<string, string> _delegateToFlagKey = new(StringComparer.Ordinal);

    // Owner tracking: flag key / resource type → module ID.
    // Used by the permissions-metadata endpoint to group flags and resources by module.
    private readonly Dictionary<string, string> _flagOwnerModule = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _resourceTypeOwnerModule = new(StringComparer.Ordinal);

    // External (hot-loaded) module hosts keyed by module ID.
    private readonly Dictionary<string, ExternalModuleHost> _externalHosts = new(StringComparer.Ordinal);

    private readonly ReaderWriterLockSlim _lock = new();

    // Cached aggregated tool definitions — rebuilt on registration changes.
    private IReadOnlyList<ChatToolDefinition>? _toolDefsCache;

    private static readonly Regex IdPattern = new(
        @"^[a-z][a-z0-9_]{0,39}$", RegexOptions.Compiled);
    private static readonly Regex PrefixPattern = new(
        @"^[a-z][a-z0-9]{0,19}$", RegexOptions.Compiled);
    private static readonly Regex ContractNamePattern = new(
        @"^[a-z][a-z0-9_]{0,59}$", RegexOptions.Compiled);

    /// <summary>
    /// Register a module. Validates ID format, prefix uniqueness, tool name
    /// uniqueness, and contract export format/uniqueness. If any validation
    /// step fails, all mutations are rolled back so the registry is never
    /// left in a partially-registered state.
    /// </summary>
    public void Register(ISharpClawModule module, ExternalModuleHost? externalHost = null)
    {
        ArgumentNullException.ThrowIfNull(module);

        if (!IdPattern.IsMatch(module.Id))
            throw new InvalidOperationException(
                $"Module ID '{module.Id}' is invalid. " +
                "Must be lowercase alphanumeric + underscores, start with a letter, max 40 chars.");

        if (!PrefixPattern.IsMatch(module.ToolPrefix))
            throw new InvalidOperationException(
                $"Tool prefix '{module.ToolPrefix}' is invalid. " +
                "Must be lowercase alphanumeric, start with a letter, max 20 chars.");

        _lock.EnterWriteLock();
        try
        {
            if (_modules.ContainsKey(module.Id))
                throw new InvalidOperationException(
                    $"Module '{module.Id}' is already registered.");

            if (_modules.Values.Any(m => m.ToolPrefix == module.ToolPrefix))
                throw new InvalidOperationException(
                    $"Tool prefix '{module.ToolPrefix}' is already in use.");

            // --- Phase 1: Validate everything before mutating state ---

            var toolDefs = module.GetToolDefinitions();
            var inlineDefs = module.GetInlineToolDefinitions();
            var exports = module.ExportedContracts;
            var cliCommands = module.GetCliCommands() ?? [];

            // Validate job-pipeline tool names and aliases.
            foreach (var tool in toolDefs)
            {
                if (_toolIndex.ContainsKey(tool.Name))
                    throw new InvalidOperationException(
                        $"Tool name '{tool.Name}' from module '{module.Id}' " +
                        "collides with an existing module tool.");

                if (tool.Aliases is { Count: > 0 } aliases)
                {
                    foreach (var alias in aliases)
                    {
                        if (_toolIndex.ContainsKey(alias))
                            throw new InvalidOperationException(
                                $"Tool alias '{alias}' from module '{module.Id}' " +
                                "collides with an existing module tool.");
                    }
                }
            }

            // Validate inline tool names and aliases.
            foreach (var tool in inlineDefs)
            {
                if (_toolIndex.ContainsKey(tool.Name))
                    throw new InvalidOperationException(
                        $"Inline tool '{tool.Name}' from module '{module.Id}' " +
                        "collides with an existing module tool.");

                if (tool.Aliases is { Count: > 0 } aliases)
                {
                    foreach (var alias in aliases)
                    {
                        if (_toolIndex.ContainsKey(alias))
                            throw new InvalidOperationException(
                                $"Inline tool alias '{alias}' from module '{module.Id}' " +
                                "collides with an existing module tool.");
                    }
                }
            }

            // Validate contract exports.
            foreach (var export in exports)
            {
                if (!ContractNamePattern.IsMatch(export.ContractName))
                    throw new InvalidOperationException(
                        $"Contract name '{export.ContractName}' from module '{module.Id}' is invalid. " +
                        "Must be lowercase alphanumeric + underscores, start with a letter, max 60 chars.");

                if (_contractProviders.TryGetValue(export.ContractName, out var existing))
                    throw new InvalidOperationException(
                        $"Contract '{export.ContractName}' from module '{module.Id}' " +
                        $"is already provided by module '{existing.ModuleId}'.");
            }

            // Validate CLI commands.
            foreach (var cmd in cliCommands)
            {
                var target = cmd.Scope == ModuleCliScope.TopLevel ? _cliTopLevel : _cliResourceTypes;
                foreach (var name in new[] { cmd.Name }.Concat(cmd.Aliases))
                {
                    if (target.ContainsKey(name))
                        throw new InvalidOperationException(
                            $"CLI command '{name}' ({cmd.Scope}) from module '{module.Id}' " +
                            "collides with an existing module CLI command.");
                }
            }

            // Validate resource type descriptors.
            var resourceDescriptors = module.GetResourceTypeDescriptors();
            foreach (var desc in resourceDescriptors)
            {
                if (_resourceTypeDescriptors.ContainsKey(desc.ResourceType))
                    throw new InvalidOperationException(
                        $"Resource type '{desc.ResourceType}' from module '{module.Id}' " +
                        "is already owned by another module.");

                if (_delegateToResourceType.ContainsKey(desc.DelegateMethodName))
                    throw new InvalidOperationException(
                        $"Delegate method '{desc.DelegateMethodName}' from module '{module.Id}' " +
                        "is already mapped by another module.");
            }

            // Validate global flag descriptors.
            var flagDescriptors = module.GetGlobalFlagDescriptors();
            foreach (var flag in flagDescriptors)
            {
                if (_globalFlagDescriptors.ContainsKey(flag.FlagKey))
                    throw new InvalidOperationException(
                        $"Global flag '{flag.FlagKey}' from module '{module.Id}' " +
                        "collides with an existing flag.");

                if (_delegateToFlagKey.ContainsKey(flag.DelegateMethodName))
                    throw new InvalidOperationException(
                        $"Global flag delegate '{flag.DelegateMethodName}' from module '{module.Id}' " +
                        "is already mapped by another module.");

                // Also verify no collision with resource type delegates.
                if (_delegateToResourceType.ContainsKey(flag.DelegateMethodName))
                    throw new InvalidOperationException(
                        $"Global flag delegate '{flag.DelegateMethodName}' from module '{module.Id}' " +
                        "collides with a resource type delegate from another module.");
            }

            // --- Phase 2: All checks passed — commit all mutations ---

            _modules[module.Id] = module;

            foreach (var tool in toolDefs)
            {
                _toolIndex[tool.Name] = (module.Id, tool.Name);

                if (tool.Aliases is { Count: > 0 } aliases)
                {
                    foreach (var alias in aliases)
                        _toolIndex[alias] = (module.Id, tool.Name);
                }
            }

            foreach (var tool in inlineDefs)
            {
                _toolIndex[tool.Name] = (module.Id, tool.Name);
                _inlineToolIndex.Add(tool.Name);

                if (tool.Aliases is { Count: > 0 } aliases)
                {
                    foreach (var alias in aliases)
                    {
                        _toolIndex[alias] = (module.Id, tool.Name);
                        _inlineToolIndex.Add(alias);
                    }
                }
            }

            foreach (var export in exports)
                _contractProviders[export.ContractName] = (module.Id, export.ServiceType);

            foreach (var cmd in cliCommands)
            {
                var target = cmd.Scope == ModuleCliScope.TopLevel ? _cliTopLevel : _cliResourceTypes;
                target[cmd.Name] = (module.Id, cmd);
                foreach (var alias in cmd.Aliases)
                    target[alias] = (module.Id, cmd);
            }

            foreach (var desc in resourceDescriptors)
            {
                _resourceTypeDescriptors[desc.ResourceType] = desc;
                _delegateToResourceType[desc.DelegateMethodName] = desc.ResourceType;
                _resourceTypeOwnerModule[desc.ResourceType] = module.Id;
            }

            foreach (var flag in flagDescriptors)
            {
                _globalFlagDescriptors[flag.FlagKey] = flag;
                _delegateToFlagKey[flag.DelegateMethodName] = flag.FlagKey;
                _flagOwnerModule[flag.FlagKey] = module.Id;
            }

            if (externalHost is not null)
                _externalHosts[module.Id] = externalHost;

            _toolDefsCache = null; // Invalidate
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Unregister a module (e.g. on InitializeAsync failure).</summary>
    public void Unregister(string moduleId)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_modules.Remove(moduleId, out var module)) return;

            foreach (var tool in module.GetToolDefinitions())
            {
                _toolIndex.Remove(tool.Name);

                if (tool.Aliases is { Count: > 0 } aliases)
                {
                    foreach (var alias in aliases)
                        _toolIndex.Remove(alias);
                }
            }

            foreach (var tool in module.GetInlineToolDefinitions())
            {
                _toolIndex.Remove(tool.Name);
                _inlineToolIndex.Remove(tool.Name);

                if (tool.Aliases is { Count: > 0 } aliases)
                {
                    foreach (var alias in aliases)
                    {
                        _toolIndex.Remove(alias);
                        _inlineToolIndex.Remove(alias);
                    }
                }
            }

            // Remove any contracts this module exported.
            foreach (var export in module.ExportedContracts)
                _contractProviders.Remove(export.ContractName);

            // Remove any CLI commands this module provided.
            foreach (var cmd in module.GetCliCommands() ?? [])
            {
                var target = cmd.Scope == ModuleCliScope.TopLevel ? _cliTopLevel : _cliResourceTypes;
                target.Remove(cmd.Name);
                foreach (var alias in cmd.Aliases)
                    target.Remove(alias);
            }

            // Remove any resource type descriptors this module provided.
            foreach (var desc in module.GetResourceTypeDescriptors())
            {
                _resourceTypeDescriptors.Remove(desc.ResourceType);
                _delegateToResourceType.Remove(desc.DelegateMethodName);
                _resourceTypeOwnerModule.Remove(desc.ResourceType);
            }

            // Remove any global flag descriptors this module provided.
            foreach (var flag in module.GetGlobalFlagDescriptors())
            {
                _globalFlagDescriptors.Remove(flag.FlagKey);
                _delegateToFlagKey.Remove(flag.DelegateMethodName);
                _flagOwnerModule.Remove(flag.FlagKey);
            }

            _manifestCache.Remove(moduleId);
            _externalHosts.Remove(moduleId);
            _toolDefsCache = null;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Cache a parsed manifest for a loaded module.</summary>
    public void CacheManifest(string moduleId, ModuleManifest manifest)
    {
        _lock.EnterWriteLock();
        try { _manifestCache[moduleId] = manifest; }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Get a cached manifest by module ID.</summary>
    public ModuleManifest? GetManifest(string moduleId)
    {
        _lock.EnterReadLock();
        try { return _manifestCache.GetValueOrDefault(moduleId); }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Try to resolve a tool name (or alias) to its module and canonical tool name.</summary>
    public bool TryResolve(string toolName, out string moduleId, out string canonicalToolName)
    {
        _lock.EnterReadLock();
        try
        {
            if (_toolIndex.TryGetValue(toolName, out var entry))
            {
                moduleId = entry.ModuleId;
                canonicalToolName = entry.ToolName;
                return true;
            }
            moduleId = canonicalToolName = "";
            return false;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Get a module by ID.</summary>
    public ISharpClawModule? GetModule(string moduleId)
    {
        _lock.EnterReadLock();
        try { return _modules.GetValueOrDefault(moduleId); }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Find a module by its tool prefix, or <c>null</c> if no module uses that prefix.</summary>
    public ISharpClawModule? GetModuleByPrefix(string toolPrefix)
    {
        _lock.EnterReadLock();
        try { return _modules.Values.FirstOrDefault(m => m.ToolPrefix == toolPrefix); }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Resolve a tool name (or alias) to its owning module and canonical tool name.
    /// Returns <c>null</c> if the tool is not registered by any loaded module.
    /// </summary>
    public (ISharpClawModule Module, string ToolName)? FindToolByName(string toolName)
    {
        _lock.EnterReadLock();
        try
        {
            if (!_toolIndex.TryGetValue(toolName, out var entry)) return null;
            if (!_modules.TryGetValue(entry.ModuleId, out var module)) return null;
            return (module, entry.ToolName);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Get all loaded modules.</summary>
    public IReadOnlyList<ISharpClawModule> GetAllModules()
    {
        _lock.EnterReadLock();
        try { return [.. _modules.Values]; }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Get all <see cref="ChatToolDefinition"/>s from all modules.
    /// Results are cached and only rebuilt when modules are registered/unregistered.
    /// </summary>
    public IReadOnlyList<ChatToolDefinition> GetAllToolDefinitions()
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            if (_toolDefsCache is not null) return _toolDefsCache;

            _lock.EnterWriteLock();
            try
            {
                _toolDefsCache = _modules.Values
                    .SelectMany(m =>
                    {
                        // Job-pipeline tools
                        var jobTools = m.GetToolDefinitions().SelectMany(t =>
                        {
                            if (t.Aliases is { Count: > 0 } aliases)
                            {
                                return aliases.Select(alias => new ChatToolDefinition(
                                    Name: alias,
                                    Description: t.Description,
                                    ParametersSchema: t.ParametersSchema));
                            }

                            return [new ChatToolDefinition(
                                Name: t.Name,
                                Description: t.Description,
                                ParametersSchema: t.ParametersSchema)];
                        });

                        // Inline tools
                        var inlineTools = m.GetInlineToolDefinitions().SelectMany(t =>
                        {
                            if (t.Aliases is { Count: > 0 } aliases)
                            {
                                return aliases.Select(alias => new ChatToolDefinition(
                                    Name: alias,
                                    Description: t.Description,
                                    ParametersSchema: t.ParametersSchema));
                            }

                            return [new ChatToolDefinition(
                                Name: t.Name,
                                Description: t.Description,
                                ParametersSchema: t.ParametersSchema)];
                        });

                        return jobTools.Concat(inlineTools);
                    })
                    .ToList();
                return _toolDefsCache;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }
        finally
        {
            _lock.ExitUpgradeableReadLock();
        }
    }

    /// <summary>Check if a tool name is an inline tool.</summary>
    public bool IsInlineTool(string toolName)
    {
        _lock.EnterReadLock();
        try { return _inlineToolIndex.Contains(toolName); }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Get a permission descriptor for a specific module tool (job-pipeline or inline).</summary>
    public ModuleToolPermission? GetPermissionDescriptor(string moduleId, string toolName)
    {
        _lock.EnterReadLock();
        try
        {
            if (!_modules.TryGetValue(moduleId, out var module)) return null;

            // Check job-pipeline tools first, then inline tools.
            return module.GetToolDefinitions()
                       .FirstOrDefault(t => t.Name == toolName)?.Permission
                ?? module.GetInlineToolDefinitions()
                       .FirstOrDefault(t => t.Name == toolName)?.Permission;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Get the per-tool timeout for a specific module tool, or <c>null</c>
    /// if the tool doesn't define one (caller should fall back to manifest timeout).
    /// </summary>
    public int? GetToolTimeout(string moduleId, string toolName)
    {
        _lock.EnterReadLock();
        try
        {
            if (!_modules.TryGetValue(moduleId, out var module)) return null;
            return module.GetToolDefinitions()
                .FirstOrDefault(t => t.Name == toolName)?.TimeoutSeconds;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // CLI command resolution
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Try to resolve a top-level CLI command by verb.</summary>
    public ModuleCliCommand? TryResolveTopLevelCommand(string verb)
    {
        _lock.EnterReadLock();
        try
        {
            return _cliTopLevel.TryGetValue(verb, out var entry) ? entry.Command : null;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Try to resolve a module-provided resource-type CLI command.</summary>
    public ModuleCliCommand? TryResolveResourceTypeCommand(string type)
    {
        _lock.EnterReadLock();
        try
        {
            return _cliResourceTypes.TryGetValue(type, out var entry) ? entry.Command : null;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Get all distinct module CLI commands for help output.</summary>
    public IReadOnlyList<(string ModuleId, ModuleCliCommand Command)> GetAllCliCommands()
    {
        _lock.EnterReadLock();
        try
        {
            var seen = new HashSet<ModuleCliCommand>();
            var result = new List<(string, ModuleCliCommand)>();
            foreach (var (_, entry) in _cliTopLevel)
            {
                if (seen.Add(entry.Command))
                    result.Add(entry);
            }
            foreach (var (_, entry) in _cliResourceTypes)
            {
                if (seen.Add(entry.Command))
                    result.Add(entry);
            }
            return result;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Resource type descriptors
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Get all registered resource type descriptors from all modules.</summary>
    public IReadOnlyList<ModuleResourceTypeDescriptor> GetAllResourceTypeDescriptors()
    {
        _lock.EnterReadLock();
        try { return [.. _resourceTypeDescriptors.Values]; }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Get a resource type descriptor by its resource type string, or <c>null</c>.</summary>
    public ModuleResourceTypeDescriptor? GetResourceTypeDescriptor(string resourceType)
    {
        _lock.EnterReadLock();
        try { return _resourceTypeDescriptors.GetValueOrDefault(resourceType); }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Resolve a <c>DelegateTo</c> method name to its resource type string.
    /// Returns <c>null</c> when the delegate name is not registered by any
    /// module (i.e. it is a global-flag delegate with no per-resource type).
    /// </summary>
    public string? ResolveResourceType(string delegateMethodName)
    {
        _lock.EnterReadLock();
        try { return _delegateToResourceType.GetValueOrDefault(delegateMethodName); }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Get all registered resource type strings as a dynamic, module-driven set.
    /// </summary>
    public IReadOnlyList<string> GetAllRegisteredResourceTypes()
    {
        _lock.EnterReadLock();
        try { return [.. _resourceTypeDescriptors.Keys]; }
        finally { _lock.ExitReadLock(); }
    }

    // ═══════════════════════════════════════════════════════════════
    // Global flag descriptors
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Get all registered global flag descriptors from all modules.</summary>
    public IReadOnlyList<ModuleGlobalFlagDescriptor> GetAllGlobalFlagDescriptors()
    {
        _lock.EnterReadLock();
        try { return [.. _globalFlagDescriptors.Values]; }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Get all registered global flag keys.</summary>
    public IReadOnlyList<string> GetAllRegisteredGlobalFlags()
    {
        _lock.EnterReadLock();
        try { return [.. _globalFlagDescriptors.Keys]; }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Resolve a <c>DelegateTo</c> method name to its global flag key.
    /// Returns <c>null</c> when the delegate name is not a global flag
    /// (i.e. it is a per-resource delegate).
    /// </summary>
    public string? ResolveGlobalFlag(string delegateMethodName)
    {
        _lock.EnterReadLock();
        try { return _delegateToFlagKey.GetValueOrDefault(delegateMethodName); }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Get a global flag descriptor by its flag key, or <c>null</c>.</summary>
    public ModuleGlobalFlagDescriptor? GetGlobalFlagDescriptor(string flagKey)
    {
        _lock.EnterReadLock();
        try { return _globalFlagDescriptors.GetValueOrDefault(flagKey); }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Get the module ID that owns a specific global flag, or <c>null</c>.</summary>
    public string? GetFlagOwnerModule(string flagKey)
    {
        _lock.EnterReadLock();
        try { return _flagOwnerModule.GetValueOrDefault(flagKey); }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Get the module ID that owns a specific resource type, or <c>null</c>.</summary>
    public string? GetResourceTypeOwnerModule(string resourceType)
    {
        _lock.EnterReadLock();
        try { return _resourceTypeOwnerModule.GetValueOrDefault(resourceType); }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Get all global flag descriptors grouped by owning module ID.</summary>
    public Dictionary<string, List<ModuleGlobalFlagDescriptor>> GetFlagsByModule()
    {
        _lock.EnterReadLock();
        try
        {
            var result = new Dictionary<string, List<ModuleGlobalFlagDescriptor>>(StringComparer.Ordinal);
            foreach (var (flagKey, descriptor) in _globalFlagDescriptors)
            {
                if (!_flagOwnerModule.TryGetValue(flagKey, out var moduleId)) continue;
                if (!result.TryGetValue(moduleId, out var list))
                {
                    list = [];
                    result[moduleId] = list;
                }
                list.Add(descriptor);
            }
            return result;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Get all resource type descriptors grouped by owning module ID.</summary>
    public Dictionary<string, List<ModuleResourceTypeDescriptor>> GetResourceTypesByModule()
    {
        _lock.EnterReadLock();
        try
        {
            var result = new Dictionary<string, List<ModuleResourceTypeDescriptor>>(StringComparer.Ordinal);
            foreach (var (resType, descriptor) in _resourceTypeDescriptors)
            {
                if (!_resourceTypeOwnerModule.TryGetValue(resType, out var moduleId)) continue;
                if (!result.TryGetValue(moduleId, out var list))
                {
                    list = [];
                    result[moduleId] = list;
                }
                list.Add(descriptor);
            }
            return result;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Resolve a contract name to its providing module ID and service type.
    /// Returns <c>null</c> if no module exports the contract.
    /// </summary>
    public (string ModuleId, Type ServiceType)? ResolveContract(string contractName)
    {
        _lock.EnterReadLock();
        try
        {
            return _contractProviders.TryGetValue(contractName, out var entry)
                ? entry
                : null;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Get the <see cref="ExternalModuleHost"/> for a hot-loaded module,
    /// or <c>null</c> if the module is bundled or not registered.
    /// </summary>
    public ExternalModuleHost? GetExternalHost(string moduleId)
    {
        _lock.EnterReadLock();
        try
        {
            return _externalHosts.GetValueOrDefault(moduleId);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Whether the given module was loaded externally (hot-loaded).</summary>
    public bool IsExternal(string moduleId)
    {
        _lock.EnterReadLock();
        try
        {
            return _externalHosts.ContainsKey(moduleId);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Return the list of non-optional contract requirements for a module
    /// that are not currently satisfied by any loaded module's exports.
    /// </summary>
    public IReadOnlyList<ModuleContractRequirement> GetUnsatisfiedRequirements(string moduleId)
    {
        _lock.EnterReadLock();
        try
        {
            if (!_modules.TryGetValue(moduleId, out var module))
                return [];

            return module.RequiredContracts
                .Where(r => !r.Optional && !IsContractSatisfied(r))
                .ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Compute a topological initialization order for all registered modules
    /// based on their contract dependencies. Modules that export contracts
    /// required by other modules are initialized first.
    /// <para>
    /// Modules with unsatisfied non-optional requirements (including
    /// cascading failures) are excluded from the result and reported
    /// via <paramref name="excluded"/>. Cycles are also detected and
    /// reported as excluded.
    /// </para>
    /// </summary>
    /// <param name="excluded">
    /// Modules that were excluded, each with a human-readable reason.
    /// </param>
    /// <returns>
    /// Module IDs in safe initialization order (providers before consumers).
    /// </returns>
    public IReadOnlyList<string> GetInitializationOrder(
        out IReadOnlyList<(string ModuleId, string Reason)> excluded)
    {
        _lock.EnterReadLock();
        try
        {
            return ComputeInitializationOrder(out excluded);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private bool IsContractSatisfied(ModuleContractRequirement requirement)
    {
        if (!_contractProviders.TryGetValue(requirement.ContractName, out var provider))
            return false;

        // If the consumer specified a ServiceType, validate type compatibility.
        if (requirement.ServiceType is not null &&
            !requirement.ServiceType.IsAssignableFrom(provider.ServiceType))
            return false;

        return true;
    }

    /// <summary>
    /// Kahn's algorithm with iterative exclusion of unsatisfied modules.
    /// Deterministic tie-breaking via ordinal sort on module ID.
    /// </summary>
    private IReadOnlyList<string> ComputeInitializationOrder(
        out IReadOnlyList<(string ModuleId, string Reason)> excluded)
    {
        var excludedList = new List<(string ModuleId, string Reason)>();

        // Build the set of eligible module IDs. We'll shrink it iteratively
        // as we discover unsatisfied dependencies (which can cascade).
        var eligible = new HashSet<string>(_modules.Keys, StringComparer.Ordinal);

        // Map: contract name → providing module ID (only eligible providers).
        var contractOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, (modId, _)) in _contractProviders)
        {
            if (eligible.Contains(modId))
                contractOwners[name] = modId;
        }

        // Iteratively remove modules whose non-optional requirements are
        // not satisfiable by remaining eligible providers. Repeat until
        // stable because removing a provider can cascade to its dependents.
        bool changed;
        do
        {
            changed = false;
            foreach (var modId in eligible.ToList())
            {
                var module = _modules[modId];
                var missing = module.RequiredContracts
                    .Where(r => !r.Optional && !IsEligibleContractSatisfied(r, eligible, contractOwners))
                    .Select(r => r.ContractName)
                    .ToList();

                if (missing.Count == 0)
                    continue;

                eligible.Remove(modId);

                // Remove any contracts this module provided — may cascade.
                foreach (var export in module.ExportedContracts)
                    contractOwners.Remove(export.ContractName);

                excludedList.Add((modId,
                    $"Unsatisfied contract(s): {string.Join(", ", missing)}"));
                changed = true;
            }
        }
        while (changed);

        // Build adjacency: for each eligible module, edges from each
        // non-optional contract provider to the consuming module.
        // Deduplicate edges: a module requiring two contracts from the same
        // provider should produce only one edge to avoid inflated in-degrees.
        var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var modId in eligible)
        {
            inDegree[modId] = 0;
            dependents[modId] = [];
        }

        foreach (var modId in eligible)
        {
            // Collect distinct provider IDs for all resolved requirements.
            var providers = new HashSet<string>(StringComparer.Ordinal);
            var module = _modules[modId];

            foreach (var req in module.RequiredContracts)
            {
                // Optional requirements that happen to be present still impose ordering.
                if (!contractOwners.TryGetValue(req.ContractName, out var providerId))
                    continue;

                if (providerId == modId)
                    continue; // Self-dependency is a no-op.

                providers.Add(providerId);
            }

            foreach (var providerId in providers)
            {
                dependents[providerId].Add(modId);
                inDegree[modId]++;
            }
        }

        // Kahn's with deterministic tie-breaking (ordinal sort).
        var queue = new SortedSet<string>(
            inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key),
            StringComparer.Ordinal);

        var result = new List<string>(eligible.Count);

        while (queue.Count > 0)
        {
            var next = queue.Min!;
            queue.Remove(next);
            result.Add(next);

            foreach (var dep in dependents[next])
            {
                inDegree[dep]--;
                if (inDegree[dep] == 0)
                    queue.Add(dep);
            }
        }

        // Any remaining eligible modules not in result are in a cycle.
        foreach (var modId in eligible.Except(result))
        {
            excludedList.Add((modId, "Circular dependency detected"));
        }

        excluded = excludedList;
        return result;
    }

    private bool IsEligibleContractSatisfied(
        ModuleContractRequirement requirement,
        HashSet<string> eligible,
        Dictionary<string, string> contractOwners)
    {
        if (!contractOwners.TryGetValue(requirement.ContractName, out var providerId))
            return false;

        if (!eligible.Contains(providerId))
            return false;

        // Validate type compatibility: if the consumer specified a ServiceType,
        // the provider's exported ServiceType must be assignable to it.
        // Without this check, a module with an incompatible type requirement
        // would survive eligibility and only fail at runtime DI resolution.
        if (requirement.ServiceType is not null &&
            _contractProviders.TryGetValue(requirement.ContractName, out var provider) &&
            !requirement.ServiceType.IsAssignableFrom(provider.ServiceType))
            return false;

        return true;
    }
}
