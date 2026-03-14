using SharpClaw.Application.Infrastructure.Tasks.Models;
using System.Text.Json;

namespace SharpClaw.Application.Infrastructure.Tasks.Compilation;

/// <summary>
/// Compiles a validated <see cref="TaskScriptDefinition"/> into a
/// <see cref="CompiledTaskPlan"/> ready for execution by the orchestrator.
/// Resolves parameters, inlines constants, and prepares the execution plan.
/// </summary>
public sealed class TaskScriptCompiler
{
    /// <summary>
    /// Compile a validated task script definition into an executable plan.
    /// </summary>
    /// <param name="definition">The parsed and validated script definition.</param>
    /// <param name="parameterValues">
    /// User-supplied parameter values (name → JSON value).
    /// Missing optional parameters fall back to their defaults.
    /// </param>
    public static TaskScriptCompilationResult Compile(
        TaskScriptDefinition definition,
        IReadOnlyDictionary<string, object?>? parameterValues = null)
    {
        var diagnostics = new List<TaskDiagnostic>();
        parameterValues ??= new Dictionary<string, object?>();

        // Resolve parameters: user-supplied, defaults, or error for missing required
        var resolvedParams = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var param in definition.Parameters)
        {
            if (parameterValues.TryGetValue(param.Name, out var userValue))
            {
                resolvedParams[param.Name] = userValue;
            }
            else if (param.DefaultValue is not null)
            {
                // Parse default value (simple literal parsing for now)
                resolvedParams[param.Name] = ParseLiteral(param.DefaultValue, param.TypeName);
            }
            else if (param.IsRequired)
            {
                diagnostics.Add(new TaskDiagnostic(
                    TaskDiagnosticSeverity.Error,
                    "TASK201",
                    $"Required parameter '{param.Name}' was not provided."));
            }
        }

        if (diagnostics.Any(d => d.Severity == TaskDiagnosticSeverity.Error))
        {
            return new TaskScriptCompilationResult(null, diagnostics);
        }

        // For now, execution steps are just a copy of the definition steps
        // In a full implementation, we would:
        // - Inline parameter references
        // - Resolve constant expressions
        // - Optimize control flow
        // - Validate variable scopes
        var executionSteps = definition.Steps;

        var plan = new CompiledTaskPlan
        {
            TaskName = definition.Name,
            Description = definition.Description,
            Definition = definition,
            ParameterValues = resolvedParams,
            ExecutionSteps = executionSteps,
            ToolCallHooks = definition.ToolCallHooks,
            AgentOutputFormat = definition.AgentOutputFormat
        };

        return new TaskScriptCompilationResult(plan, diagnostics);
    }

    private static object? ParseLiteral(string literal, string typeName)
    {
        // Simple literal parsing — expand as needed
        return typeName switch
        {
            "string" => literal.Trim('"'),
            "int" => int.Parse(literal),
            "long" => long.Parse(literal.TrimEnd('L', 'l')),
            "double" => double.Parse(literal),
            "bool" => bool.Parse(literal),
            "Guid" => Guid.Parse(literal.Trim('"')),
            _ => null
        };
    }
}
