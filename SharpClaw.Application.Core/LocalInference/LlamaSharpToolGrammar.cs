namespace SharpClaw.Application.Core.LocalInference;

/// <summary>
/// Builds the GBNF grammar string that constrains LLamaSharp inference
/// to the SharpClaw tool-call envelope:
/// <code>
/// { "mode": "message",     "text": "prose",  "calls": [] }
/// { "mode": "tool_calls",  "text": "",        "calls": [{ "id": "…", "name": "…", "args": {} }] }
/// </code>
/// Applied via <c>DefaultSamplingPipeline.Grammar</c> to guarantee
/// that the model always emits well-formed JSON in this shape,
/// regardless of quantization level.
/// </summary>
internal static class LlamaSharpToolGrammar
{
    private static readonly string _grammar = BuildGrammar();

    /// <summary>
    /// Returns the GBNF grammar string for the envelope.
    /// The result is computed once and cached.
    /// </summary>
    public static string Build() => _grammar;

    private static string BuildGrammar() =>
        """
        root   ::= "{" ws "\"mode\"" ws ":" ws mode-val "," ws
                       "\"text\"" ws ":" ws string     "," ws
                       "\"calls\"" ws ":" ws calls-arr
                   "}"

        mode-val ::= "\"message\"" | "\"tool_calls\""

        calls-arr ::= "[" ws "]"
                    | "[" ws call-obj ( ws "," ws call-obj )* ws "]"

        call-obj  ::= "{" ws "\"id\""   ws ":" ws string "," ws
                           "\"name\""  ws ":" ws string "," ws
                           "\"args\""  ws ":" ws obj
                       "}"

        # ── JSON primitives ──────────────────────────────────────────

        obj    ::= "{" ws "}"
                 | "{" ws kv-pair ( ws "," ws kv-pair )* ws "}"

        kv-pair ::= string ws ":" ws value

        value  ::= string | number | obj | arr | "true" | "false" | "null"

        arr    ::= "[" ws "]"
                 | "[" ws value ( ws "," ws value )* ws "]"

        string ::= "\"" char* "\""

        char   ::= [^"\\]
                 | "\\" ( "\"" | "\\" | "/" | "b" | "f" | "n" | "r" | "t" )
                 | "\\" "u" [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F]

        number ::= "-"? ( "0" | [1-9] [0-9]* ) ( "." [0-9]+ )? ( [eE] [+-]? [0-9]+ )?

        ws     ::= [ \t\n\r]*
        """;
}
