namespace SharpClaw.Application.Infrastructure.Tasks.Models;

/// <summary>
/// An input parameter declared in a task script.  Parameters are
/// resolved at task instantiation time from user-supplied values
/// or channel / context defaults.
/// </summary>
public sealed record TaskParameterDefinition(
    string Name,
    string TypeName,
    string? Description = null,
    string? DefaultValue = null,
    bool IsRequired = true);
