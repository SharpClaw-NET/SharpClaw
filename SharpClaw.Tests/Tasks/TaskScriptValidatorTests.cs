using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Application.Infrastructure.Tasks;
using SharpClaw.Application.Infrastructure.Tasks.Models;

namespace SharpClaw.Tests.Tasks;

/// <summary>
/// Tests for task script validation: verifies that semantic rules are enforced
/// after parsing, including type constraints, duplicate declarations, loop
/// requirements, and output type cardinality.
/// </summary>
[TestFixture]
public class TaskScriptValidatorTests
{
    private static TaskScriptDefinition ParseValid(string source)
    {
        var result = TaskScriptEngine.Parse(source);
        result.Success.Should().BeTrue("the source must parse cleanly before validation");
        return result.Definition!;
    }

    // ─────────────────────────────────────────────────────────────
    // Valid scripts
    // ─────────────────────────────────────────────────────────────

    [Test]
    public void Validate_MinimalValidScript_Passes()
    {
        var source = """
[Task("minimal")]
public class MinimalTask
{
    public async Task RunAsync(CancellationToken ct)
    {
        Log("ok");
    }
}
""";
        var definition = ParseValid(source);

        var result = TaskScriptEngine.Validate(definition);

        result.IsValid.Should().BeTrue();
        result.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void Validate_AllPrimitiveParameterTypes_ArePermitted()
    {
        var source = """
[Task("primitives")]
public class PrimitivesTask
{
    public string S { get; set; } = "";
    public int I { get; set; } = 0;
    public long L { get; set; } = 0;
    public double D { get; set; } = 0;
    public decimal M { get; set; } = 0;
    public bool B { get; set; } = false;
    public Guid G { get; set; } = default;

    public async Task RunAsync(CancellationToken ct) { }
}
""";
        var definition = ParseValid(source);

        var result = TaskScriptEngine.Validate(definition);

        result.IsValid.Should().BeTrue();
    }

    // ─────────────────────────────────────────────────────────────
    // TASK101 — invalid parameter type
    // ─────────────────────────────────────────────────────────────

    [Test]
    public void Validate_UnknownParameterType_ProducesTASK101()
    {
        var source = """
[Task("bad-param")]
public class BadParamTask
{
    public SomeUnknownType Data { get; set; }

    public async Task RunAsync(CancellationToken ct) { }
}
""";
        var definition = ParseValid(source);

        var result = TaskScriptEngine.Validate(definition);

        result.IsValid.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == "TASK101");
    }

    // ─────────────────────────────────────────────────────────────
    // TASK103 — multiple [Output] types
    // ─────────────────────────────────────────────────────────────

