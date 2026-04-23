using SharpClaw.Application.Infrastructure.Tasks.Models;

namespace SharpClaw.Application.Infrastructure.Tasks.Validation;

/// <summary>
/// Validates a parsed <see cref="TaskScriptDefinition"/> against the
/// allowed subset rules.  Ensures type references are valid, data types
/// are well-formed, and steps use only permitted operations.
/// </summary>
public sealed class TaskScriptValidator
{
    private static readonly HashSet<string> AllowedPrimitiveTypes = new(StringComparer.Ordinal)
    {
        "string", "int", "long", "double", "decimal", "bool",
        "DateTime", "DateTimeOffset", "TimeSpan", "Guid"
    };

    /// <summary>
    /// Validate a parsed task script definition.
    /// </summary>
    public static TaskScriptValidationResult Validate(TaskScriptDefinition definition)
    {
        var diagnostics = new List<TaskDiagnostic>();

        // Build set of known types: primitives + task-defined data types
        var knownTypes = new HashSet<string>(AllowedPrimitiveTypes, StringComparer.Ordinal);
        foreach (var dt in definition.DataTypes)
        {
            knownTypes.Add(dt.Name);
        }

        // Validate parameters
        foreach (var param in definition.Parameters)
        {
            if (!IsValidType(param.TypeName, knownTypes))
            {
                diagnostics.Add(new TaskDiagnostic(
                    TaskDiagnosticSeverity.Error,
                    "TASK101",
                    $"Parameter '{param.Name}' has invalid type '{param.TypeName}'. " +
                    "Only primitive types and task-defined data types are allowed."));
            }
        }

        // Validate data types
        foreach (var dataType in definition.DataTypes)
        {
            foreach (var prop in dataType.Properties)
            {
                var typeToCheck = prop.IsCollection ? prop.ElementTypeName! : prop.TypeName;
                if (!IsValidType(typeToCheck, knownTypes))
                {
                    diagnostics.Add(new TaskDiagnostic(
                        TaskDiagnosticSeverity.Error,
                        "TASK102",
                        $"Property '{dataType.Name}.{prop.Name}' has invalid type '{typeToCheck}'."));
                }
            }
        }

        // Ensure at most one output type
        var outputCount = definition.DataTypes.Count(dt => dt.IsOutputType);
        if (outputCount > 1)
        {
            diagnostics.Add(new TaskDiagnostic(
                TaskDiagnosticSeverity.Error,
                "TASK103",
                "Only one data type can be marked with [Output] attribute."));
        }

        // Validate steps (recursive)
        var context = new ValidationContext(knownTypes, new HashSet<string>(StringComparer.Ordinal));
        foreach (var step in definition.Steps)
        {
            ValidateStep(step, context, diagnostics);
        }

        return new TaskScriptValidationResult(
            diagnostics.All(d => d.Severity != TaskDiagnosticSeverity.Error),
            diagnostics);
    }

    private static bool IsValidType(string typeName, HashSet<string> knownTypes)
    {
        // Handle nullable types
        if (typeName.EndsWith("?"))
        {
            typeName = typeName[..^1];
        }

        // Handle collection types
        if (typeName.StartsWith("List<") ||
            typeName.StartsWith("IList<") ||
            typeName.StartsWith("IEnumerable<") ||
            typeName.StartsWith("ICollection<"))
        {
            var start = typeName.IndexOf('<') + 1;
            var end = typeName.LastIndexOf('>');
            if (end > start)
            {
                var elementType = typeName.Substring(start, end - start);
                return IsValidType(elementType, knownTypes);
            }
            return false;
        }

        return knownTypes.Contains(typeName);
    }

    private static void ValidateStep(
        TaskStepDefinition step,
        ValidationContext context,
        List<TaskDiagnostic> diagnostics)
    {
        // Track declared variables
        if (step.Kind == TaskStepKind.DeclareVariable && step.VariableName is not null)
        {
            if (context.DeclaredVariables.Contains(step.VariableName))
            {
                diagnostics.Add(new TaskDiagnostic(
                    TaskDiagnosticSeverity.Error,
                    "TASK104",
                    $"Variable '{step.VariableName}' is already declared.",
                    step.Line,
                    step.Column));
            }
            else
            {
                context.DeclaredVariables.Add(step.VariableName);
            }

            // Validate type
            if (step.TypeName is not null && !IsValidType(step.TypeName, context.KnownTypes))
            {
                diagnostics.Add(new TaskDiagnostic(
                    TaskDiagnosticSeverity.Error,
                    "TASK105",
                    $"Variable '{step.VariableName}' has invalid type '{step.TypeName}'.",
                    step.Line,
                    step.Column));
            }
        }

        // Validate result variable assignment
        if (step.ResultVariable is not null)
        {
            context.DeclaredVariables.Add(step.ResultVariable);
        }

        if (step.Kind == TaskStepKind.Loop)
        {
            var loopKind = step.LoopKind ?? (step.VariableName is not null
                ? TaskLoopKind.ForEach
                : TaskLoopKind.While);

            if (loopKind == TaskLoopKind.ForEach)
            {
                if (string.IsNullOrWhiteSpace(step.VariableName))
                {
                    diagnostics.Add(new TaskDiagnostic(
                        TaskDiagnosticSeverity.Error,
                        "TASK106",
                        "Foreach loops must declare an iteration variable.",
                        step.Line,
                        step.Column));
                }

                if (string.IsNullOrWhiteSpace(step.Expression))
                {
                    diagnostics.Add(new TaskDiagnostic(
                        TaskDiagnosticSeverity.Error,
                        "TASK107",
                        "Foreach loops must declare a source expression.",
                        step.Line,
                        step.Column));
                }
            }
        }

        if (step.Kind == TaskStepKind.ParseResponse &&
            !string.IsNullOrWhiteSpace(step.TypeName) &&
            !IsValidType(step.TypeName, context.KnownTypes))
        {
            diagnostics.Add(new TaskDiagnostic(
                TaskDiagnosticSeverity.Error,
                "TASK108",
                $"ParseResponse references unknown type '{step.TypeName}'.",
                step.Line,
                step.Column));
        }

        // Validate nested bodies
        if (step.Body is not null)
        {
            foreach (var nested in step.Body)
            {
                ValidateStep(nested, context, diagnostics);
            }
        }

        if (step.ElseBody is not null)
        {
            foreach (var nested in step.ElseBody)
            {
                ValidateStep(nested, context, diagnostics);
            }
        }
    }

    private sealed class ValidationContext
    {
        public HashSet<string> KnownTypes { get; }
        public HashSet<string> DeclaredVariables { get; }

        public ValidationContext(HashSet<string> knownTypes, HashSet<string> declaredVariables)
        {
            KnownTypes = knownTypes;
            DeclaredVariables = declaredVariables;
        }
    }
}
