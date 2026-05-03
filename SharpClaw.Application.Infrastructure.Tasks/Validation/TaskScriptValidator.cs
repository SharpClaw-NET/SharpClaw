using System.Runtime.InteropServices;
using SharpClaw.Application.Infrastructure.Tasks.Models;
using SharpClaw.Application.Infrastructure.Tasks.Parsing;
using SharpClaw.Contracts.Tasks;

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

        ValidateRequirements(definition, diagnostics);

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
        if (step.StepKey == TaskScriptParser.Primitives.DeclareVariable && step.VariableName is not null)
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

        if (step.StepKey == TaskScriptParser.Primitives.Loop && step.VariableName is not null)
        {
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

        if (step.StepKey == TaskScriptParser.Primitives.ParseResponse &&
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

    private static void ValidateRequirements(
        TaskScriptDefinition definition,
        List<TaskDiagnostic> diagnostics)
    {
        var seen = new HashSet<(TaskRequirementKind, string?, string?, string?)>();

        foreach (var req in definition.Requirements)
        {
            // TASK407: duplicate requirement tuple
            var key = (req.Kind, req.Value, req.CapabilityValue, req.ParameterName);
            if (!seen.Add(key))
            {
                diagnostics.Add(new TaskDiagnostic(
                    TaskDiagnosticSeverity.Warning,
                    "TASK407",
                    $"Duplicate requirement '{req.Kind}' with the same arguments is redundant.",
                    req.Line));
                continue;
            }

            switch (req.Kind)
            {
                case TaskRequirementKind.RequiresPlatform:
                {
                    // TASK401: platform value must be a valid TaskPlatform flag name
                    if (string.IsNullOrWhiteSpace(req.Value) ||
                        !Enum.TryParse<TaskPlatform>(req.Value, ignoreCase: false, out var platform))
                    {
                        diagnostics.Add(new TaskDiagnostic(
                            TaskDiagnosticSeverity.Error,
                            "TASK401",
                            $"RequiresPlatform value '{req.Value}' is not a valid TaskPlatform name. " +
                            "Expected one of: Windows, Linux, MacOS, AnyDesktop.",
                            req.Line));
                        break;
                    }

                    // TASK402: current host must satisfy the declared platform
                    var hostSatisfies =
                        (platform.HasFlag(TaskPlatform.Windows) && RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) ||
                        (platform.HasFlag(TaskPlatform.Linux)   && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))   ||
                        (platform.HasFlag(TaskPlatform.MacOS)   && RuntimeInformation.IsOSPlatform(OSPlatform.OSX));

                    if (!hostSatisfies)
                    {
                        var severity = req.Severity == TaskDiagnosticSeverity.Warning
                            ? TaskDiagnosticSeverity.Warning
                            : TaskDiagnosticSeverity.Error;
                        diagnostics.Add(new TaskDiagnostic(
                            severity,
                            "TASK402",
                            $"RequiresPlatform '{req.Value}' is not satisfied on the current host.",
                            req.Line));
                    }
                    break;
                }

                case TaskRequirementKind.RequiresProvider:
                    // TASK403: provider value must not be blank
                    if (string.IsNullOrWhiteSpace(req.Value))
                    {
                        diagnostics.Add(new TaskDiagnostic(
                            TaskDiagnosticSeverity.Error,
                            "TASK403",
                            "RequiresProvider must specify a non-empty provider type name.",
                            req.Line));
                    }
                    break;

                case TaskRequirementKind.RequiresModelCapability:
                    // TASK404: capability name must not be blank
                    if (string.IsNullOrWhiteSpace(req.CapabilityValue))
                    {
                        diagnostics.Add(new TaskDiagnostic(
                            TaskDiagnosticSeverity.Error,
                            "TASK404",
                            "RequiresModelCapability must specify a non-empty capability name.",
                            req.Line));
                    }
                    break;

                case TaskRequirementKind.RequiresCapabilityParameter:
                    // TASK404: capability name must not be blank
                    if (string.IsNullOrWhiteSpace(req.CapabilityValue))
                    {
                        diagnostics.Add(new TaskDiagnostic(
                            TaskDiagnosticSeverity.Error,
                            "TASK404",
                            "RequiresCapabilityParameter must specify a non-empty capability name.",
                            req.Line));
                    }

                    // TASK405/TASK406: parameter must exist and be string or Guid
                    ValidateParameterReference(req, definition, diagnostics);
                    break;

                case TaskRequirementKind.ModelIdParameter:
                    // TASK405/TASK406: parameter must exist and be string or Guid
                    ValidateParameterReference(req, definition, diagnostics);
                    break;

                case TaskRequirementKind.RequiresPermission:
                    // TASK408: permission flag key must not be blank
                    if (string.IsNullOrWhiteSpace(req.Value))
                    {
                        diagnostics.Add(new TaskDiagnostic(
                            TaskDiagnosticSeverity.Error,
                            "TASK408",
                            "RequiresPermission must specify a non-empty permission flag key.",
                            req.Line));
                    }
                    break;
            }
        }
    }

    private static void ValidateParameterReference(
        TaskRequirementDefinition req,
        TaskScriptDefinition definition,
        List<TaskDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(req.ParameterName))
        {
            diagnostics.Add(new TaskDiagnostic(
                TaskDiagnosticSeverity.Error,
                "TASK405",
                $"{req.Kind} must specify the name of the parameter it is bound to.",
                req.Line));
            return;
        }

        var param = definition.Parameters
            .FirstOrDefault(p => string.Equals(p.Name, req.ParameterName, StringComparison.Ordinal));

        if (param is null)
        {
            diagnostics.Add(new TaskDiagnostic(
                TaskDiagnosticSeverity.Error,
                "TASK405",
                $"{req.Kind} references parameter '{req.ParameterName}' which is not declared on the task.",
                req.Line));
            return;
        }

        // TASK406: bound parameter must be string or Guid
        var baseType = param.TypeName.TrimEnd('?');
        if (!string.Equals(baseType, "string", StringComparison.Ordinal) &&
            !string.Equals(baseType, "Guid",   StringComparison.Ordinal))
        {
            diagnostics.Add(new TaskDiagnostic(
                TaskDiagnosticSeverity.Error,
                "TASK406",
                $"{req.Kind} is bound to parameter '{req.ParameterName}' of type '{param.TypeName}'. " +
                "Only string or Guid parameters may be used as model/capability selectors.",
                req.Line));
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