    [Test]
    public void Validate_MultipleOutputTypes_ProducesTASK103()
    {
        // Insert two [Output]-marked nested classes directly into the definition
        // because the parser would correctly reject duplicate task classes.
        // We construct the definition manually to isolate the validator rule.
        var definition = new TaskScriptDefinition
        {
            Name = "multi-output",
            SourceText = "",
            ClassName = "MultiOutputTask",
            EntryPointMethod = "RunAsync",
            Parameters = [],
            Steps = [],
            ToolCallHooks = [],
            DataTypes =
            [
                new TaskDataTypeDefinition("ResultA", [], IsOutputType: true),
                new TaskDataTypeDefinition("ResultB", [], IsOutputType: true),
            ]
        };

        var result = TaskScriptEngine.Validate(definition);

        result.IsValid.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == "TASK103");
    }

    // ─────────────────────────────────────────────────────────────
    // TASK104 — duplicate variable declaration
    // ─────────────────────────────────────────────────────────────

    [Test]
    public void Validate_DuplicateVariableDeclaration_ProducesTASK104()
    {
        var definition = new TaskScriptDefinition
        {
            Name = "dup-var",
            SourceText = "",
            ClassName = "DupVarTask",
            EntryPointMethod = "RunAsync",
            Parameters = [],
            DataTypes = [],
            ToolCallHooks = [],
            Steps =
            [
                new TaskStepDefinition { Kind = TaskStepKind.DeclareVariable, Line = 1, Column = 0, VariableName = "x", TypeName = "string" },
                new TaskStepDefinition { Kind = TaskStepKind.DeclareVariable, Line = 2, Column = 0, VariableName = "x", TypeName = "string" },
            ]
        };

        var result = TaskScriptEngine.Validate(definition);

        result.IsValid.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == "TASK104");
    }

    // ─────────────────────────────────────────────────────────────
    // TASK105 — variable declared with invalid type
    // ─────────────────────────────────────────────────────────────

    [Test]
    public void Validate_VariableWithUnknownType_ProducesTASK105()
    {
        var definition = new TaskScriptDefinition
        {
            Name = "bad-var-type",
            SourceText = "",
            ClassName = "BadVarTypeTask",
            EntryPointMethod = "RunAsync",
            Parameters = [],
            DataTypes = [],
            ToolCallHooks = [],
            Steps =
            [
                new TaskStepDefinition { Kind = TaskStepKind.DeclareVariable, Line = 1, Column = 0, VariableName = "obj", TypeName = "WeirdType" },
            ]
        };

        var result = TaskScriptEngine.Validate(definition);

        result.IsValid.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == "TASK105");
    }

    // ─────────────────────────────────────────────────────────────
    // TASK106 — foreach loop missing iteration variable
    // ─────────────────────────────────────────────────────────────

    [Test]
    public void Validate_ForEachLoopWithNoIterationVariable_ProducesTASK106()
    {
        var definition = new TaskScriptDefinition
        {
            Name = "no-iter-var",
            SourceText = "",
            ClassName = "NoIterVarTask",
            EntryPointMethod = "RunAsync",
            Parameters = [],
            DataTypes = [],
            ToolCallHooks = [],
            Steps =
            [
                new TaskStepDefinition
                {
                    Kind = TaskStepKind.Loop,
                    Line = 1, Column = 0,
                    LoopKind = TaskLoopKind.ForEach,
                    VariableName = null,          // missing
                    Expression = "items",
                    Body = []
                }
            ]
        };

        var result = TaskScriptEngine.Validate(definition);

        result.IsValid.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == "TASK106");
    }

    // ─────────────────────────────────────────────────────────────
    // TASK107 — foreach loop missing source expression
    // ─────────────────────────────────────────────────────────────

    [Test]
    public void Validate_ForEachLoopWithNoSourceExpression_ProducesTASK107()
    {
        var definition = new TaskScriptDefinition
        {
            Name = "no-source",
            SourceText = "",
            ClassName = "NoSourceTask",
            EntryPointMethod = "RunAsync",
            Parameters = [],
            DataTypes = [],
            ToolCallHooks = [],
            Steps =
            [
                new TaskStepDefinition
                {
                    Kind = TaskStepKind.Loop,
                    Line = 1, Column = 0,
                    LoopKind = TaskLoopKind.ForEach,
                    VariableName = "item",
                    Expression = null,            // missing
                    Body = []
                }
            ]
        };

        var result = TaskScriptEngine.Validate(definition);

        result.IsValid.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == "TASK107");
    }

    // ─────────────────────────────────────────────────────────────
    // TASK108 — ParseResponse with unknown type
    // ─────────────────────────────────────────────────────────────

    [Test]
    public void Validate_ParseResponseWithUnknownType_ProducesTASK108()
    {
        var source = """
[Task("parse-bad")]
public class ParseBadTask
{
    public async Task RunAsync(CancellationToken ct)
    {
        var result = ParseResponse<Nonexistent>("{}");
        Log(result);
    }
}
""";
        var definition = ParseValid(source);

        var result = TaskScriptEngine.Validate(definition);

        result.IsValid.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == "TASK108");
    }

    // ─────────────────────────────────────────────────────────────
    // Nested validation
    // ─────────────────────────────────────────────────────────────

    [Test]
    public void Validate_InvalidTypeInsideLoopBody_IsReported()
    {
        var definition = new TaskScriptDefinition
        {
            Name = "nested-bad",
            SourceText = "",
            ClassName = "NestedBadTask",
            EntryPointMethod = "RunAsync",
            Parameters = [],
            DataTypes = [],
            ToolCallHooks = [],
            Steps =
            [
                new TaskStepDefinition
                {
                    Kind = TaskStepKind.Loop,
                    Line = 1, Column = 0,
                    LoopKind = TaskLoopKind.While,
                    Expression = "true",
                    Body =
                    [
                        new TaskStepDefinition
                        {
                            Kind = TaskStepKind.DeclareVariable,
                            Line = 2, Column = 4,
                            VariableName = "v",
                            TypeName = "UnknownNestedType"
                        }
                    ]
                }
            ]
        };

        var result = TaskScriptEngine.Validate(definition);

        result.IsValid.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == "TASK105");
    }
}
