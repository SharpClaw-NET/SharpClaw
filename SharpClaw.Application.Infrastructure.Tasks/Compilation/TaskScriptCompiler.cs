using SharpClaw.Application.Infrastructure.Tasks.Models;
using System.Text.Json;
using System.Text.Json.Nodes;

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
                var conversion = TryConvertValue(userValue, param.TypeName);
                if (!conversion.Success)
                {
                    diagnostics.Add(new TaskDiagnostic(
                        TaskDiagnosticSeverity.Error,
                        "TASK202",
                        $"Parameter '{param.Name}' value could not be converted to '{param.TypeName}'."));
                    continue;
                }

                resolvedParams[param.Name] = conversion.Value;
            }
            else if (param.DefaultValue is not null)
            {
                var conversion = TryConvertValue(param.DefaultValue, param.TypeName);
                if (!conversion.Success)
                {
                    diagnostics.Add(new TaskDiagnostic(
                        TaskDiagnosticSeverity.Error,
                        "TASK203",
                        $"Default value for parameter '{param.Name}' could not be converted to '{param.TypeName}'."));
                    continue;
                }

                resolvedParams[param.Name] = conversion.Value;
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

        var executionSteps = NormalizeSteps(definition.Steps);

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

    private static IReadOnlyList<TaskStepDefinition> NormalizeSteps(IReadOnlyList<TaskStepDefinition> steps)
    {
        return steps
            .Select(step => step with
            {
                LoopKind = step.Kind == TaskStepKind.Loop
                    ? step.LoopKind ?? (step.VariableName is not null ? TaskLoopKind.ForEach : TaskLoopKind.While)
                    : step.LoopKind,
                Body = step.Body is not null ? NormalizeSteps(step.Body) : step.Body,
                ElseBody = step.ElseBody is not null ? NormalizeSteps(step.ElseBody) : step.ElseBody,
            })
            .ToList();
    }

    private static (bool Success, object? Value) TryConvertValue(object? rawValue, string typeName)
    {
        var normalizedTypeName = NormalizeTypeName(typeName, out var isNullable);
        if (rawValue is null)
        {
            return (isNullable || normalizedTypeName == "string", null);
        }

        if (TryConvertCollection(rawValue, normalizedTypeName, out var collectionValue))
        {
            return (true, collectionValue);
        }

        if (rawValue is JsonElement jsonElement)
        {
            return TryConvertJsonElement(jsonElement, normalizedTypeName, isNullable);
        }

        if (rawValue is string rawText)
        {
            return TryConvertString(rawText, normalizedTypeName, isNullable);
        }

        return normalizedTypeName switch
        {
            "string" => (true, rawValue.ToString()),
            "int" when rawValue is int i => (true, i),
            "long" when rawValue is long l => (true, l),
            "double" when rawValue is double d => (true, d),
            "decimal" when rawValue is decimal m => (true, m),
            "bool" when rawValue is bool b => (true, b),
            "Guid" when rawValue is Guid g => (true, g),
            "DateTime" when rawValue is DateTime dt => (true, dt),
            "DateTimeOffset" when rawValue is DateTimeOffset dto => (true, dto),
            "TimeSpan" when rawValue is TimeSpan ts => (true, ts),
            _ => (true, rawValue)
        };
    }

    private static bool TryConvertCollection(object rawValue, string typeName, out object? value)
    {
        value = null;
        if (!TryGetCollectionElementType(typeName, out var elementType))
        {
            return false;
        }

        if (rawValue is string stringValue)
        {
            try
            {
                var jsonNode = JsonNode.Parse(stringValue);
                if (jsonNode is JsonArray array)
                {
                    value = ConvertJsonArray(array, elementType);
                    return true;
                }
            }
            catch (JsonException)
            {
                return false;
            }
        }

        if (rawValue is JsonElement element && element.ValueKind == JsonValueKind.Array)
        {
            value = ConvertJsonArray(JsonNode.Parse(element.GetRawText())!.AsArray(), elementType);
            return true;
        }

        return false;
    }

    private static object ConvertJsonArray(JsonArray array, string elementType)
    {
        var values = new List<object?>();
        foreach (var item in array)
        {
            if (item is null)
            {
                values.Add(null);
                continue;
            }

            var conversion = TryConvertValue(item.ToJsonString(), elementType);
            values.Add(conversion.Success ? conversion.Value : item.ToJsonString());
        }

        return values;
    }

    private static (bool Success, object? Value) TryConvertJsonElement(JsonElement element, string typeName, bool isNullable)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return (isNullable || typeName == "string", null);
        }

        return typeName switch
        {
            "string" => (true, element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText()),
            "int" when element.TryGetInt32(out var i) => (true, i),
            "long" when element.TryGetInt64(out var l) => (true, l),
            "double" when element.TryGetDouble(out var d) => (true, d),
            "decimal" when element.TryGetDecimal(out var m) => (true, m),
            "bool" when element.ValueKind is JsonValueKind.True or JsonValueKind.False => (true, element.GetBoolean()),
            "Guid" when element.ValueKind == JsonValueKind.String && Guid.TryParse(element.GetString(), out var g) => (true, g),
            "DateTime" when element.ValueKind == JsonValueKind.String && DateTime.TryParse(element.GetString(), out var dt) => (true, dt),
            "DateTimeOffset" when element.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(element.GetString(), out var dto) => (true, dto),
            "TimeSpan" when element.ValueKind == JsonValueKind.String && TimeSpan.TryParse(element.GetString(), out var ts) => (true, ts),
            _ => (true, element.Clone())
        };
    }

    private static (bool Success, object? Value) TryConvertString(string rawText, string typeName, bool isNullable)
    {
        var text = rawText.Trim();
        if (text == "null")
        {
            return (isNullable || typeName == "string", null);
        }

        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            text = text[1..^1];
        }

        return typeName switch
        {
            "string" => (true, text),
            "int" when int.TryParse(text, out var i) => (true, i),
            "long" when long.TryParse(text.TrimEnd('L', 'l'), out var l) => (true, l),
            "double" when double.TryParse(text, out var d) => (true, d),
            "decimal" when decimal.TryParse(text, out var m) => (true, m),
            "bool" when bool.TryParse(text, out var b) => (true, b),
            "Guid" when Guid.TryParse(text, out var g) => (true, g),
            "DateTime" when DateTime.TryParse(text, out var dt) => (true, dt),
            "DateTimeOffset" when DateTimeOffset.TryParse(text, out var dto) => (true, dto),
            "TimeSpan" when TimeSpan.TryParse(text, out var ts) => (true, ts),
            _ => (true, text)
        };
    }

    private static string NormalizeTypeName(string typeName, out bool isNullable)
    {
        isNullable = typeName.EndsWith("?", StringComparison.Ordinal);
        return isNullable ? typeName[..^1] : typeName;
    }

    private static bool TryGetCollectionElementType(string typeName, out string elementType)
    {
        foreach (var prefix in new[] { "List<", "IList<", "IEnumerable<", "ICollection<" })
        {
            if (typeName.StartsWith(prefix, StringComparison.Ordinal) && typeName.EndsWith(">", StringComparison.Ordinal))
            {
                elementType = typeName[prefix.Length..^1];
                return true;
            }
        }

        elementType = string.Empty;
        return false;
    }
}
