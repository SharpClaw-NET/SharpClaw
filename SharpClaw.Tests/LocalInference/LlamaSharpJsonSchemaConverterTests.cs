using System.Reflection;
using System.Text.Json;
using LLama.Sampling;
using SharpClaw.Application.Core.Clients;
using SharpClaw.Application.Core.LocalInference;

namespace SharpClaw.Tests.LocalInference;

// ═══════════════════════════════════════════════════════════════════════
// LlamaSharpJsonSchemaConverter — semantic unit tests
// ═══════════════════════════════════════════════════════════════════════

[TestFixture]
public class LlamaSharpJsonSchemaConverterTests
{
    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement;

    [SetUp]
    public void ResetCache() => LlamaSharpJsonSchemaConverter.ResetCache();

    // ── Root handling ──────────────────────────────────────────────────

    [Test]
    public void NonObjectRoot_IsRejected_With_Pointer_Entry()
    {
        var schema = Parse("""[1, 2, 3]""");

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(
            schema, out var gbnf, out var unsupported);

        ok.Should().BeFalse();
        gbnf.Should().BeEmpty();
        unsupported.Should().ContainSingle();
        unsupported[0].Should().StartWith("/");
    }

    [Test]
    public void EmptyObject_Schema_Produces_Grammar()
    {
        var schema = Parse("""{ }""");

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(
            schema, out var gbnf, out _);

        ok.Should().BeTrue();
        gbnf.Should().Contain("root ::= ws");
        gbnf.Should().Contain("value");
    }

    // ── Object shape ───────────────────────────────────────────────────

    [Test]
    public void ObjectSchema_With_Properties_Emits_Named_Rules()
    {
        var schema = Parse("""
            {
              "type": "object",
              "properties": {
                "name": { "type": "string" },
                "age":  { "type": "integer" }
              },
              "required": ["name"]
            }
            """);

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(
            schema, out var gbnf, out var unsupported);

        ok.Should().BeTrue();
        gbnf.Should().Contain("\\\"name\\\"");
        gbnf.Should().Contain("\\\"age\\\"");
        gbnf.Should().Contain("integer");
        gbnf.Should().Contain("string");
        unsupported.Should().BeEmpty();
    }

    [Test]
    public void ObjectSchema_With_AdditionalProperties_False_Closes_Object()
    {
        var schema = Parse("""
            {
              "type": "object",
              "properties": { "x": { "type": "string" } },
              "required": ["x"],
              "additionalProperties": false
            }
            """);

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(
            schema, out var gbnf, out var unsupported);

        ok.Should().BeTrue();
        gbnf.Should().Contain("\\\"x\\\"");
        unsupported.Should().BeEmpty();
    }

    [Test]
    public void ObjectSchema_With_AdditionalProperties_Subschema_Emits_Extra_Kv_Rule()
    {
        var schema = Parse("""
            {
              "type": "object",
              "properties": { "x": { "type": "string" } },
              "required": ["x"],
              "additionalProperties": { "type": "number" }
            }
            """);

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(
            schema, out var gbnf, out _);

        ok.Should().BeTrue();
        gbnf.Should().Contain("number");
    }

    // ── Array shape ────────────────────────────────────────────────────

    [Test]
    public void ArraySchema_Homogeneous_Items()
    {
        var schema = Parse("""
            { "type": "array", "items": { "type": "string" } }
            """);

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(
            schema, out var gbnf, out _);

        ok.Should().BeTrue();
        gbnf.Should().Contain("string");
        gbnf.Should().Contain("\"[\"");
    }

    [Test]
    public void ArraySchema_Tuple_Items()
    {
        var schema = Parse("""
            {
              "type": "array",
              "items": [
                { "type": "string" },
                { "type": "integer" }
              ]
            }
            """);

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(
            schema, out var gbnf, out _);

        ok.Should().BeTrue();
        gbnf.Should().Contain("string");
        gbnf.Should().Contain("integer");
    }

    [Test]
    public void ArraySchema_MinItems_MaxItems_Honoured()
    {
        var schema = Parse("""
            { "type": "array", "items": { "type": "integer" },
              "minItems": 2, "maxItems": 3 }
            """);

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(
            schema, out var gbnf, out var unsupported);

        ok.Should().BeTrue();
        unsupported.Should().BeEmpty();
        gbnf.Should().Contain("integer");
    }

