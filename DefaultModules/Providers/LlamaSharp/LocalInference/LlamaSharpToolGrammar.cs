using System.Text;
using System.Text.Json;
using SharpClaw.Modules.Providers.LlamaSharp.Clients;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Modules.Providers.LlamaSharp.LocalInference;

/// <summary>
/// Builds the GBNF grammar string that constrains LLamaSharp inference
/// to the SharpClaw tool-call envelope:
/// <code>
/// { "mode": "message",     "text": "prose",  "calls": [] }
/// { "mode": "tool_calls",  "text": "",        "calls": [{ "id": "…", "name": "…", "args": {} }] }
/// { "mode": "refusal",     "text": "reason", "calls": [] }   // when refusal mode enabled
/// </code>
/// Applied via <c>DefaultSamplingPipeline.Grammar</c> to guarantee
/// that the model always emits well-formed JSON in this shape,
/// regardless of quantization level.
/// <para>
/// Phase 2/3 — the grammar is specialised at build time according to
/// the caller's <see cref="ToolChoice"/>, <c>parallel_tool_calls</c>
/// policy, and (when strict mode is enabled) the per-tool JSON
/// argument schemas. Reference implementation: llama.cpp's
/// <c>common/chat.cpp::build_grammar_from_tools</c>.
/// </para>
/// </summary>
internal static class LlamaSharpToolGrammar
{
    private static readonly string _defaultGrammar =
        BuildGrammar(ToolChoice.Auto, parallelCalls: true, allowRefusal: false);

    /// <summary>
    /// Returns the default (auto / parallel-enabled / no-refusal) grammar. Cached.
    /// </summary>
    public static string Build() => _defaultGrammar;

    /// <summary>
    /// Returns the grammar for the requested tool-selection policy.
    /// <para>
    /// When <paramref name="choice"/> is <see cref="ToolChoiceMode.Named"/>
    /// the <c>name</c> terminal is pinned to a string literal so the
    /// sampler cannot produce a different function. When
    /// <paramref name="parallelCalls"/> is <see langword="false"/> the
    /// <c>calls</c> array is restricted to exactly one element.
    /// </para>
    /// </summary>
    public static string Build(ToolChoice choice, bool parallelCalls = true, bool allowRefusal = false)
    {
        if (choice.Mode == ToolChoiceMode.Auto && parallelCalls && !allowRefusal)
            return _defaultGrammar;

        return BuildGrammar(choice, parallelCalls, allowRefusal);
    }

    /// <summary>
    /// Strict-mode overload: composes a per-tool grammar where each
    /// tool's <c>call-obj</c> branch pins the function name literal and
    /// substitutes an argument rule derived from
    /// <see cref="ChatToolDefinition.ParametersSchema"/>.
    /// <para>
    /// A tool whose schema cannot be mechanically converted degrades to
    /// the permissive generic <c>obj</c> rule for that tool only, so a
    /// single un-schematizable tool can never break the rest of the
    /// call surface. When <paramref name="strict"/> is
    /// <see langword="false"/> or <paramref name="tools"/> is empty,
    /// the non-strict overload is used.
    /// </para>
    /// </summary>
    public static string Build(
        ToolChoice choice,
        bool parallelCalls,
        IReadOnlyList<ChatToolDefinition> tools,
        bool strict,
        bool allowRefusal = false)
    {
        if (!strict || tools is null || tools.Count == 0)
            return Build(choice, parallelCalls, allowRefusal);

        return BuildStrictGrammar(choice, parallelCalls, tools, allowRefusal);
    }

    private static string BuildGrammar(ToolChoice choice, bool parallelCalls, bool allowRefusal)
    {
        // Mode alternatives.
        var allowMessage   = choice.Mode is ToolChoiceMode.Auto or ToolChoiceMode.None;
        var allowToolCalls = choice.Mode is ToolChoiceMode.Auto or ToolChoiceMode.Required or ToolChoiceMode.Named;

        // Refusal is only meaningful when message mode is allowed: if the
        // caller forces a tool call (Required / Named), a refusal branch
        // would contradict the choice and invite the model to sidestep it.
        var refusalActive = allowRefusal && allowMessage;

        string modeVal = BuildModeVal(allowMessage, allowToolCalls, refusalActive);

        // Calls array shape.
        var singleCallOnly = !parallelCalls || choice.Mode == ToolChoiceMode.Named;
        string callsArr = BuildCallsArr(choice, parallelCalls, singleCallOnly);

        // Name terminal — literal when a function is pinned.
        string nameTerminal = choice.Mode == ToolChoiceMode.Named
            ? $"\"\\\"{EscapeForJsonInsideGbnf(choice.NamedFunction!)}\\\"\""
            : "string";

        var sb = new StringBuilder();
        AppendRootAndMode(sb, modeVal, callsArr);
        // Single-line rule body — see AppendRootAndMode for why.
        sb.Append(
            "call-obj  ::= \"{\" ws " +
            "\"\\\"id\\\"\" ws \":\" ws string \",\" ws " +
            $"\"\\\"name\\\"\" ws \":\" ws {nameTerminal} \",\" ws " +
            "\"\\\"args\\\"\" ws \":\" ws obj " +
            "\"}\"\n\n");
        AppendJsonPrimitives(sb);
        return sb.ToString();
    }

