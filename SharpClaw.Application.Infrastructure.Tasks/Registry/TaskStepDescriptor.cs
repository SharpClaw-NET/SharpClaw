using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Application.Infrastructure.Tasks.Registry;

/// <summary>
/// Describes a single task step operation that can be registered in
/// <see cref="TaskStepRegistry"/>. Core step descriptors are built-in;
/// module descriptors are added during module startup.
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
    /// Use <see cref="WellKnownTaskStepKeys"/> for core operations.
    /// </summary>
    public required string StepKey { get; init; }

    /// <summary>
    /// The module ID that owns this step, or <see cref="CoreOwner"/>
    /// for built-in core steps.
    /// </summary>
    public required string OwnerId { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the first method argument is captured
    /// as <c>Expression</c> on the parsed <see cref="Models.TaskStepDefinition"/>.
    /// </summary>
    public bool FirstArgIsExpression { get; init; }

    /// <summary>
    /// HTTP verb for HTTP-request descriptors ("GET", "POST", "PUT", "DELETE").
    /// <see langword="null"/> for non-HTTP steps.
    /// </summary>
    public string? HttpMethod { get; init; }

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

    /// <summary>Owner marker for all built-in core steps.</summary>
    public const string CoreOwner = "core";
}