    [Test]
    public void ArraySchema_LargeMinItems_Relaxed()
    {
        var schema = Parse("""
            { "type": "array", "items": { "type": "integer" }, "minItems": 100 }
            """);

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(
            schema, out _, out var unsupported);

        ok.Should().BeTrue();
        unsupported.Should().Contain(e => e.Contains("minItems"));
    }

    // ── String, number, boolean, null ──────────────────────────────────

    [Test]
    public void StringType_With_No_Constraints_Uses_Primitive()
    {
        var schema = Parse("""{ "type": "string" }""");

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(
            schema, out var gbnf, out _);

        ok.Should().BeTrue();
        gbnf.Should().Contain("string");
    }

    [Test]
    public void StringType_With_Simple_Pattern_Is_Converted()
    {
        var schema = Parse("""{ "type": "string", "pattern": "^foo$" }""");

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(
            schema, out var gbnf, out var unsupported);

        ok.Should().BeTrue();
        unsupported.Should().BeEmpty();
        gbnf.Should().Contain("\"f\" \"o\" \"o\"");
    }

    [Test]
    public void StringType_With_Unsupported_Pattern_Tracks_Unsupported()
    {
        // Backreferences are outside the supported regex subset.
        var schema = Parse("""{ "type": "string", "pattern": "^(a)\\1$" }""");

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(
            schema, out _, out var unsupported);

        ok.Should().BeTrue();
        unsupported.Should().Contain(e => e.Contains("pattern"));
    }

    [Test]
    public void IntegerType_Enforced_Range_Yields_Literal_Alternation()
    {
        var schema = Parse("""
            { "type": "integer", "minimum": 1, "maximum": 5 }
            """);

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(
            schema, out var gbnf, out var unsupported);

        ok.Should().BeTrue();
        unsupported.Should().BeEmpty();
        gbnf.Should().Contain("\"1\"");
        gbnf.Should().Contain("\"5\"");
    }

    [Test]
    public void IntegerType_Wide_Range_Relaxed()
    {
        var schema = Parse("""
            { "type": "integer", "minimum": -100000, "maximum": 100000 }
            """);

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(
            schema, out _, out var unsupported);

        ok.Should().BeTrue();
        unsupported.Should().Contain(e => e.Contains("minimum") || e.Contains("maximum"));
    }

    [Test]
    public void BooleanType_Uses_Primitive()
    {
        var schema = Parse("""{ "type": "boolean" }""");

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(schema, out var gbnf, out _);

        ok.Should().BeTrue();
        gbnf.Should().Contain("boolean");
    }

    [Test]
    public void NullType_Uses_Primitive()
    {
        var schema = Parse("""{ "type": "null" }""");

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(schema, out var gbnf, out _);

        ok.Should().BeTrue();
        gbnf.Should().Contain("null-lit");
    }

    [Test]
    public void TypeArray_Produces_Alternation()
    {
        var schema = Parse("""{ "type": ["string", "null"] }""");

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(schema, out var gbnf, out _);

        ok.Should().BeTrue();
        gbnf.Should().Contain("string");
        gbnf.Should().Contain("null-lit");
    }

    // ── enum, const ────────────────────────────────────────────────────

    [Test]
    public void Enum_String_Values_Emits_Literal_Alternation()
    {
        var schema = Parse("""{ "enum": ["red", "green", "blue"] }""");

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(schema, out var gbnf, out _);

        ok.Should().BeTrue();
        gbnf.Should().Contain("\\\"red\\\"");
        gbnf.Should().Contain("\\\"green\\\"");
        gbnf.Should().Contain("\\\"blue\\\"");
    }

    [Test]
    public void Const_Emits_Single_Literal()
    {
        var schema = Parse("""{ "const": "fixed-value" }""");

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(schema, out var gbnf, out _);

        ok.Should().BeTrue();
        gbnf.Should().Contain("\\\"fixed-value\\\"");
    }

