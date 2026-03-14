using SharpClaw.Application.Infrastructure.Tasks.Compilation;
using SharpClaw.Application.Infrastructure.Tasks.Parsing;
using SharpClaw.Application.Infrastructure.Tasks.Validation;

namespace SharpClaw.Application.Infrastructure.Tasks;

/// <summary>
/// High-level facade for processing task scripts: parse → validate → compile.
/// </summary>
public static class TaskScriptEngine
{
    /// <summary>
    /// Parse, validate, and compile a task script in one call.
    /// </summary>
    public static TaskScriptCompilationResult ProcessScript(
        string sourceText,
        IReadOnlyDictionary<string, object?>? parameterValues = null)
    {
        // Parse
        var parseResult = TaskScriptParser.Parse(sourceText);
        if (!parseResult.Success || parseResult.Definition is null)
        {
            return new TaskScriptCompilationResult(null, parseResult.Diagnostics);
        }

        // Validate
        var validationResult = TaskScriptValidator.Validate(parseResult.Definition);
        if (!validationResult.IsValid)
        {
            var allDiagnostics = parseResult.Diagnostics
                .Concat(validationResult.Diagnostics)
                .ToList();
            return new TaskScriptCompilationResult(null, allDiagnostics);
        }

        // Compile
        var compilationResult = TaskScriptCompiler.Compile(parseResult.Definition, parameterValues);

        // Merge diagnostics
        var finalDiagnostics = parseResult.Diagnostics
            .Concat(validationResult.Diagnostics)
            .Concat(compilationResult.Diagnostics)
            .ToList();

        return new TaskScriptCompilationResult(compilationResult.Plan, finalDiagnostics);
    }

    /// <summary>
    /// Parse a task script without validation or compilation.
    /// </summary>
    public static TaskScriptParseResult Parse(string sourceText)
    {
        return TaskScriptParser.Parse(sourceText);
    }

    /// <summary>
    /// Validate an already-parsed definition.
    /// </summary>
    public static TaskScriptValidationResult Validate(Models.TaskScriptDefinition definition)
    {
        return TaskScriptValidator.Validate(definition);
    }

    /// <summary>
    /// Compile a validated definition into an executable plan.
    /// </summary>
    public static TaskScriptCompilationResult Compile(
        Models.TaskScriptDefinition definition,
        IReadOnlyDictionary<string, object?>? parameterValues = null)
    {
        return TaskScriptCompiler.Compile(definition, parameterValues);
    }
}