    private static string BuildStrictGrammar(
        ToolChoice choice,
        bool parallelCalls,
        IReadOnlyList<ChatToolDefinition> tools,
        bool allowRefusal)
    {
        var allowMessage   = choice.Mode is ToolChoiceMode.Auto or ToolChoiceMode.None;
        var allowToolCalls = choice.Mode is ToolChoiceMode.Auto or ToolChoiceMode.Required or ToolChoiceMode.Named;
        var refusalActive  = allowRefusal && allowMessage;

        string modeVal = BuildModeVal(allowMessage, allowToolCalls, refusalActive);
        var singleCallOnly = !parallelCalls || choice.Mode == ToolChoiceMode.Named;
        string callsArr = BuildCallsArr(choice, parallelCalls, singleCallOnly);

        // Build per-tool fragments. When Named is pinned, we only need
        // the single branch the caller asked for; skip other tools.
        var fragments = new List<(string Name, string ArgsRule, string SchemaBody)>(tools.Count);
        for (int j = 0; j < tools.Count; j++)
        {
            var tool = tools[j];
            if (choice.Mode == ToolChoiceMode.Named
                && !string.Equals(tool.Name, choice.NamedFunction, StringComparison.Ordinal))
            {
                continue;
            }

            var prefix = $"t{j}-";
            if (tool.ParametersSchema.ValueKind == JsonValueKind.Object
                && LlamaSharpJsonSchemaConverter.TryConvertFragment(
                    tool.ParametersSchema, prefix,
                    out var topRule, out var body, out _))
            {
                fragments.Add((tool.Name, topRule, body));
            }
            else
            {
                // Unschematizable: fall back to generic obj for this tool only.
                fragments.Add((tool.Name, "obj", string.Empty));
            }
        }

        // Defensive: if every tool degraded and we had no branches to emit,
        // fall back to the non-strict grammar rather than emitting an empty
        // alternation (which is not valid GBNF).
        if (fragments.Count == 0)
            return BuildGrammar(choice, parallelCalls, allowRefusal);

        var sb = new StringBuilder();
        AppendRootAndMode(sb, modeVal, callsArr);

        sb.Append("call-obj  ::= ");
        for (int j = 0; j < fragments.Count; j++)
        {
            // Single-line alternation list — see BuildCallsArr for why.
            if (j > 0) sb.Append(" | ");
            sb.Append($"call-obj-{j}");
        }
        sb.Append("\n\n");

        for (int j = 0; j < fragments.Count; j++)
        {
            var (name, argsRule, _) = fragments[j];
            var literal = EscapeForJsonInsideGbnf(name);
            // Single-line rule body — see AppendRootAndMode for why.
            sb.Append(
                $"call-obj-{j} ::= \"{{\" ws " +
                "\"\\\"id\\\"\" ws \":\" ws string \",\" ws " +
                $"\"\\\"name\\\"\" ws \":\" ws \"\\\"{literal}\\\"\" \",\" ws " +
                $"\"\\\"args\\\"\" ws \":\" ws {argsRule} " +
                "\"}\"\n\n");
        }

        foreach (var (_, _, schemaBody) in fragments)
        {
            if (!string.IsNullOrEmpty(schemaBody))
            {
                sb.Append(schemaBody);
                if (!schemaBody.EndsWith('\n')) sb.Append('\n');
            }
        }

        AppendJsonPrimitives(sb);
        return sb.ToString();
    }

    // ── Shared emitters ────────────────────────────────────────────

    private static string BuildModeVal(bool allowMessage, bool allowToolCalls, bool allowRefusal)
    {
        var branches = new List<string>(3);
        if (allowMessage)   branches.Add("\"\\\"message\\\"\"");
        if (allowToolCalls) branches.Add("\"\\\"tool_calls\\\"\"");
        if (allowRefusal)   branches.Add("\"\\\"refusal\\\"\"");

        if (branches.Count == 0)
            throw new InvalidOperationException("ToolChoice produced an unreachable grammar.");

        return string.Join(" | ", branches);
    }