    [Test]
    public void Enum_Mixed_Types_Supported()
    {
        var schema = Parse("""{ "enum": ["a", 1, true, null] }""");

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(schema, out var gbnf, out _);

        ok.Should().BeTrue();
        gbnf.Should().Contain("\\\"a\\\"");
        gbnf.Should().Contain("1");
        gbnf.Should().Contain("true");
        gbnf.Should().Contain("null");
    }

    // ── Composition ────────────────────────────────────────────────────

    [Test]
    public void AnyOf_Emits_Alternation()
    {
        var schema = Parse("""
            { "anyOf": [ { "type": "string" }, { "type": "integer" } ] }
            """);

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(schema, out var gbnf, out _);

        ok.Should().BeTrue();
        gbnf.Should().Contain("string");
        gbnf.Should().Contain("integer");
    }

    [Test]
    public void OneOf_Relaxed_To_AnyOf_And_Tracked()
    {
        var schema = Parse("""
            { "oneOf": [ { "type": "string" }, { "type": "integer" } ] }
            """);

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(schema, out _, out var unsupported);

        ok.Should().BeTrue();
        unsupported.Should().Contain(e => e.Contains("oneOf"));
    }

    [Test]
    public void AllOf_Mergeable_Objects_Combined()
    {
        var schema = Parse("""
            {
              "allOf": [
                { "type": "object", "properties": { "a": { "type": "string" } }, "required": ["a"] },
                { "type": "object", "properties": { "b": { "type": "integer" } }, "required": ["b"] }
              ]
            }
            """);

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(schema, out var gbnf, out _);

        ok.Should().BeTrue();
        gbnf.Should().Contain("\\\"a\\\"");
        gbnf.Should().Contain("\\\"b\\\"");
    }

    // ── $ref / $defs

    [Test]
    public void LocalRef_To_Defs_Resolves()
    {
        var schema = Parse("""
            {
              "type": "object",
              "properties": { "item": { "$ref": "#/$defs/Item" } },
              "required": ["item"],
              "$defs": {
                "Item": { "type": "object", "properties": { "id": { "type": "integer" } }, "required": ["id"] }
              }
            }
            """);

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(schema, out var gbnf, out var unsupported);

        ok.Should().BeTrue();
        unsupported.Should().BeEmpty();
        gbnf.Should().Contain("\\\"id\\\"");
    }

    [Test]
    public void NonLocalRef_Tracked()
    {
        var schema = Parse("""
            { "$ref": "https://example.com/schema.json" }
            """);

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(schema, out _, out var unsupported);

        ok.Should().BeTrue();
        unsupported.Should().Contain(e => e.Contains("$ref"));
    }

    [Test]
    public void RecursiveRef_Compiles_Without_Stack_Overflow()
    {
        var schema = Parse("""
            {
              "$ref": "#/$defs/Node",
              "$defs": {
                "Node": {
                  "type": "object",
                  "properties": {
                    "value": { "type": "integer" },
                    "child": { "$ref": "#/$defs/Node" }
                  },
                  "required": ["value"]
                }
              }
            }
            """);

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(schema, out var gbnf, out _);

        ok.Should().BeTrue();
        gbnf.Should().Contain("\\\"value\\\"");
        gbnf.Should().Contain("\\\"child\\\"");
    }

    // ── Tracked-but-unsupported keywords (telemetry) ───────────────────

    [TestCase("""{ "type": "object", "patternProperties": { "^f": { "type": "string" } } }""", "patternProperties")]
    [TestCase("""{ "type": "object", "propertyNames": { "minLength": 1 } }""", "propertyNames")]
    [TestCase("""{ "type": "object", "minProperties": 1 }""", "minProperties")]
    [TestCase("""{ "type": "object", "maxProperties": 10 }""", "maxProperties")]
    [TestCase("""{ "type": "array", "uniqueItems": true }""", "uniqueItems")]
    [TestCase("""{ "type": "array", "contains": { "type": "integer" } }""", "contains")]
    [TestCase("""{ "not": { "type": "string" } }""", "not")]
    [TestCase("""{ "if": { "type": "string" }, "then": { "minLength": 1 } }""", "if")]
    public void Tracked_Unsupported_Keywords_Appear_In_Channel(string json, string keyword)
    {
        var schema = Parse(json);

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(schema, out _, out var unsupported);

        ok.Should().BeTrue();
        unsupported.Should().Contain(e => e.Contains(keyword));
    }

