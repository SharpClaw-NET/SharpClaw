using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Application.Infrastructure.Tasks.Registry;

/// <summary>
/// Single authoritative registry for all task step descriptors. All
/// descriptors are module-owned: the registry starts empty and modules
/// populate it during startup via <see cref="Register"/>.
/// </summary>
public sealed class TaskStepRegistry
{
    private readonly Dictionary<string, TaskStepDescriptor> _byMethod =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskStepDescriptor> _byKey =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _methodRegistrationCounts =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _keyRegistrationCounts =
        new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>Shared singleton; populated by modules during startup.</summary>
    public static readonly TaskStepRegistry Default = new();

    /// <summary>
    /// Clear all registered descriptors. Intended for test fixtures that
    /// need to seed a deterministic descriptor set; not for production use.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _byMethod.Clear();
            _byKey.Clear();
            _methodRegistrationCounts.Clear();
            _keyRegistrationCounts.Clear();
        }
    }

    public void UnregisterOwner(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return;

        lock (_lock)
        {
            var stepKeysHandledByMethods = new HashSet<string>(StringComparer.Ordinal);
            foreach (var methodName in _byMethod
                         .Where(pair => string.Equals(pair.Value.OwnerId, ownerId, StringComparison.Ordinal))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                var descriptor = _byMethod[methodName];
                stepKeysHandledByMethods.Add(descriptor.StepKey);

                if (DecrementCount(_methodRegistrationCounts, methodName) == 0)
                    _byMethod.Remove(methodName);

                if (DecrementCount(_keyRegistrationCounts, descriptor.StepKey) == 0)
                    _byKey.Remove(descriptor.StepKey);
            }

            foreach (var stepKey in _byKey
                         .Where(pair => string.Equals(pair.Value.OwnerId, ownerId, StringComparison.Ordinal))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                if (stepKeysHandledByMethods.Contains(stepKey))
                    continue;

                if (DecrementCount(_keyRegistrationCounts, stepKey) == 0)
                    _byKey.Remove(stepKey);
            }
        }
    }

    /// <summary>
    /// Register a step descriptor. Duplicate method names or step keys from
    /// different owners are rejected with <see cref="InvalidOperationException"/>.
    /// Re-registering the same descriptor (same owner, same key, same method) is a no-op.
    /// </summary>
    public void Register(TaskStepDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        lock (_lock)
        {
            if (descriptor.MethodName is not null)
            {
                if (_byMethod.TryGetValue(descriptor.MethodName, out var existing))
                {
                    if (existing.StepKey == descriptor.StepKey && existing.OwnerId == descriptor.OwnerId)
                    {
                        IncrementCount(_methodRegistrationCounts, descriptor.MethodName);
                        IncrementCount(_keyRegistrationCounts, descriptor.StepKey);
                        return; // idempotent re-registration
                    }

                    throw new InvalidOperationException(
                        $"Task step method '{descriptor.MethodName}' is already registered " +
                        $"by owner '{existing.OwnerId}' with key '{existing.StepKey}'. " +
                        $"Attempted to re-register by '{descriptor.OwnerId}' with key '{descriptor.StepKey}'.");
                }
                _byMethod[descriptor.MethodName] = descriptor;
                _methodRegistrationCounts[descriptor.MethodName] = 1;
            }

            if (_byKey.TryGetValue(descriptor.StepKey, out var existingKey))
            {
                if (existingKey.OwnerId != descriptor.OwnerId)
                    throw new InvalidOperationException(
                        $"Task step key '{descriptor.StepKey}' is already registered " +
                        $"by owner '{existingKey.OwnerId}'. " +
                        $"Attempted to re-register by '{descriptor.OwnerId}'.");
                // Same owner, different method sharing the same key (e.g. HTTP verbs) — allowed.
                // _byKey keeps the first registration; all methods are accessible via _byMethod.
                IncrementCount(_keyRegistrationCounts, descriptor.StepKey);
            }
            else
            {
                _byKey[descriptor.StepKey] = descriptor;
                _keyRegistrationCounts[descriptor.StepKey] = 1;
            }
        }
    }

    private static void IncrementCount(Dictionary<string, int> counts, string key)
    {
        counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
    }

    private static int DecrementCount(Dictionary<string, int> counts, string key)
    {
        if (!counts.TryGetValue(key, out var count))
            return 0;

        count--;
        if (count <= 0)
        {
            counts.Remove(key);
            return 0;
        }

        counts[key] = count;
        return count;
    }

    /// <summary>
    /// Look up a descriptor by script method name. Returns <see langword="null"/>
    /// if the method name is not registered.
    /// </summary>
    public TaskStepDescriptor? FindByMethod(string methodName)
    {
        lock (_lock)
            return _byMethod.GetValueOrDefault(methodName);
    }

    /// <summary>
    /// Look up a descriptor by step key. Returns <see langword="null"/>
    /// if the key is not registered.
    /// </summary>
    public TaskStepDescriptor? FindByKey(string stepKey)
    {
        lock (_lock)
            return _byKey.GetValueOrDefault(stepKey);
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="methodName"/> is
    /// registered as a core or module method.
    /// </summary>
    public bool IsRegisteredMethod(string methodName)
    {
        lock (_lock)
            return _byMethod.ContainsKey(methodName);
    }
}
