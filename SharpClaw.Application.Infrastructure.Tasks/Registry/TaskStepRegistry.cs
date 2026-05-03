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
                        return; // idempotent re-registration

                    throw new InvalidOperationException(
                        $"Task step method '{descriptor.MethodName}' is already registered " +
                        $"by owner '{existing.OwnerId}' with key '{existing.StepKey}'. " +
                        $"Attempted to re-register by '{descriptor.OwnerId}' with key '{descriptor.StepKey}'.");
                }
                _byMethod[descriptor.MethodName] = descriptor;
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
            }
            else
            {
                _byKey[descriptor.StepKey] = descriptor;
            }
        }
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
