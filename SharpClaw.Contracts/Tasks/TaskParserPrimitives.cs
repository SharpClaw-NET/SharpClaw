namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Wire-format step-key strings for the statement-shaped constructs the
/// task-script parser emits directly (declarations, assignments, control
/// flow, return, delay, evaluate, log, parse-response).
/// <para>
/// These keys have no method-name binding and therefore cannot flow through
/// <see cref="ITaskParserModuleExtension.StepKeyMappings"/>. A module that
/// owns the scripting-language primitives supplies an instance of this
/// record via <see cref="ITaskParserModuleExtension.Primitives"/>; the
/// parser stores it and stamps these values on
/// <c>TaskStepDefinition.StepKey</c> as it produces statement nodes.
/// </para>
/// <para>
/// Core defines no step-name constants of its own; every value comes from
/// the registering module.
/// </para>
/// </summary>
public sealed record TaskParserPrimitives
{
    /// <summary>Step key for variable declarations.</summary>
    public required string DeclareVariable { get; init; }

    /// <summary>Step key for variable / property assignments.</summary>
    public required string Assign { get; init; }

    /// <summary>Step key for event-handler registrations.</summary>
    public required string EventHandler { get; init; }

    /// <summary>Step key for if/else conditional branches.</summary>
    public required string Conditional { get; init; }

    /// <summary>Step key for while/foreach loops.</summary>
    public required string Loop { get; init; }

    /// <summary>Step key for return statements.</summary>
    public required string Return { get; init; }

    /// <summary>Step key for the parser-recognized <c>Task.Delay</c> form.</summary>
    public required string Delay { get; init; }

    /// <summary>Step key for evaluated expressions.</summary>
    public required string Evaluate { get; init; }

    /// <summary>Step key for log statements.</summary>
    public required string Log { get; init; }

    /// <summary>
    /// Step key for parse-response steps. Validator-recognized so that
    /// <c>TypeName</c> on parse-response steps is checked against the
    /// task's known type set.
    /// </summary>
    public required string ParseResponse { get; init; }
}
