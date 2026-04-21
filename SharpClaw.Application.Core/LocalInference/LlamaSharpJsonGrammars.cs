namespace SharpClaw.Application.Core.LocalInference;

/// <summary>
/// Generic JSON GBNF grammars used when a caller sets
/// <see cref="Clients.CompletionParameters.ResponseFormat"/> on the
/// LlamaSharp provider.
/// <para>
/// The <see cref="JsonObject"/> grammar constrains the model to emit a
/// single well-formed JSON value (object, array, string, number, boolean,
/// or <c>null</c>) — matching OpenAI's <c>{"type":"json_object"}</c>
/// semantics. The structured <c>json_schema</c> form is handled by
/// <see cref="LlamaSharpJsonSchemaConverter"/>, which consumes
/// <see cref="BuildJsonValueGrammarFragment"/> as its primitive rule
/// block and falls back to <see cref="JsonObject"/> when a schema
/// contains features outside the converter's coverage matrix.
/// </para>
/// <para>
/// Applied via <c>DefaultSamplingPipeline.Grammar</c>. Only used in the
/// plain chat path; tool-calling paths always use
/// <see cref="LlamaSharpToolGrammar"/>, which already produces JSON.
/// </para>
/// </summary>
internal static class LlamaSharpJsonGrammars
{
    private static readonly string _jsonObject = BuildJsonObjectGrammar();
    private static readonly string _jsonValueFragment = BuildJsonValueFragment();

    /// <summary>
    /// GBNF grammar that accepts any well-formed JSON value. Cached.
    /// </summary>
    public static string JsonObject() => _jsonObject;

    /// <summary>
    /// Reusable JSON primitive rule block used by
    /// <see cref="LlamaSharpJsonSchemaConverter"/>. Contains no
    /// <c>root</c> rule — the converter prepends its own. Rules defined:
    /// <c>value</c>, <c>object</c>, <c>object-kv</c>, <c>array</c>,
    /// <c>string</c>, <c>char</c>, <c>number</c>, <c>integer</c>,
    /// <c>boolean</c>, <c>null-lit</c>, <c>ws</c>.
    /// </summary>
    public static string BuildJsonValueGrammarFragment() => _jsonValueFragment;

    private static string BuildJsonObjectGrammar() =>
        """
        root   ::= ws value ws

        value  ::= obj | arr | string | number | "true" | "false" | "null"

        obj    ::= "{" ws "}"
                 | "{" ws kv-pair ( ws "," ws kv-pair )* ws "}"

        kv-pair ::= string ws ":" ws value

        arr    ::= "[" ws "]"
                 | "[" ws value ( ws "," ws value )* ws "]"

        string ::= "\"" char* "\""

        char   ::= [^"\\]
                 | "\\" ( "\"" | "\\" | "/" | "b" | "f" | "n" | "r" | "t" )
                 | "\\" "u" [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F]

        number ::= "-"? ( "0" | [1-9] [0-9]* ) ( "." [0-9]+ )? ( [eE] [+-]? [0-9]+ )?

        ws     ::= [ \t\n\r]*
        """;

    private static string BuildJsonValueFragment() =>
        """
        value    ::= object | array | string | number | boolean | null-lit
        object   ::= "{" ws "}" | "{" ws object-kv ( ws "," ws object-kv )* ws "}"
        object-kv ::= string ws ":" ws value
        array    ::= "[" ws "]" | "[" ws value ( ws "," ws value )* ws "]"
        string   ::= "\"" char* "\""
        char     ::= [^"\\]
                   | "\\" ( "\"" | "\\" | "/" | "b" | "f" | "n" | "r" | "t" )
                   | "\\" "u" [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F]
        number   ::= "-"? ( "0" | [1-9] [0-9]* ) ( "." [0-9]+ )? ( [eE] [+-]? [0-9]+ )?
        integer  ::= "-"? ( "0" | [1-9] [0-9]* )
        boolean  ::= "true" | "false"
        null-lit ::= "null"
        ws       ::= [ \t\n\r]*
        """;
}
