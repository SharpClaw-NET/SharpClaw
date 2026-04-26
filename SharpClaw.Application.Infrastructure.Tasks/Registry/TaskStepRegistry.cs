using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Application.Infrastructure.Tasks.Registry;

/// <summary>
/// Single authoritative registry for all task step descriptors, both core
/// and module-owned. The registry is initialized once with core descriptors
/// before any parsing occurs. Modules add their own descriptors during startup
/// via <see cref="Register"/>.
/// </summary>
public sealed class TaskStepRegistry
{
    private readonly Dictionary<string, TaskStepDescriptor> _byMethod =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskStepDescriptor> _byKey =
        new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>Shared singleton initialized with all core descriptors.</summary>
    public static readonly TaskStepRegistry Default = CreateWithCoreDescriptors();

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

    private static TaskStepRegistry CreateWithCoreDescriptors()
    {
        var registry = new TaskStepRegistry();
        foreach (var d in CoreDescriptors())
            registry.Register(d);
        return registry;
    }

    private static IEnumerable<TaskStepDescriptor> CoreDescriptors()
    {
        const string core = TaskStepDescriptor.CoreOwner;

        // ── Agent interaction ──────────────────────────────────────────────────

        yield return new TaskStepDescriptor
        {
            MethodName          = "Chat",
            StepKey             = WellKnownTaskStepKeys.Chat,
            OwnerId             = core,
            ExpressionArgIndex  = 1,   // second arg is the message
        };
        yield return new TaskStepDescriptor
        {
            MethodName          = "ChatStream",
            StepKey             = WellKnownTaskStepKeys.ChatStream,
            OwnerId             = core,
            ExpressionArgIndex  = 1,
        };

        // ── Output ────────────────────────────────────────────────────────────

        yield return new TaskStepDescriptor
        {
            MethodName         = "Emit",
            StepKey            = WellKnownTaskStepKeys.Emit,
            OwnerId            = core,
            FirstArgIsExpression = true,
        };
        yield return new TaskStepDescriptor
        {
            MethodName          = "ParseResponse",
            StepKey             = WellKnownTaskStepKeys.ParseResponse,
            OwnerId             = core,
            CapturesGenericType = true,
        };

        // ── Control flow ──────────────────────────────────────────────────────

        // Note: DeclareVariable, Assign, Conditional, Loop, Return, EventHandler,
        // and Evaluate are not method calls; they are registered by key only.

        yield return new TaskStepDescriptor
        {
            StepKey = WellKnownTaskStepKeys.DeclareVariable,
            OwnerId = core,
        };
        yield return new TaskStepDescriptor
        {
            StepKey = WellKnownTaskStepKeys.Assign,
            OwnerId = core,
        };
        yield return new TaskStepDescriptor
        {
            StepKey = WellKnownTaskStepKeys.EventHandler,
            OwnerId = core,
        };
        yield return new TaskStepDescriptor
        {
            StepKey = WellKnownTaskStepKeys.Conditional,
            OwnerId = core,
        };
        yield return new TaskStepDescriptor
        {
            StepKey = WellKnownTaskStepKeys.Loop,
            OwnerId = core,
        };
        yield return new TaskStepDescriptor
        {
            StepKey = WellKnownTaskStepKeys.Return,
            OwnerId = core,
        };
        yield return new TaskStepDescriptor
        {
            StepKey = WellKnownTaskStepKeys.Evaluate,
            OwnerId = core,
        };

        // Task.Delay is syntactically special (member access); registered by key only.
        yield return new TaskStepDescriptor
        {
            MethodName           = "Delay",
            StepKey              = WellKnownTaskStepKeys.Delay,
            OwnerId              = core,
            FirstArgIsExpression = true,
        };

        yield return new TaskStepDescriptor
        {
            MethodName           = "WaitUntilStopped",
            StepKey              = WellKnownTaskStepKeys.WaitUntilStopped,
            OwnerId              = core,
            FirstArgIsExpression = true,
        };

        // ── Logging ───────────────────────────────────────────────────────────

        yield return new TaskStepDescriptor
        {
            MethodName           = "Log",
            StepKey              = WellKnownTaskStepKeys.Log,
            OwnerId              = core,
            FirstArgIsExpression = true,
        };

        // ── HTTP ──────────────────────────────────────────────────────────────

        yield return new TaskStepDescriptor
        {
            MethodName           = "HttpGet",
            StepKey              = WellKnownTaskStepKeys.HttpRequest,
            OwnerId              = core,
            HttpMethod           = "GET",
            FirstArgIsExpression = true,
        };
        yield return new TaskStepDescriptor
        {
            MethodName           = "HttpPost",
            StepKey              = WellKnownTaskStepKeys.HttpRequest,
            OwnerId              = core,
            HttpMethod           = "POST",
            FirstArgIsExpression = true,
        };
        yield return new TaskStepDescriptor
        {
            MethodName           = "HttpPut",
            StepKey              = WellKnownTaskStepKeys.HttpRequest,
            OwnerId              = core,
            HttpMethod           = "PUT",
            FirstArgIsExpression = true,
        };
        yield return new TaskStepDescriptor
        {
            MethodName           = "HttpDelete",
            StepKey              = WellKnownTaskStepKeys.HttpRequest,
            OwnerId              = core,
            HttpMethod           = "DELETE",
            FirstArgIsExpression = true,
        };

        // ── Entity lookup / creation ──────────────────────────────────────────

        yield return new TaskStepDescriptor
        {
            MethodName           = "FindModel",
            StepKey              = WellKnownTaskStepKeys.FindModel,
            OwnerId              = core,
            FirstArgIsExpression = true,
        };
        yield return new TaskStepDescriptor
        {
            MethodName           = "FindProvider",
            StepKey              = WellKnownTaskStepKeys.FindProvider,
            OwnerId              = core,
            FirstArgIsExpression = true,
        };
        yield return new TaskStepDescriptor
        {
            MethodName           = "FindAgent",
            StepKey              = WellKnownTaskStepKeys.FindAgent,
            OwnerId              = core,
            FirstArgIsExpression = true,
        };
        yield return new TaskStepDescriptor
        {
            MethodName          = "CreateAgent",
            StepKey             = WellKnownTaskStepKeys.CreateAgent,
            OwnerId             = core,
            // first arg = name (Expression), second arg = modelId (Arguments)
            FirstArgIsExpression = true,
        };
        yield return new TaskStepDescriptor
        {
            MethodName           = "CreateThread",
            StepKey              = WellKnownTaskStepKeys.CreateThread,
            OwnerId              = core,
            FirstArgIsExpression = true,
        };
        yield return new TaskStepDescriptor
        {
            MethodName          = "ChatToThread",
            StepKey             = WellKnownTaskStepKeys.ChatToThread,
            OwnerId             = core,
            ExpressionArgIndex  = 1,   // second arg is the message
        };

        // ── Role / permission / channel provisioning ──────────────────────────

        yield return new TaskStepDescriptor
        {
            MethodName           = "CreateRole",
            StepKey              = WellKnownTaskStepKeys.CreateRole,
            OwnerId              = core,
            FirstArgIsExpression = true,
        };
        yield return new TaskStepDescriptor
        {
            MethodName           = "FindRole",
            StepKey              = WellKnownTaskStepKeys.FindRole,
            OwnerId              = core,
            FirstArgIsExpression = true,
        };
        yield return new TaskStepDescriptor
        {
            MethodName          = "SetRolePermissions",
            StepKey             = WellKnownTaskStepKeys.SetRolePermissions,
            OwnerId             = core,
            // first arg = roleId as Expression
            FirstArgIsExpression = true,
        };
        yield return new TaskStepDescriptor
        {
            MethodName          = "AssignRole",
            StepKey             = WellKnownTaskStepKeys.AssignRole,
            OwnerId             = core,
            // first arg = agentId as Expression
            FirstArgIsExpression = true,
        };
        yield return new TaskStepDescriptor
        {
            MethodName          = "CreateChannel",
            StepKey             = WellKnownTaskStepKeys.CreateChannel,
            OwnerId             = core,
            // first arg = title as Expression
            FirstArgIsExpression = true,
        };
        yield return new TaskStepDescriptor
        {
            MethodName           = "FindChannel",
            StepKey              = WellKnownTaskStepKeys.FindChannel,
            OwnerId              = core,
            FirstArgIsExpression = true,
        };
    }
}