    // ── Grammar structural invariants ──────────────────────────────────

    [Test]
    public void EveryConversion_Produces_Root_Rule()
    {
        var schema = Parse("""{ "type": "object", "properties": { "x": { "type": "string" } } }""");

        LlamaSharpJsonSchemaConverter.TryConvert(schema, out var gbnf, out _);

        gbnf.Should().StartWith("root ::=");
    }

    [Test]
    public void EveryConversion_Includes_Primitive_Fragment()
    {
        var schema = Parse("""{ "type": "boolean" }""");

        LlamaSharpJsonSchemaConverter.TryConvert(schema, out var gbnf, out _);

        gbnf.Should().Contain("boolean  ::=");
        gbnf.Should().Contain("ws       ::=");
    }

    // ── Cache behaviour ────────────────────────────────────────────────

    [Test]
    public void Cache_Returns_Identical_Grammar_For_Same_Schema()
    {
        var schema = Parse("""{ "type": "object", "properties": { "x": { "type": "string" } } }""");

        LlamaSharpJsonSchemaConverter.TryConvert(schema, out var first, out _);
        LlamaSharpJsonSchemaConverter.TryConvert(schema, out var second, out _);

        first.Should().Be(second);
    }
}

// ═══════════════════════════════════════════════════════════════════════
// Phase A — dispatch wire-up reflection tests (still valid)
// ═══════════════════════════════════════════════════════════════════════

[TestFixture]
public class LocalInferenceApiClientSchemaDispatchTests
{
    private static readonly MethodInfo Resolve =
        typeof(LocalInferenceApiClient).GetMethod(
            "ResolveResponseFormatGrammar",
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static Grammar? Invoke(CompletionParameters p) =>
        (Grammar?)Resolve.Invoke(null, new object?[] { p });

    private static JsonElement ParseFormat(string json) =>
        JsonDocument.Parse(json).RootElement;

    [Test]
    public void Dispatch_JsonObject_Still_Returns_Grammar()
    {
        var p = new CompletionParameters
        {
            ResponseFormat = ParseFormat("""{ "type": "json_object" }"""),
        };

        Invoke(p).Should().NotBeNull();
    }

    [Test]
    public void Dispatch_JsonSchema_TrivialObject_Returns_Grammar()
    {
        var p = new CompletionParameters
        {
            ResponseFormat = ParseFormat("""
                {
                  "type": "json_schema",
                  "json_schema": {
                    "name": "trivial",
                    "schema": { "type": "object" }
                  }
                }
                """),
        };

        Invoke(p).Should().NotBeNull();
    }

    [Test]
    public void Dispatch_JsonSchema_NonTrivial_Converter_Succeeds()
    {
        var p = new CompletionParameters
        {
            ResponseFormat = ParseFormat("""
                {
                  "type": "json_schema",
                  "json_schema": {
                    "name": "person",
                    "schema": {
                      "type": "object",
                      "properties": { "name": { "type": "string" } },
                      "required": ["name"]
                    }
                  }
                }
                """),
        };

        Invoke(p).Should().NotBeNull();
    }

    [Test]
    public void Dispatch_JsonSchema_MissingSchema_FallsBack_Without_Throwing()
    {
        var p = new CompletionParameters
        {
            ResponseFormat = ParseFormat("""
                {
                  "type": "json_schema",
                  "json_schema": { "name": "broken" }
                }
                """),
        };

        Invoke(p).Should().NotBeNull();
    }

    [Test]
    public void Dispatch_JsonSchema_MissingWrapper_FallsBack_Without_Throwing()
    {
        var p = new CompletionParameters
        {
            ResponseFormat = ParseFormat("""{ "type": "json_schema" }"""),
        };

        Invoke(p).Should().NotBeNull();
    }

    [Test]
    public void Dispatch_Null_ResponseFormat_Returns_Null()
    {
        var p = new CompletionParameters { ResponseFormat = null };

        Invoke(p).Should().BeNull();
    }

    [Test]
    public void Dispatch_Unknown_Type_Returns_Null()
    {
        var p = new CompletionParameters
        {
            ResponseFormat = ParseFormat("""{ "type": "text" }"""),
        };

        Invoke(p).Should().BeNull();
    }
}
