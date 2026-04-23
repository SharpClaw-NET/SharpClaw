using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SharpClaw.Application.Infrastructure.Tasks.Models;
using System.Collections.Immutable;

namespace SharpClaw.Application.Infrastructure.Tasks.Parsing;

/// <summary>
/// Parses task script .cs files into <see cref="TaskScriptDefinition"/>.
/// Uses Roslyn to parse the C# syntax tree, then extracts the task
/// metadata, parameters, data types, and entry-point body steps.
/// <para>
/// <b>Allowed subset</b>:
/// <list type="bullet">
///   <item>One public class with <c>[Task("name")]</c> attribute</item>
///   <item>Public properties = task parameters</item>
///   <item>Nested public classes = data types</item>
///   <item>One <c>public async Task RunAsync(CancellationToken ct)</c> entry point</item>
///   <item>Restricted statement set in body (no arbitrary C# allowed)</item>
/// </list>
/// </para>
/// </summary>
public sealed class TaskScriptParser
{
    /// <summary>
    /// Parse a task script .cs file into its structured definition.
    /// </summary>
    public static TaskScriptParseResult Parse(string sourceText)
    {
        var diagnostics = new List<TaskDiagnostic>();

        // Parse with Roslyn
        var tree = CSharpSyntaxTree.ParseText(sourceText, new CSharpParseOptions(LanguageVersion.CSharp14));
        var root = (CompilationUnitSyntax)tree.GetRoot();

        // Collect Roslyn syntax errors
        foreach (var diag in tree.GetDiagnostics())
        {
            if (diag.Severity == DiagnosticSeverity.Error)
            {
                var lineSpan = diag.Location.GetLineSpan();
                diagnostics.Add(new TaskDiagnostic(
                    TaskDiagnosticSeverity.Error,
                    diag.Id,
                    diag.GetMessage(),
                    lineSpan.StartLinePosition.Line + 1,
                    lineSpan.StartLinePosition.Character));
            }
        }

        if (diagnostics.Any(d => d.Severity == TaskDiagnosticSeverity.Error))
        {
            return new TaskScriptParseResult(null, diagnostics);
        }

        // Find the task class
        var taskClass = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Modifiers.Any(SyntaxKind.PublicKeyword) &&
                                 HasTaskAttribute(c));

        if (taskClass is null)
        {
            diagnostics.Add(new TaskDiagnostic(
                TaskDiagnosticSeverity.Error,
                "TASK001",
                "No public class with [Task(\"name\")] attribute found."));
            return new TaskScriptParseResult(null, diagnostics);
        }

        // Extract task name from attribute
        var taskName = ExtractTaskName(taskClass);
        if (string.IsNullOrWhiteSpace(taskName))
        {
            diagnostics.Add(new TaskDiagnostic(
                TaskDiagnosticSeverity.Error,
                "TASK002",
                "Task attribute must specify a non-empty name: [Task(\"YourTaskName\")].",
                GetLine(taskClass)));
            return new TaskScriptParseResult(null, diagnostics);
        }

        // Extract optional description
        var description = ExtractDescription(taskClass);

        // Extract parameters (public properties on the task class)
        var parameters = ExtractParameters(taskClass, diagnostics);

        // Extract data types (nested public classes)
        var dataTypes = ExtractDataTypes(taskClass, diagnostics);

        // Find output type (data type marked with [Output] attribute)
        var outputType = dataTypes.FirstOrDefault(dt => dt.IsOutputType);

        // Extract [AgentOutput("format")] from the task class
        var agentOutputFormat = ExtractAgentOutputFormat(taskClass);

        // Extract [ToolCall("name")] methods
        var toolCallHooks = ExtractToolCallHooks(taskClass, diagnostics);

