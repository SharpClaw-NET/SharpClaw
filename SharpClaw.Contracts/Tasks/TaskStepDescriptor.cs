namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Describes a single task step operation that can be registered in
/// the task step registry. All descriptors are owned by a module
/// (<see cref="OwnerId"/>) and contributed to the host via
/// <see cref="ITaskStepDescriptorProvider"/>.
/// </summary>
public sealed record TaskStepDescriptor
{
    /// <summary>
    /// The script method name as it appears in a task script body
    /// (e.g. <c>Chat</c>, <c>StartTranscription</c>).
    /// For statement constructs that are not method calls (declarations,
    /// assignments, control flow) this is <see langword="null"/>.
    /// </summary>
    public string? MethodName { get; init; }

    /// <summary>
    /// Stable wire-style step key (e.g. <c>core.chat</c>).
    /// </summary>
    public required string StepKey { get; init; }

    /// <summary>
    /// The module ID that owns this step. There are no longer any core-owned
    /// descriptors; every descriptor must declare a module owner.
    /// </summary>
    public required string OwnerId { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the first method argument is captured
    /// as <c>Expression</c> on the parsed step.
    /// </summary>
    public bool FirstArgIsExpression { get; init; }

    /// <summary>
    /// Optional constant value to prepend to the parsed step's
    /// <c>Arguments</c> list. Lets a module encode descriptor-specific
    /// data (e.g. an HTTP verb) without leaking it into the
    /// host-side step contract.
    /// </summary>
    public string? PrefixArgument { get; init; }

    /// <summary>
    /// <see langword="true"/> when the method uses a generic type argument
    /// that should be captured as <c>TypeName</c> (e.g. <c>ParseResponse&lt;T&gt;</c>).
    /// </summary>
    public bool CapturesGenericType { get; init; }

    /// <summary>
    /// When set, the index of the argument that becomes <c>Expression</c>
    /// (overrides <see cref="FirstArgIsExpression"/> when non-zero).
    /// </summary>
    public int ExpressionArgIndex { get; init; }
}