    private static string BuildCallsArr(ToolChoice choice, bool parallelCalls, bool singleCallOnly) =>
        // All bodies are emitted on a single physical line. llama.cpp's GBNF
        // parser does not reliably treat whitespace-then-'|' on a new line as
        // a continuation of the previous rule's alternation; see bug #4 in
        // docs/internal/local-inference-pipeline-debug-report.md.
        choice.Mode switch
        {
            // None: must be empty — enforced also when refusal mode applies
            // because refusal, like message, carries no calls.
            ToolChoiceMode.None =>
                "\"[\" ws \"]\"",
            // Auto: allow empty (message/refusal branch) or a populated array.
            ToolChoiceMode.Auto when parallelCalls =>
                "\"[\" ws \"]\" | \"[\" ws call-obj ( ws \",\" ws call-obj )* ws \"]\"",
            ToolChoiceMode.Auto =>
                "\"[\" ws \"]\" | \"[\" ws call-obj ws \"]\"",
            _ when singleCallOnly =>
                "\"[\" ws call-obj ws \"]\"",
            _ =>
                "\"[\" ws call-obj ( ws \",\" ws call-obj )* ws \"]\"",
        };

    private static void AppendRootAndMode(StringBuilder sb, string modeVal, string callsArr)
    {
        // The root rule is emitted on a single physical line. llama.cpp's GBNF
        // parser (see llama-grammar.cpp::parse_rule) only treats a new line as
        // a continuation of the previous rule when that line begins with an
        // alternation bar '|'. Split-across-lines sequences terminate the rule
        // at the first newline, which made the parser try to read "text" as a
        // new rule LHS and fail with: expecting name at "\"text\"" ws ":" ws string.
        // See docs/internal/local-inference-pipeline-debug-report.md bug #4.
        sb.Append(
            "root   ::= \"{\" ws " +
            "\"\\\"mode\\\"\" ws \":\" ws mode-val \",\" ws " +
            "\"\\\"text\\\"\" ws \":\" ws string \",\" ws " +
            "\"\\\"calls\\\"\" ws \":\" ws calls-arr " +
            "\"}\"\n\n");
        sb.Append($"mode-val ::= {modeVal}\n\n");
        sb.Append($"calls-arr ::= {callsArr}\n\n");
    }

    private static void AppendJsonPrimitives(StringBuilder sb)
    {
        sb.Append("# ── JSON primitives ──────────────────────────────────────────\n\n");
        // Single-line alternations — see bug #4 and BuildCallsArr.
        sb.Append("obj    ::= \"{\" ws \"}\" | \"{\" ws kv-pair ( ws \",\" ws kv-pair )* ws \"}\"\n\n");
        sb.Append("kv-pair ::= string ws \":\" ws value\n\n");
        sb.Append("value  ::= string | number | obj | arr | \"true\" | \"false\" | \"null\"\n\n");
        sb.Append("arr    ::= \"[\" ws \"]\" | \"[\" ws value ( ws \",\" ws value )* ws \"]\"\n\n");
        sb.Append("string ::= \"\\\"\" char* \"\\\"\"\n\n");
        sb.Append("char   ::= [^\"\\\\] | \"\\\\\" ( \"\\\"\" | \"\\\\\" | \"/\" | \"b\" | \"f\" | \"n\" | \"r\" | \"t\" ) | \"\\\\\" \"u\" [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F]\n\n");
        sb.Append("number ::= \"-\"? ( \"0\" | [1-9] [0-9]* ) ( \".\" [0-9]+ )? ( [eE] [+-]? [0-9]+ )?\n\n");
        sb.Append("integer ::= \"-\"? ( \"0\" | [1-9] [0-9]* )\n\n");
        sb.Append("boolean ::= \"true\" | \"false\"\n\n");
        sb.Append("null-lit ::= \"null\"\n\n");
        sb.Append("object ::= obj\n\n");
        sb.Append("object-kv ::= kv-pair\n\n");
        sb.Append("array ::= arr\n\n");
        sb.Append("ws     ::= [ \\t\\n\\r]*\n");
    }

    /// <summary>
    /// Escapes a function name so it can be embedded inside a GBNF
    /// literal that itself represents a JSON-quoted string. Allows
    /// ASCII letters/digits/underscore/hyphen/dot; rejects anything
    /// else because OpenAI tool names already constrain to that set.
    /// </summary>
    private static string EscapeForJsonInsideGbnf(string name)
    {
        foreach (var ch in name)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.'))
                throw new ArgumentException(
                    $"Invalid character '{ch}' in function name '{name}' — tool-choice Named requires a plain [A-Za-z0-9_.-] identifier.",
                    nameof(name));
        }
        return name;
    }
}