        // Find entry point: public async Task RunAsync(CancellationToken ct)
        var entryPoint = taskClass.Members
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == "RunAsync" &&
                                 m.Modifiers.Any(SyntaxKind.PublicKeyword) &&
                                 m.Modifiers.Any(SyntaxKind.AsyncKeyword));

        if (entryPoint is null)
        {
            diagnostics.Add(new TaskDiagnostic(
                TaskDiagnosticSeverity.Error,
                "TASK003",
                "Task class must have: public async Task RunAsync(CancellationToken ct)",
                GetLine(taskClass)));
            return new TaskScriptParseResult(null, diagnostics);
        }

        // Validate entry-point signature
        if (entryPoint.ParameterList.Parameters.Count != 1 ||
            entryPoint.ParameterList.Parameters[0].Type?.ToString() != "CancellationToken")
        {
            diagnostics.Add(new TaskDiagnostic(
                TaskDiagnosticSeverity.Error,
                "TASK004",
                "RunAsync must have exactly one parameter: CancellationToken ct",
                GetLine(entryPoint)));
            return new TaskScriptParseResult(null, diagnostics);
        }

        // Parse body statements
        var steps = new List<TaskStepDefinition>();
        if (entryPoint.Body is not null)
        {
            foreach (var statement in entryPoint.Body.Statements)
            {
                var step = ParseStatement(statement, diagnostics);
                if (step is not null)
                {
                    steps.Add(step);
                }
            }
        }

        var definition = new TaskScriptDefinition
        {
            Name = taskName,
            Description = description,
            SourceText = sourceText,
            ClassName = taskClass.Identifier.Text,
            EntryPointMethod = entryPoint.Identifier.Text,
            Parameters = parameters,
            DataTypes = dataTypes,
            OutputType = outputType,
            Steps = steps,
            ToolCallHooks = toolCallHooks,
            AgentOutputFormat = agentOutputFormat
        };

        return new TaskScriptParseResult(definition, diagnostics);
    }

    // ── Attribute helpers ─────────────────────────────────────────

    private static bool HasTaskAttribute(ClassDeclarationSyntax classSyntax)
    {
        return classSyntax.AttributeLists
            .SelectMany(al => al.Attributes)
            .Any(a => a.Name.ToString() is "Task" or "TaskAttribute");
    }

    private static string? ExtractTaskName(ClassDeclarationSyntax classSyntax)
    {
        var taskAttr = classSyntax.AttributeLists
            .SelectMany(al => al.Attributes)
            .FirstOrDefault(a => a.Name.ToString() is "Task" or "TaskAttribute");

        if (taskAttr?.ArgumentList?.Arguments.Count > 0)
        {
            var arg = taskAttr.ArgumentList.Arguments[0];
            if (arg.Expression is LiteralExpressionSyntax literal &&
                literal.Token.Value is string name)
            {
                return name;
            }
        }
        return null;
    }

    private static string? ExtractDescription(ClassDeclarationSyntax classSyntax)
    {
        var descAttr = classSyntax.AttributeLists
            .SelectMany(al => al.Attributes)
            .FirstOrDefault(a => a.Name.ToString() is "Description" or "DescriptionAttribute");

        if (descAttr?.ArgumentList?.Arguments.Count > 0)
        {
            var arg = descAttr.ArgumentList.Arguments[0];
            if (arg.Expression is LiteralExpressionSyntax literal &&
                literal.Token.Value is string desc)
            {
                return desc;
            }
        }
        return null;
    }

    private static string? ExtractAgentOutputFormat(ClassDeclarationSyntax classSyntax)
    {
        var attr = classSyntax.AttributeLists
            .SelectMany(al => al.Attributes)
            .FirstOrDefault(a => a.Name.ToString() is "AgentOutput" or "AgentOutputAttribute");

        if (attr?.ArgumentList?.Arguments.Count > 0 &&
            attr.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax literal &&
            literal.Token.Value is string format)
        {
            return format;
        }
        return null;
    }

    // ── [ToolCall] hook extraction ────────────────────────────────

    private static IReadOnlyList<TaskToolCallHook> ExtractToolCallHooks(
        ClassDeclarationSyntax classSyntax,
        List<TaskDiagnostic> diagnostics)
    {
        var hooks = new List<TaskToolCallHook>();

        foreach (var method in classSyntax.Members.OfType<MethodDeclarationSyntax>())
        {
            var toolCallAttr = method.AttributeLists
                .SelectMany(al => al.Attributes)
                .FirstOrDefault(a => a.Name.ToString() is "ToolCall" or "ToolCallAttribute");

            if (toolCallAttr is null)
                continue;

            // Extract tool name from attribute argument
            string? toolName = null;
            if (toolCallAttr.ArgumentList?.Arguments.Count > 0 &&
                toolCallAttr.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax nameLit &&
                nameLit.Token.Value is string name)
            {
                toolName = name;
            }

            if (string.IsNullOrWhiteSpace(toolName))
            {
                diagnostics.Add(new TaskDiagnostic(
                    TaskDiagnosticSeverity.Error,
                    "TASK020",
                    $"[ToolCall] on method '{method.Identifier.Text}' must specify a non-empty name.",
                    GetLine(method)));
                continue;
            }

            // Extract optional [Description] on the method
            var hookDescription = method.AttributeLists
                .SelectMany(al => al.Attributes)
                .Where(a => a.Name.ToString() is "Description" or "DescriptionAttribute")
                .Select(a => a.ArgumentList?.Arguments.Count > 0 &&
                    a.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax lit &&
                    lit.Token.Value is string desc ? desc : null)
                .FirstOrDefault(d => d is not null);

            // Extract parameters (skip CancellationToken)
            var parameters = new List<TaskToolCallParameter>();
            foreach (var param in method.ParameterList.Parameters)
            {
                var paramType = param.Type?.ToString() ?? "string";
                if (paramType == "CancellationToken")
                    continue;

                var paramName = param.Identifier.Text;

                // Check for [Description] on the parameter
                var paramDesc = param.AttributeLists
                    .SelectMany(al => al.Attributes)
                    .Where(a => a.Name.ToString() is "Description" or "DescriptionAttribute")
                    .Select(a => a.ArgumentList?.Arguments.Count > 0 &&
                        a.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax lit &&
                        lit.Token.Value is string d ? d : null)
                    .FirstOrDefault(d => d is not null);

                parameters.Add(new TaskToolCallParameter(paramName, paramType, paramDesc));
            }

            // Parse method body
            var body = new List<TaskStepDefinition>();
            if (method.Body is not null)
            {
                foreach (var statement in method.Body.Statements)
                {
                    var step = ParseStatement(statement, diagnostics);
                    if (step is not null)
                        body.Add(step);
                }
            }

            // Determine return variable: if the last statement is a return
            // with an expression, use that variable name.
            var returnVariable = "$return";
            if (method.Body?.Statements.LastOrDefault() is ReturnStatementSyntax returnStmt
                && returnStmt.Expression is not null)
            {
                returnVariable = returnStmt.Expression.ToString();
            }

            hooks.Add(new TaskToolCallHook
            {
                Name = toolName,
                Description = hookDescription,
                Parameters = parameters,
                Body = body,
                ReturnVariable = returnVariable
            });
        }

        return hooks;
    }

    // ── Parameter extraction ──────────────────────────────────────

    private static IReadOnlyList<TaskParameterDefinition> ExtractParameters(
        ClassDeclarationSyntax classSyntax,
        List<TaskDiagnostic> diagnostics)
    {
        var parameters = new List<TaskParameterDefinition>();

        foreach (var property in classSyntax.Members.OfType<PropertyDeclarationSyntax>())
        {
            if (!property.Modifiers.Any(SyntaxKind.PublicKeyword))
                continue;

            var name = property.Identifier.Text;
            var typeName = property.Type.ToString();

            // Check for [Description] and [DefaultValue]
            var description = ExtractPropertyDescription(property);
            var defaultValue = ExtractPropertyDefaultValue(property);

            // Required by default unless [DefaultValue] or initializer is present
            var isRequired = defaultValue is null && property.Initializer is null;

            parameters.Add(new TaskParameterDefinition(
                name,
                typeName,
                description,
                defaultValue,
                isRequired));
        }

        return parameters;
    }

    private static string? ExtractPropertyDescription(PropertyDeclarationSyntax property)
    {
        var attr = property.AttributeLists
            .SelectMany(al => al.Attributes)
            .FirstOrDefault(a => a.Name.ToString() is "Description" or "DescriptionAttribute");

        if (attr?.ArgumentList?.Arguments.Count > 0 &&
            attr.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax lit &&
            lit.Token.Value is string desc)
        {
            return desc;
        }
        return null;
    }

    private static string? ExtractPropertyDefaultValue(PropertyDeclarationSyntax property)
    {
        var attr = property.AttributeLists
            .SelectMany(al => al.Attributes)
            .FirstOrDefault(a => a.Name.ToString() is "DefaultValue" or "DefaultValueAttribute");

        if (attr?.ArgumentList?.Arguments.Count > 0)
        {
            return attr.ArgumentList.Arguments[0].Expression.ToString();
        }

        // Also check for initializer
        if (property.Initializer is not null)
        {
            return property.Initializer.Value.ToString();
        }

        return null;
    }

    // ── Data type extraction ──────────────────────────────────────

    private static IReadOnlyList<TaskDataTypeDefinition> ExtractDataTypes(
        ClassDeclarationSyntax classSyntax,
        List<TaskDiagnostic> diagnostics)
    {
        var dataTypes = new List<TaskDataTypeDefinition>();

        foreach (var nestedClass in classSyntax.Members.OfType<ClassDeclarationSyntax>())
        {
            if (!nestedClass.Modifiers.Any(SyntaxKind.PublicKeyword))
                continue;

            var name = nestedClass.Identifier.Text;
            var properties = new List<TaskPropertyDefinition>();

            foreach (var prop in nestedClass.Members.OfType<PropertyDeclarationSyntax>())
            {
                if (!prop.Modifiers.Any(SyntaxKind.PublicKeyword))
                    continue;

                var propName = prop.Identifier.Text;
                var typeName = prop.Type.ToString();
                var defaultValue = prop.Initializer?.Value.ToString();

                // Check if collection type (List<T>, IEnumerable<T>, etc.)
                var isCollection = IsCollectionType(typeName, out var elementType);

                properties.Add(new TaskPropertyDefinition(
                    propName,
                    typeName,
                    defaultValue,
                    isCollection,
                    elementType));
            }

            // Check for [Output] attribute
            var isOutput = nestedClass.AttributeLists
                .SelectMany(al => al.Attributes)
                .Any(a => a.Name.ToString() is "Output" or "OutputAttribute");

            dataTypes.Add(new TaskDataTypeDefinition(name, properties, isOutput));
        }

        return dataTypes;
    }

    private static bool IsCollectionType(string typeName, out string? elementType)
    {
        if (typeName.StartsWith("List<") ||
            typeName.StartsWith("IList<") ||
            typeName.StartsWith("IEnumerable<") ||
            typeName.StartsWith("ICollection<"))
        {
            var start = typeName.IndexOf('<') + 1;
            var end = typeName.LastIndexOf('>');
            if (end > start)
            {
                elementType = typeName.Substring(start, end - start);
                return true;
            }
        }
        elementType = null;
        return false;
    }

    // ── Statement parsing ────────────────────────────────────────

    private static TaskStepDefinition? ParseStatement(
        StatementSyntax statement,
        List<TaskDiagnostic> diagnostics)
    {
        return statement switch
        {
            LocalDeclarationStatementSyntax local => ParseLocalDeclaration(local, diagnostics),
            ExpressionStatementSyntax expr        => ParseExpressionStatement(expr, diagnostics),
            IfStatementSyntax ifStmt              => ParseIfStatement(ifStmt, diagnostics),
            WhileStatementSyntax whileStmt        => ParseWhileStatement(whileStmt, diagnostics),
            ForEachStatementSyntax forEachStmt    => ParseForEachStatement(forEachStmt, diagnostics),
            ReturnStatementSyntax ret             => ParseReturnStatement(ret),
            _                                     => UnrecognizedStatement(statement, diagnostics)
        };
    }

    // ── Local declaration ─────────────────────────────────────────

    private static TaskStepDefinition? ParseLocalDeclaration(
        LocalDeclarationStatementSyntax local,
        List<TaskDiagnostic> diagnostics)
    {
        var declarator = local.Declaration.Variables.FirstOrDefault();
        if (declarator is null)
            return null;

        var variableName = declarator.Identifier.Text;
        var typeName = local.Declaration.Type.IsVar
            ? null
            : local.Declaration.Type.ToString();
        var line = GetLine(local);
        var column = GetColumn(local);

        // No initializer – bare declaration
        if (declarator.Initializer is null)
        {
            return new TaskStepDefinition
            {
                Kind = TaskStepKind.DeclareVariable,
                Line = line,
                Column = column,
                VariableName = variableName,
                TypeName = typeName
            };
        }

        var initializer = declarator.Initializer.Value;

        // Unwrap await: var x = await SomeCall(...)
        if (UnwrapAwaitInvocation(initializer) is { } awaitedInvocation)
        {
            var apiStep = TryParseContextApiCall(awaitedInvocation, line, column);
            if (apiStep is not null)
                return apiStep with { ResultVariable = variableName };
        }

        // Non-awaited context API call: var x = FindModel(...), var x = Chat(...)
        if (initializer is InvocationExpressionSyntax directInvocation)
        {
            var apiStep = TryParseContextApiCall(directInvocation, line, column);
            if (apiStep is not null)
                return apiStep with { ResultVariable = variableName };
        }

        // Plain declaration: var x = new Foo(), var x = expr, var x = a ?? await b()
        return new TaskStepDefinition
        {
            Kind = TaskStepKind.DeclareVariable,
            Line = line,
            Column = column,
            VariableName = variableName,
            TypeName = typeName,
            Expression = initializer.ToString()
        };
    }

    // ── Expression statement ──────────────────────────────────────

    private static TaskStepDefinition? ParseExpressionStatement(
        ExpressionStatementSyntax exprStmt,
        List<TaskDiagnostic> diagnostics)
    {
        var expression = exprStmt.Expression;
        var line = GetLine(exprStmt);
        var column = GetColumn(exprStmt);

        // await ContextApiCall(...)
        if (expression is AwaitExpressionSyntax awaitExpr)
        {
            if (awaitExpr.Expression is InvocationExpressionSyntax awaitedInvocation)
            {
                var apiStep = TryParseContextApiCall(awaitedInvocation, line, column);
                if (apiStep is not null)
                    return apiStep;
            }

            diagnostics.Add(new TaskDiagnostic(
                TaskDiagnosticSeverity.Error,
                "TASK010",
                $"Unrecognized await expression: {awaitExpr.Expression}",
                line, column));
            return null;
        }

        // Non-await invocation: event handlers (OnTranscriptionSegment, OnTimer) and Log
        if (expression is InvocationExpressionSyntax invocation)
        {
            var eventStep = TryParseEventHandler(invocation, line, column, diagnostics);
            if (eventStep is not null)
                return eventStep;

            var methodName = GetMethodName(invocation);
            if (methodName == "Log")
            {
                return new TaskStepDefinition
                {
                    Kind = TaskStepKind.Log,
                    Line = line,
                    Column = column,
                    Expression = ExtractFirstArgText(invocation),
                    Arguments = ExtractArgumentTexts(invocation)
                };
            }

            // Non-awaited context API call: ChatToThread(...), CreateAgent(...), etc.
            var apiStep = TryParseContextApiCall(invocation, line, column);
            if (apiStep is not null)
                return apiStep;
        }

        // Assignment: x = ..., x.Prop += ..., etc.
        if (expression is AssignmentExpressionSyntax assignment)
        {
            return new TaskStepDefinition
            {
                Kind = TaskStepKind.Assign,
                Line = line,
                Column = column,
                VariableName = assignment.Left.ToString(),
                Expression = assignment.Right.ToString()
            };
        }

        // Fallback: arbitrary expression (e.g. list.AddRange(...))
        return new TaskStepDefinition
        {
            Kind = TaskStepKind.Evaluate,
            Line = line,
            Column = column,
            Expression = expression.ToString()
        };
    }

    // ── Control flow ──────────────────────────────────────────────

    private static TaskStepDefinition ParseIfStatement(
        IfStatementSyntax ifStmt,
        List<TaskDiagnostic> diagnostics)
    {
        var thenBody = ParseBlock(ifStmt.Statement, diagnostics);
        var elseBody = ifStmt.Else is not null
            ? ParseBlock(ifStmt.Else.Statement, diagnostics)
            : null;

        return new TaskStepDefinition
        {
            Kind = TaskStepKind.Conditional,
            Line = GetLine(ifStmt),
            Column = GetColumn(ifStmt),
            Expression = ifStmt.Condition.ToString(),
            Body = thenBody,
            ElseBody = elseBody
        };
    }

    private static TaskStepDefinition ParseWhileStatement(
        WhileStatementSyntax whileStmt,
        List<TaskDiagnostic> diagnostics)
    {
        return new TaskStepDefinition
        {
            Kind = TaskStepKind.Loop,
            Line = GetLine(whileStmt),
            Column = GetColumn(whileStmt),
            LoopKind = TaskLoopKind.While,
            Expression = whileStmt.Condition.ToString(),
            Body = ParseBlock(whileStmt.Statement, diagnostics)
        };
    }

    private static TaskStepDefinition ParseForEachStatement(
        ForEachStatementSyntax forEachStmt,
        List<TaskDiagnostic> diagnostics)
    {
        return new TaskStepDefinition
        {
            Kind = TaskStepKind.Loop,
            Line = GetLine(forEachStmt),
            Column = GetColumn(forEachStmt),
            LoopKind = TaskLoopKind.ForEach,
            VariableName = forEachStmt.Identifier.Text,
            TypeName = forEachStmt.Type.IsVar ? null : forEachStmt.Type.ToString(),
            Expression = forEachStmt.Expression.ToString(),
            Body = ParseBlock(forEachStmt.Statement, diagnostics)
        };
    }

    private static TaskStepDefinition ParseReturnStatement(ReturnStatementSyntax ret)
    {
        return new TaskStepDefinition
        {
            Kind = TaskStepKind.Return,
            Line = GetLine(ret),
            Column = GetColumn(ret)
        };
    }

    private static TaskStepDefinition? UnrecognizedStatement(
        StatementSyntax statement,
        List<TaskDiagnostic> diagnostics)
    {
        diagnostics.Add(new TaskDiagnostic(
            TaskDiagnosticSeverity.Error,
            "TASK011",
            $"Unsupported statement: {statement.Kind()}",
            GetLine(statement),
            GetColumn(statement)));
        return null;
    }

    // ── Context API call recognition ──────────────────────────────

    private static TaskStepDefinition? TryParseContextApiCall(
        InvocationExpressionSyntax invocation,
        int line,
        int column)
    {
        var methodName = GetMethodName(invocation);
        if (methodName is null)
            return null;

        // Task.Delay(...)
        if (methodName == "Delay" && IsTaskMemberAccess(invocation))
        {
            return new TaskStepDefinition
            {
                Kind = TaskStepKind.Delay,
                Line = line,
                Column = column,
                Expression = ExtractFirstArgText(invocation),
                Arguments = ExtractArgumentTexts(invocation)
            };
        }

        var kind = ResolveContextApiKind(methodName);
        if (kind is null)
            return null;

        var step = new TaskStepDefinition
        {
            Kind = kind.Value,
            Line = line,
            Column = column,
            Arguments = ExtractArgumentTexts(invocation)
        };

        // Chat / ChatStream: first arg = agentId, second arg = message text
        if (kind is TaskStepKind.Chat or TaskStepKind.ChatStream &&
            invocation.ArgumentList.Arguments.Count >= 2)
        {
            step = step with
            {
                Expression = invocation.ArgumentList.Arguments[1].Expression.ToString()
            };
        }

        // ParseResponse<T>: capture the generic type argument
        if (kind is TaskStepKind.ParseResponse)
        {
            var typeArg = GetGenericTypeArgument(invocation);
            if (typeArg is not null)
                step = step with { TypeName = typeArg };
        }

        // HttpGet/Post/Put/Delete: capture the HTTP verb + URL expression
        if (kind is TaskStepKind.HttpRequest)
        {
            step = step with
            {
                HttpMethod = ResolveHttpMethod(methodName),
                Expression = ExtractFirstArgText(invocation)
            };
        }

        // Single-arg context methods: store first arg as Expression
        if (kind is TaskStepKind.Emit or TaskStepKind.Log or
            TaskStepKind.StartTranscription or TaskStepKind.StopTranscription or
            TaskStepKind.WaitUntilStopped or
            TaskStepKind.FindModel or TaskStepKind.FindProvider or TaskStepKind.FindAgent or
            TaskStepKind.CreateThread)
        {
            step = step with { Expression = ExtractFirstArgText(invocation) };
        }

        // CreateAgent: first arg = name, second arg = modelId
        if (kind is TaskStepKind.CreateAgent &&
            invocation.ArgumentList.Arguments.Count >= 2)
        {
            step = step with
            {
                Expression = invocation.ArgumentList.Arguments[0].Expression.ToString()
            };
        }

        // ChatToThread: first arg = threadId, second arg = message, optional third = agentId
        if (kind is TaskStepKind.ChatToThread &&
            invocation.ArgumentList.Arguments.Count >= 2)
        {
            step = step with
            {
                Expression = invocation.ArgumentList.Arguments[1].Expression.ToString()
            };
        }

        return step;
    }

    // ── Event handler parsing ─────────────────────────────────────

    private static TaskStepDefinition? TryParseEventHandler(
        InvocationExpressionSyntax invocation,
        int line,
        int column,
        List<TaskDiagnostic> diagnostics)
    {
        var methodName = GetMethodName(invocation);
        var triggerKind = ResolveEventTrigger(methodName);
        if (triggerKind is null)
            return null;

        // Non-lambda arguments (e.g. the job variable reference)
        var nonLambdaArgs = invocation.ArgumentList.Arguments
            .Where(a => a.Expression is not
                (ParenthesizedLambdaExpressionSyntax or SimpleLambdaExpressionSyntax))
            .Select(a => a.Expression.ToString())
            .ToList();

        // Find the lambda argument (parenthesized or simple form)
        string? handlerParam = null;
        IReadOnlyList<TaskStepDefinition>? body = null;

        var parenLambda = invocation.ArgumentList.Arguments
            .Select(a => a.Expression)
            .OfType<ParenthesizedLambdaExpressionSyntax>()
            .FirstOrDefault();

        if (parenLambda is not null)
        {
            handlerParam = parenLambda.ParameterList.Parameters
                .FirstOrDefault()?.Identifier.Text;
            body = ParseLambdaBody(parenLambda.Body, diagnostics);
        }
        else
        {
            var simpleLambda = invocation.ArgumentList.Arguments
                .Select(a => a.Expression)
                .OfType<SimpleLambdaExpressionSyntax>()
                .FirstOrDefault();

            if (simpleLambda is not null)
            {
                handlerParam = simpleLambda.Parameter.Identifier.Text;
                body = ParseLambdaBody(simpleLambda.Body, diagnostics);
            }
        }

        return new TaskStepDefinition
        {
            Kind = TaskStepKind.EventHandler,
            Line = line,
            Column = column,
            TriggerKind = triggerKind.Value,
            HandlerParameter = handlerParam,
            Arguments = nonLambdaArgs,
            Body = body ?? []
        };
    }

    private static IReadOnlyList<TaskStepDefinition> ParseLambdaBody(
        CSharpSyntaxNode body,
        List<TaskDiagnostic> diagnostics)
    {
        if (body is BlockSyntax block)
            return ParseStatements(block.Statements, diagnostics);

        // Expression-bodied lambda → single Evaluate step
        return
        [
            new TaskStepDefinition
            {
                Kind = TaskStepKind.Evaluate,
                Line = GetLine(body),
                Column = GetColumn(body),
                Expression = body.ToString()
            }
        ];
    }

    // ── Block / statement-list helpers ─────────────────────────────

    private static IReadOnlyList<TaskStepDefinition> ParseBlock(
        StatementSyntax statement,
        List<TaskDiagnostic> diagnostics)
    {
        if (statement is BlockSyntax block)
            return ParseStatements(block.Statements, diagnostics);

        var step = ParseStatement(statement, diagnostics);
        return step is not null ? [step] : [];
    }

    private static IReadOnlyList<TaskStepDefinition> ParseStatements(
        SyntaxList<StatementSyntax> statements,
        List<TaskDiagnostic> diagnostics)
    {
        var steps = new List<TaskStepDefinition>();
        foreach (var stmt in statements)
        {
            var step = ParseStatement(stmt, diagnostics);
            if (step is not null)
                steps.Add(step);
        }
        return steps;
    }

    // ── Syntax extraction helpers ─────────────────────────────────

    private static string? GetMethodName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            IdentifierNameSyntax id          => id.Identifier.Text,
            GenericNameSyntax generic         => generic.Identifier.Text,
            MemberAccessExpressionSyntax mem => mem.Name switch
            {
                IdentifierNameSyntax id  => id.Identifier.Text,
                GenericNameSyntax generic => generic.Identifier.Text,
                _                        => null
            },
            _ => null
        };
    }

    private static string? GetGenericTypeArgument(InvocationExpressionSyntax invocation)
    {
        var genericName = invocation.Expression switch
        {
            GenericNameSyntax g              => g,
            MemberAccessExpressionSyntax m   => m.Name as GenericNameSyntax,
            _                                => null
        };

        return genericName?.TypeArgumentList.Arguments.Count > 0
            ? genericName.TypeArgumentList.Arguments[0].ToString()
            : null;
    }

    private static bool IsTaskMemberAccess(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression is MemberAccessExpressionSyntax member &&
               member.Expression.ToString() == "Task";
    }

    private static InvocationExpressionSyntax? UnwrapAwaitInvocation(ExpressionSyntax expression)
    {
        if (expression is AwaitExpressionSyntax awaitExpr &&
            awaitExpr.Expression is InvocationExpressionSyntax invocation)
        {
            return invocation;
        }
        return null;
    }

    private static IReadOnlyList<string> ExtractArgumentTexts(InvocationExpressionSyntax invocation)
    {
        return invocation.ArgumentList.Arguments
            .Where(a => a.Expression is not
                (ParenthesizedLambdaExpressionSyntax or SimpleLambdaExpressionSyntax))
            .Select(a => a.Expression.ToString())
            .ToList();
    }

    private static string? ExtractFirstArgText(InvocationExpressionSyntax invocation)
    {
        return invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString();
    }

    // ── Lookup tables ─────────────────────────────────────────────

    private static TaskStepKind? ResolveContextApiKind(string methodName) => methodName switch
    {
        "Chat"                  => TaskStepKind.Chat,
        "ChatStream"            => TaskStepKind.ChatStream,
        "StartTranscription"    => TaskStepKind.StartTranscription,
        "StopTranscription"     => TaskStepKind.StopTranscription,
        "GetDefaultInputAudio" => TaskStepKind.GetDefaultInputAudio,
        "Emit"                  => TaskStepKind.Emit,
        "ParseResponse"         => TaskStepKind.ParseResponse,
        "WaitUntilStopped"      => TaskStepKind.WaitUntilStopped,
        "Log"                   => TaskStepKind.Log,
        "HttpGet" or "HttpPost" or "HttpPut" or "HttpDelete"
                                => TaskStepKind.HttpRequest,
        "FindModel"             => TaskStepKind.FindModel,
        "FindProvider"          => TaskStepKind.FindProvider,
        "FindAgent"             => TaskStepKind.FindAgent,
        "CreateAgent"           => TaskStepKind.CreateAgent,
        "CreateThread"          => TaskStepKind.CreateThread,
        "ChatToThread"          => TaskStepKind.ChatToThread,
        _                       => null
    };

    private static TaskTriggerKind? ResolveEventTrigger(string? methodName) => methodName switch
    {
        "OnTranscriptionSegment" => TaskTriggerKind.TranscriptionSegment,
        "OnTimer"                => TaskTriggerKind.Timer,
        _                        => null
    };

    private static string? ResolveHttpMethod(string methodName) => methodName switch
    {
        "HttpGet"    => "GET",
        "HttpPost"   => "POST",
        "HttpPut"    => "PUT",
        "HttpDelete" => "DELETE",
        _            => null
    };

    // ── Position helpers ──────────────────────────────────────────

    private static int GetLine(SyntaxNode node)
    {
        var lineSpan = node.GetLocation().GetLineSpan();
        return lineSpan.StartLinePosition.Line + 1;
    }

    private static int GetColumn(SyntaxNode node)
    {
        var lineSpan = node.GetLocation().GetLineSpan();
        return lineSpan.StartLinePosition.Character;
    }
}
