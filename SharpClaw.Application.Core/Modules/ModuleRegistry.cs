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
    private readonly Dictionary<string, ModuleManifest> _manifestCache = new(StringComparer.Ordinal);

    // Contract name → (providing module ID, service type).
    // Only one module may export a given contract at a time.
    private readonly Dictionary<string, (string ModuleId, Type ServiceType)> _contractProviders = new(StringComparer.Ordinal);

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
    public void Register(ISharpClawModule module)
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
            var exports = module.ExportedContracts;

            // Validate tool names.
            foreach (var tool in toolDefs)
            {
                var prefixed = $"{module.ToolPrefix}_{tool.Name}";

                if (_toolIndex.ContainsKey(prefixed))
                    throw new InvalidOperationException(
                        $"Tool name '{prefixed}' from module '{module.Id}' " +
                        "collides with an existing module tool.");
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

            // --- Phase 2: All checks passed — commit all mutations ---

            _modules[module.Id] = module;

            foreach (var tool in toolDefs)
                _toolIndex[$"{module.ToolPrefix}_{tool.Name}"] = (module.Id, tool.Name);

            foreach (var export in exports)
                _contractProviders[export.ContractName] = (module.Id, export.ServiceType);

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
                _toolIndex.Remove($"{module.ToolPrefix}_{tool.Name}");

            // Remove any contracts this module exported.
            foreach (var export in module.ExportedContracts)
                _contractProviders.Remove(export.ContractName);

            _manifestCache.Remove(moduleId);
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

    /// <summary>Try to resolve a prefixed tool name to its module and local tool name.</summary>
    public bool TryResolve(string prefixedToolName, out string moduleId, out string toolName)
    {
        _lock.EnterReadLock();
        try
        {
            if (_toolIndex.TryGetValue(prefixedToolName, out var entry))
            {
                moduleId = entry.ModuleId;
                toolName = entry.ToolName;
                return true;
            }
            moduleId = toolName = "";
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

    /// <summary>Get all loaded modules.</summary>
    public IReadOnlyList<ISharpClawModule> GetAllModules()
    {
        _lock.EnterReadLock();
        try { return [.. _modules.Values]; }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Get all <see cref="ChatToolDefinition"/>s from all modules (prefixed names).
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
                    .SelectMany(m => m.GetToolDefinitions().Select(t => new ChatToolDefinition(
                        Name: $"{m.ToolPrefix}_{t.Name}",
                        Description: t.Description,
                        ParametersSchema: t.ParametersSchema)))
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

    /// <summary>Get a permission descriptor for a specific module tool.</summary>
    public ModuleToolPermission? GetPermissionDescriptor(string moduleId, string toolName)
    {
        _lock.EnterReadLock();
        try
        {
            if (!_modules.TryGetValue(moduleId, out var module)) return null;
            return module.GetToolDefinitions()
                .FirstOrDefault(t => t.Name == toolName)?.Permission;
        }
        finally
        {
            _lock.ExitReadLock();
        }
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
