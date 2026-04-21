using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SharpClaw.Application.Core.LocalInference;

/// <summary>
/// Converts a JSON Schema document into a GBNF grammar that
/// <see cref="LLama.Sampling.Grammar"/> can apply via
/// <see cref="LLama.Sampling.DefaultSamplingPipeline.Grammar"/>.
/// <para>
/// SharpClaw port of llama.cpp's reference <c>json-schema-to-grammar</c>.
/// Covers the OpenAI strict-mode structured-output profile plus a
/// pragmatic superset of common non-strict keywords. See
/// <c>docs/internal/llamasharp-json-schema-plan.md</c> for the full
/// coverage matrix.
/// </para>
/// <para>
/// Callers on the tool-calling paths must <em>not</em> consult this
/// converter: the tool envelope grammar
/// (<see cref="LlamaSharpToolGrammar"/>) always takes precedence.
/// </para>
/// </summary>
internal static class LlamaSharpJsonSchemaConverter
{
    /// <summary>Hard cap on rules per conversion. Pathologically large
    /// schemas are forced to fall back to the generic JSON grammar.</summary>
    private const int MaxRulesPerConversion = 256;

    /// <summary>Cap on optional-property cartesian enumeration before
    /// collapsing to "required-first, optional-any-order".</summary>
    private const int OptionalCartesianCap = 4;

    /// <summary>Small-integer window within which numeric range
    /// constraints are enforced via literal alternation.</summary>
    private const int NumericRangeWindowMin = -9999;
    private const int NumericRangeWindowMax = 9999;
    private const int NumericRangeMaxCount = 64;

    /// <summary>Small-string length window within which minLength/maxLength
    /// are enforced via character-class repetition.</summary>
    private const int StringLengthWindowMax = 64;

    /// <summary>Array repetition relax threshold. Lower bounds above
    /// this collapse to <c>+</c>.</summary>
    private const int ArrayLowerBoundCap = 8;

    /// <summary>LRU cache of converted grammars keyed on the SHA-256 of
    /// the schema's minified JSON. Bounded so steady-state memory stays
    /// flat regardless of unique-schema churn.</summary>
    private static readonly ConcurrentDictionary<string, CacheEntry> _grammarCache = new();
    private static long _cacheTick;
    private const int CacheCapacity = 256;

    private sealed record CacheEntry(bool Success, string Grammar, string[] Unsupported, long LastUsed);

    /// <summary>
    /// Attempts to convert <paramref name="schema"/> into a GBNF grammar
    /// whose root rule is named <c>root</c>.
    /// </summary>
    public static bool TryConvert(
        JsonElement schema,
        out string grammar,
        out IReadOnlyList<string> unsupportedKeywords)
    {
        grammar = string.Empty;
        unsupportedKeywords = Array.Empty<string>();

        if (schema.ValueKind != JsonValueKind.Object)
        {
            unsupportedKeywords = new[] { "/ (non-object schema root)" };
            return false;
        }

        var cacheKey = ComputeCacheKey(schema);
        if (_grammarCache.TryGetValue(cacheKey, out var cached))
        {
            _grammarCache[cacheKey] = cached with { LastUsed = Interlocked.Increment(ref _cacheTick) };
            unsupportedKeywords = cached.Unsupported;
            if (cached.Success)
            {
                grammar = cached.Grammar;
                return true;
            }
            return false;
        }

        var walker = new SchemaWalker(schema, prefix: string.Empty);
        var ok = walker.TryBuild(out var built, out var unsupportedList);
        var unsupportedArr = unsupportedList.ToArray();

        _grammarCache[cacheKey] = new CacheEntry(
            ok, ok ? built : string.Empty, unsupportedArr,
            Interlocked.Increment(ref _cacheTick));
        TrimCache();

        unsupportedKeywords = unsupportedArr;
        if (!ok) return false;

        grammar = built;
        return true;
    }

    /// <summary>
    /// Attempts to convert <paramref name="schema"/> into a GBNF
    /// <em>fragment</em> whose rules are prefixed with
    /// <paramref name="prefix"/> so the result can be spliced into a
    /// larger composite grammar (per-tool strict-mode arguments).
    /// <para>
    /// The returned <paramref name="rulesBody"/> contains only the rule
    /// lines produced by this schema — no <c>root ::=</c> line and no
    /// shared JSON primitives block. The caller is responsible for
    /// emitting the primitives fragment (<see cref="LlamaSharpJsonGrammars.BuildJsonValueGrammarFragment"/>)
    /// exactly once for the combined grammar, and for wiring
    /// <paramref name="topRuleName"/> into its own root.
    /// </para>
    /// </summary>
    public static bool TryConvertFragment(
        JsonElement schema,
        string prefix,
        out string topRuleName,
        out string rulesBody,
        out IReadOnlyList<string> unsupportedKeywords)
    {
        topRuleName = string.Empty;
        rulesBody = string.Empty;
        unsupportedKeywords = Array.Empty<string>();

        if (schema.ValueKind != JsonValueKind.Object)
        {
            unsupportedKeywords = new[] { "/ (non-object schema root)" };
            return false;
        }

        var walker = new SchemaWalker(schema, prefix ?? string.Empty);
        if (!walker.TryBuildFragment(out var top, out var body, out var unsupportedList))
        {
            unsupportedKeywords = unsupportedList.ToArray();
            return false;
        }

        topRuleName = top;
        rulesBody = body;
        unsupportedKeywords = unsupportedList.ToArray();
        return true;
    }

    private static void TrimCache()
    {
        if (_grammarCache.Count <= CacheCapacity) return;
        var oldest = _grammarCache
            .OrderBy(kvp => kvp.Value.LastUsed)
            .Take(_grammarCache.Count - CacheCapacity)
            .Select(kvp => kvp.Key)
            .ToArray();
        foreach (var key in oldest)
            _grammarCache.TryRemove(key, out _);
    }

    private static string ComputeCacheKey(JsonElement schema)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            schema.WriteTo(writer);
        }
        var bytes = SHA256.HashData(ms.ToArray());
        return Convert.ToHexString(bytes);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SchemaWalker — owns mutable state for a single conversion call.
    // ═══════════════════════════════════════════════════════════════════
    private sealed class SchemaWalker
    {
        private readonly JsonElement _root;
        private readonly string _prefix;
        private readonly Dictionary<string, JsonElement> _defs = new(StringComparer.Ordinal);
        private readonly List<(string Name, string Body)> _rules = new();
        private readonly Dictionary<string, string> _nameByContentHash = new(StringComparer.Ordinal);
        private readonly HashSet<string> _reservedNames = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _refToRuleName = new(StringComparer.Ordinal);
        private readonly List<string> _unsupported = new();
        private readonly HashSet<string> _unsupportedSet = new(StringComparer.Ordinal);
        private bool _ruleCountExceeded;
        private bool _needsJsonValueFragment;

        public SchemaWalker(JsonElement root, string prefix = "")
        {
            _root = root;
            _prefix = prefix ?? string.Empty;
            if (root.TryGetProperty("$defs", out var d) && d.ValueKind == JsonValueKind.Object)
                foreach (var p in d.EnumerateObject()) _defs[p.Name] = p.Value;
            if (root.TryGetProperty("definitions", out var d2) && d2.ValueKind == JsonValueKind.Object)
                foreach (var p in d2.EnumerateObject()) _defs[p.Name] = p.Value;

            foreach (var reserved in new[]
            {
                "root", "ws", "value", "object", "object-kv", "array",
                "string", "char", "number", "integer", "boolean", "null-lit",
            })
            {
                _reservedNames.Add(reserved);
            }
        }

        public bool TryBuild(out string grammar, out IReadOnlyList<string> unsupported)
        {
            grammar = string.Empty;
            unsupported = _unsupported;
            try
            {
                var topRule = Visit(_root, "/", "top");
                if (_ruleCountExceeded || topRule is null) return false;

                var sb = new StringBuilder();
                sb.AppendLine($"root ::= ws {topRule} ws");
                foreach (var (name, body) in _rules)
                    sb.AppendLine($"{name} ::= {body}");

                // Primitives are always appended so rules that reference
                // value/string/number/etc. compile regardless of order.
                sb.AppendLine(LlamaSharpJsonGrammars.BuildJsonValueGrammarFragment());

                grammar = sb.ToString();
                return true;
            }
            catch (ConversionAbortedException)
            {
                return false;
            }
        }

        /// <summary>
        /// Builds the rule body without emitting a <c>root</c> line or
        /// the shared primitives fragment. Used when the result will
        /// be spliced into a composite grammar whose caller already
        /// owns a single <c>root</c> and appends the primitives once.
        /// </summary>
        public bool TryBuildFragment(
            out string topRuleName,
            out string rulesBody,
            out IReadOnlyList<string> unsupported)
        {
            topRuleName = string.Empty;
            rulesBody = string.Empty;
            unsupported = _unsupported;
            try
            {
                var topRule = Visit(_root, "/", "top");
                if (_ruleCountExceeded || topRule is null) return false;

                var sb = new StringBuilder();
                foreach (var (name, body) in _rules)
                    sb.AppendLine($"{name} ::= {body}");

                topRuleName = topRule;
                rulesBody = sb.ToString();
                return true;
            }
            catch (ConversionAbortedException)
            {
                return false;
            }
        }

        private sealed class ConversionAbortedException : Exception { }

        // ───────────────────────────────────────────────────────────────
        // Visit: returns the rule name for a subschema, emitting rules
        // as needed. `pointer` is the JSON-pointer location used for
        // unsupported-keyword diagnostics. `hint` is a rule-name seed.
        // ───────────────────────────────────────────────────────────────
        private string Visit(JsonElement schema, string pointer, string hint)
        {
            if (_rules.Count >= MaxRulesPerConversion)
            {
                _ruleCountExceeded = true;
                throw new ConversionAbortedException();
            }

            if (schema.ValueKind == JsonValueKind.True)
                return EmitPrimitiveRef("value");
            if (schema.ValueKind == JsonValueKind.False)
            {
                TrackUnsupported($"{pointer} (false schema)");
                return EmitPrimitiveRef("value");
            }
            if (schema.ValueKind != JsonValueKind.Object)
            {
                TrackUnsupported($"{pointer} (non-object subschema)");
                return EmitPrimitiveRef("value");
            }

            // $ref short-circuits everything else (spec-mandated).
            if (schema.TryGetProperty("$ref", out var refProp) &&
                refProp.ValueKind == JsonValueKind.String)
            {
                return VisitRef(refProp.GetString()!, pointer);
            }

            // enum / const produce literal alternations at any level.
            if (schema.TryGetProperty("const", out var constEl))
                return EmitEnum(new[] { constEl }, hint);
            if (schema.TryGetProperty("enum", out var enumEl) &&
                enumEl.ValueKind == JsonValueKind.Array)
                return EmitEnum(enumEl.EnumerateArray().ToArray(), hint);

            // Composition.
            if (schema.TryGetProperty("anyOf", out var anyOfEl) &&
                anyOfEl.ValueKind == JsonValueKind.Array)
                return EmitUnion(anyOfEl, pointer, hint, isOneOf: false);
            if (schema.TryGetProperty("oneOf", out var oneOfEl) &&
                oneOfEl.ValueKind == JsonValueKind.Array)
            {
                TrackUnsupported($"{pointer}/oneOf (relaxed to anyOf)");
                return EmitUnion(oneOfEl, pointer, hint, isOneOf: true);
            }
            if (schema.TryGetProperty("allOf", out var allOfEl) &&
                allOfEl.ValueKind == JsonValueKind.Array)
            {
                var merged = TryMergeAllOf(allOfEl, pointer);
                if (merged.HasValue)
                    return Visit(merged.Value, pointer, hint);
                TrackUnsupported($"{pointer}/allOf (unmergeable; using first branch)");
                var first = allOfEl.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object)
                    return Visit(first, pointer + "/allOf/0", hint);
                return EmitPrimitiveRef("value");
            }

            // Tracked-but-unsupported composition keywords.
            if (schema.TryGetProperty("not", out _)) TrackUnsupported($"{pointer}/not");
            if (schema.TryGetProperty("if", out _)) TrackUnsupported($"{pointer}/if");
            if (schema.TryGetProperty("then", out _)) TrackUnsupported($"{pointer}/then");
            if (schema.TryGetProperty("else", out _)) TrackUnsupported($"{pointer}/else");

            // Type-driven dispatch.
            if (schema.TryGetProperty("type", out var typeEl))
            {
                if (typeEl.ValueKind == JsonValueKind.String)
                    return VisitTyped(schema, typeEl.GetString()!, pointer, hint);

                if (typeEl.ValueKind == JsonValueKind.Array)
                {
                    var branches = new List<string>();
                    foreach (var t in typeEl.EnumerateArray())
                    {
                        if (t.ValueKind != JsonValueKind.String) continue;
                        branches.Add(VisitTyped(schema, t.GetString()!, pointer, hint));
                    }
                    if (branches.Count == 0)
                        return EmitPrimitiveRef("value");
                    return EmitAlternationRule(branches, hint);
                }

                TrackUnsupported($"{pointer}/type (invalid kind)");
            }

            // No type, no composition, no enum → any JSON value.
            return EmitPrimitiveRef("value");
        }

        // ───────────────────────────────────────────────────────────────
        // Type-specific emitters.
        // ───────────────────────────────────────────────────────────────
        private string VisitTyped(JsonElement schema, string type, string pointer, string hint) =>
            type switch
            {
                "object" => EmitObject(schema, pointer, hint),
                "array" => EmitArray(schema, pointer, hint),
                "string" => EmitString(schema, pointer, hint),
                "number" => EmitNumber(schema, pointer, hint, integerOnly: false),
                "integer" => EmitNumber(schema, pointer, hint, integerOnly: true),
                "boolean" => EmitPrimitiveRef("boolean"),
                "null" => EmitPrimitiveRef("null-lit"),
                _ => TrackUnknownType(pointer, type),
            };

        private string TrackUnknownType(string pointer, string type)
        {
            TrackUnsupported($"{pointer}/type ({type})");
            return EmitPrimitiveRef("value");
        }

        private string EmitObject(JsonElement schema, string pointer, string hint)
        {
            TrackObjectUnsupported(schema, pointer);

            var properties = ReadProperties(schema);
            var required = ReadRequired(schema);
            var (additionalAllowed, additionalSchema) = ReadAdditional(schema);

            // Empty object (no props declared; no constraints) → open object.
            if (properties.Count == 0)
            {
                if (additionalAllowed == false)
                {
                    return EmitNamedRule(hint + "-empty-obj", "\"{\" ws \"}\"");
                }
                return EmitPrimitiveRef("object");
            }

            // Emit a rule per property value.
            var kvRuleNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, sub) in properties)
            {
                var valueRule = Visit(sub, $"{pointer}/properties/{name}", hint + "-" + Sanitize(name));
                var kvRule = EmitNamedRule(
                    hint + "-kv-" + Sanitize(name),
                    $"{FormatStringLiteral(name)} ws \":\" ws {valueRule}");
                kvRuleNames[name] = kvRule;
            }

            var reqNames = required.Where(r => kvRuleNames.ContainsKey(r)).ToList();
            var optNames = properties.Select(kv => kv.Key)
                .Where(p => !reqNames.Contains(p)).ToList();

            string? extraKvRule = null;
            if (additionalAllowed == true && additionalSchema is null)
            {
                extraKvRule = EmitPrimitiveRef("object-kv");
            }
            else if (additionalSchema.HasValue)
            {
                var extraValue = Visit(additionalSchema.Value,
                    $"{pointer}/additionalProperties", hint + "-addl");
                extraKvRule = EmitNamedRule(
                    hint + "-addl-kv",
                    $"string ws \":\" ws {extraValue}");
            }

            return EmitObjectShapeRule(
                kvRuleNames, reqNames, optNames, extraKvRule, hint);
        }

        private string EmitObjectShapeRule(
            Dictionary<string, string> kvRuleNames,
            List<string> reqNames,
            List<string> optNames,
            string? extraKvRule,
            string hint)
        {
            var sb = new StringBuilder();

            if (reqNames.Count == 0 && optNames.Count == 0)
            {
                sb.Append("\"{\" ws \"}\"");
                if (extraKvRule is not null)
                    sb.Append($" | \"{{\" ws {extraKvRule} ( ws \",\" ws {extraKvRule} )* ws \"}}\"");
                return EmitNamedRule(hint + "-obj", sb.ToString());
            }

            var requiredPart = string.Join(" ws \",\" ws ",
                reqNames.Select(n => kvRuleNames[n]));

            // Collect candidates for the optional tail.
            var tailCandidates = optNames.Select(n => kvRuleNames[n]).ToList();
            if (extraKvRule is not null && !tailCandidates.Contains(extraKvRule))
                tailCandidates.Add(extraKvRule);

            if (reqNames.Count > 0 && tailCandidates.Count == 0)
            {
                return EmitNamedRule(hint + "-obj", $"\"{{\" ws {requiredPart} ws \"}}\"");
            }

            if (reqNames.Count == 0 && tailCandidates.Count > 0)
            {
                var optAlt = BuildAlternation(tailCandidates);
                var optKvRule = EmitNamedRule(hint + "-opt-kv", optAlt);
                return EmitNamedRule(
                    hint + "-obj",
                    $"\"{{\" ws \"}}\" | \"{{\" ws {optKvRule} ( ws \",\" ws {optKvRule} )* ws \"}}\"");
            }

            // Required + optional tail.
            var tailAlt = BuildAlternation(tailCandidates);
            var tailKvRule = EmitNamedRule(hint + "-tail-kv", tailAlt);
            return EmitNamedRule(
                hint + "-obj",
                $"\"{{\" ws {requiredPart} ( ws \",\" ws {tailKvRule} )* ws \"}}\"");
        }

        private string EmitArray(JsonElement schema, string pointer, string hint)
        {
            TrackArrayUnsupported(schema, pointer);

            // Tuple form: items is an array of subschemas.
            if (schema.TryGetProperty("items", out var itemsEl) &&
                itemsEl.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                var i = 0;
                foreach (var sub in itemsEl.EnumerateArray())
                {
                    parts.Add(Visit(sub, $"{pointer}/items/{i}", hint + $"-i{i}"));
                    i++;
                }
                if (parts.Count == 0)
                    return EmitPrimitiveRef("array");
                var tupleBody = "\"[\" ws " +
                    string.Join(" ws \",\" ws ", parts) +
                    " ws \"]\"";
                return EmitNamedRule(hint + "-arr", tupleBody);
            }

            string itemRule = "value";
            if (schema.TryGetProperty("items", out var singleItems) &&
                singleItems.ValueKind == JsonValueKind.Object)
            {
                itemRule = Visit(singleItems, $"{pointer}/items", hint + "-item");
            }

            var min = TryGetInt(schema, "minItems");
            var max = TryGetInt(schema, "maxItems");
            if (min.HasValue && min.Value > ArrayLowerBoundCap)
            {
                TrackUnsupported($"{pointer}/minItems (> {ArrayLowerBoundCap}; relaxed)");
                min = null;
            }

            string body;
            if (!min.HasValue && !max.HasValue)
            {
                body =
                    $"\"[\" ws \"]\" | \"[\" ws {itemRule} ( ws \",\" ws {itemRule} )* ws \"]\"";
            }
            else
            {
                var lo = min ?? 0;
                if (max.HasValue && max.Value < lo) max = lo;
                if (lo == 0 && max is null)
                {
                    body =
                        $"\"[\" ws \"]\" | \"[\" ws {itemRule} ( ws \",\" ws {itemRule} )* ws \"]\"";
                }
                else
                {
                    var inner = new StringBuilder();
                    if (lo == 0)
                    {
                        inner.Append("\"[\" ws \"]\" | \"[\" ws ");
                        inner.Append(itemRule);
                        if (max.HasValue && max.Value > 1)
                            inner.Append($" ( ws \",\" ws {itemRule} ){{0,{max.Value - 1}}}");
                        else if (!max.HasValue)
                            inner.Append($" ( ws \",\" ws {itemRule} )*");
                        inner.Append(" ws \"]\"");
                    }
                    else
                    {
                        inner.Append("\"[\" ws ");
                        inner.Append(itemRule);
                        for (int k = 1; k < lo; k++)
                            inner.Append($" ws \",\" ws {itemRule}");
                        if (max.HasValue)
                        {
                            var extra = max.Value - lo;
                            if (extra > 0)
                                inner.Append($" ( ws \",\" ws {itemRule} ){{0,{extra}}}");
                        }
                        else
                        {
                            inner.Append($" ( ws \",\" ws {itemRule} )*");
                        }
                        inner.Append(" ws \"]\"");
                    }
                    body = inner.ToString();
                }
            }

            return EmitNamedRule(hint + "-arr", body);
        }

        private string EmitString(JsonElement schema, string pointer, string hint)
        {
            // Phase 5 — honour a supported `format` keyword by emitting
            // a specialised rule from LlamaSharpStringFormatGrammars.
            // Unsupported formats still track as unsupported so the
            // conversion report explains which keys were ignored.
            if (schema.TryGetProperty("format", out var fmt) &&
                fmt.ValueKind == JsonValueKind.String &&
                fmt.GetString() is { Length: > 0 } fmtName)
            {
                var fragment = LlamaSharpStringFormatGrammars.TryGet(fmtName);
                if (fragment is not null)
                {
                    foreach (var (helperHint, helperBody) in fragment.Helpers)
                        EmitNamedRule(helperHint, helperBody);
                    return EmitNamedRule(hint + "-" + Sanitize(fmtName), fragment.TopBody);
                }
                TrackUnsupported($"{pointer}/format ({fmtName})");
            }

            // Pattern keyword: pass through to a trivial-regex probe. If
            // the pattern can be turned into a safe, anchored regular
            // grammar, we use it; otherwise fall back to the generic
            // string primitive and record as unsupported.
            if (schema.TryGetProperty("pattern", out var patternEl) &&
                patternEl.ValueKind == JsonValueKind.String &&
                patternEl.GetString() is { Length: > 0 } pattern)
            {
                if (LlamaSharpRegexToGrammar.TryConvert(pattern, out var patternBody))
                    return EmitNamedRule(hint + "-pat", patternBody);
                TrackUnsupported($"{pointer}/pattern");
            }

            var min = TryGetInt(schema, "minLength") ?? 0;
            var max = TryGetInt(schema, "maxLength");

            if (min == 0 && !max.HasValue)
                return EmitPrimitiveRef("string");

            if (max.HasValue && max.Value > StringLengthWindowMax)
            {
                TrackUnsupported($"{pointer}/maxLength (> {StringLengthWindowMax}; relaxed)");
                max = null;
            }
            if (min > StringLengthWindowMax)
            {
                TrackUnsupported($"{pointer}/minLength (> {StringLengthWindowMax}; relaxed)");
                min = 0;
            }

            if (min == 0 && !max.HasValue)
                return EmitPrimitiveRef("string");

            // Repeat char class between bounds.
            var body = max.HasValue
                ? $"\"\\\"\" char{{{min},{max.Value}}} \"\\\"\""
                : $"\"\\\"\" char{{{min},}} \"\\\"\"";

            return EmitNamedRule(hint + "-str", body);
        }

        private string EmitNumber(JsonElement schema, string pointer, string hint, bool integerOnly)
        {
            if (schema.TryGetProperty("multipleOf", out _))
                TrackUnsupported($"{pointer}/multipleOf");
            if (schema.TryGetProperty("exclusiveMinimum", out _))
                TrackUnsupported($"{pointer}/exclusiveMinimum");
            if (schema.TryGetProperty("exclusiveMaximum", out _))
                TrackUnsupported($"{pointer}/exclusiveMaximum");

            var min = TryGetInt(schema, "minimum");
            var max = TryGetInt(schema, "maximum");

            var basePrim = integerOnly ? "integer" : "number";

            if (!integerOnly || !min.HasValue || !max.HasValue)
            {
                if (min.HasValue || max.HasValue)
                    TrackUnsupported($"{pointer}/minimum|maximum (not enforced)");
                return EmitPrimitiveRef(basePrim);
            }

            // Integer, finite bounds.
            if (min.Value < NumericRangeWindowMin || max.Value > NumericRangeWindowMax ||
                max.Value < min.Value ||
                (max.Value - min.Value + 1) > NumericRangeMaxCount)
            {
                TrackUnsupported($"{pointer}/minimum|maximum (range too wide; relaxed)");
                return EmitPrimitiveRef(basePrim);
            }

            var literals = new List<string>();
            for (long v = min.Value; v <= max.Value; v++)
                literals.Add($"\"{v.ToString(CultureInfo.InvariantCulture)}\"");
            return EmitNamedRule(hint + "-int-range", string.Join(" | ", literals));
        }

        private string EmitEnum(JsonElement[] values, string hint)
        {
            if (values.Length == 0) return EmitPrimitiveRef("value");
            var literals = values.Select(v => JsonLiteralToGbnf(v)).ToList();
            return EmitNamedRule(hint + "-enum", string.Join(" | ", literals));
        }

        private string EmitUnion(JsonElement array, string pointer, string hint, bool isOneOf)
        {
            var parts = new List<string>();
            var i = 0;
            foreach (var sub in array.EnumerateArray())
            {
                parts.Add(Visit(sub, $"{pointer}/{(isOneOf ? "oneOf" : "anyOf")}/{i}", hint + $"-u{i}"));
                i++;
            }
            if (parts.Count == 0) return EmitPrimitiveRef("value");
            return EmitAlternationRule(parts, hint);
        }

        private string VisitRef(string refPath, string pointer)
        {
            if (!refPath.StartsWith("#/", StringComparison.Ordinal))
            {
                TrackUnsupported($"{pointer}/$ref ({refPath}; non-local)");
                return EmitPrimitiveRef("value");
            }

            if (_refToRuleName.TryGetValue(refPath, out var existing))
                return existing;

            if (!TryResolveLocalRef(refPath, out var target, out var defName))
            {
                TrackUnsupported($"{pointer}/$ref ({refPath}; unresolved)");
                return EmitPrimitiveRef("value");
            }

            // Pre-register the rule name so recursive refs resolve.
            var ruleName = AllocateRuleName("def-" + Sanitize(defName));
            _refToRuleName[refPath] = ruleName;

            // Visit target and replace the slot contents.
            var innerName = Visit(target!.Value, refPath, "def-" + Sanitize(defName));
            // Body is an alias alternation with a single branch.
            AddOrAliasRule(ruleName, innerName);
            return ruleName;
        }

        private bool TryResolveLocalRef(string refPath, out JsonElement? target, out string defName)
        {
            target = null;
            defName = "ref";
            // Supported shapes: #/$defs/Name and #/definitions/Name.
            const string defsPrefix = "#/$defs/";
            const string definitionsPrefix = "#/definitions/";
            string? name = null;
            if (refPath.StartsWith(defsPrefix, StringComparison.Ordinal))
                name = refPath.Substring(defsPrefix.Length);
            else if (refPath.StartsWith(definitionsPrefix, StringComparison.Ordinal))
                name = refPath.Substring(definitionsPrefix.Length);

            if (name is null) return false;
            if (!_defs.TryGetValue(name, out var el)) return false;
            target = el;
            defName = name;
            return true;
        }

        // ───────────────────────────────────────────────────────────────
        // allOf shallow merge — only for the common "object with
        // combined properties/required" pattern.
        // ───────────────────────────────────────────────────────────────
        private JsonElement? TryMergeAllOf(JsonElement allOfEl, string pointer)
        {
            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var required = new HashSet<string>(StringComparer.Ordinal);
            var sawObject = false;

            foreach (var branch in allOfEl.EnumerateArray())
            {
                if (branch.ValueKind != JsonValueKind.Object) return null;
                if (branch.TryGetProperty("$ref", out _)) return null;
                if (branch.TryGetProperty("anyOf", out _) ||
                    branch.TryGetProperty("oneOf", out _) ||
                    branch.TryGetProperty("allOf", out _) ||
                    branch.TryGetProperty("not", out _)) return null;

                if (branch.TryGetProperty("type", out var t) &&
                    t.ValueKind == JsonValueKind.String &&
                    !string.Equals(t.GetString(), "object", StringComparison.Ordinal))
                    return null;

                sawObject = true;

                if (branch.TryGetProperty("properties", out var p) &&
                    p.ValueKind == JsonValueKind.Object)
                    foreach (var pp in p.EnumerateObject())
                        properties[pp.Name] = pp.Value;

                if (branch.TryGetProperty("required", out var r) &&
                    r.ValueKind == JsonValueKind.Array)
                    foreach (var rr in r.EnumerateArray())
                        if (rr.ValueKind == JsonValueKind.String)
                            required.Add(rr.GetString()!);
            }

            if (!sawObject) return null;

            // Synthesize a merged object JSON and reparse.
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("type", "object");
                w.WritePropertyName("properties");
                w.WriteStartObject();
                foreach (var (k, v) in properties)
                {
                    w.WritePropertyName(k);
                    v.WriteTo(w);
                }
                w.WriteEndObject();
                if (required.Count > 0)
                {
                    w.WritePropertyName("required");
                    w.WriteStartArray();
                    foreach (var name in required) w.WriteStringValue(name);
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            var doc = JsonDocument.Parse(ms.ToArray());
            return doc.RootElement.Clone();
        }

        // ───────────────────────────────────────────────────────────────
        // Rule emission helpers.
        // ───────────────────────────────────────────────────────────────
        private string EmitPrimitiveRef(string primitive)
        {
            _needsJsonValueFragment = true;
            return primitive;
        }

        private string EmitAlternationRule(IList<string> branches, string hint)
        {
            if (branches.Count == 1) return branches[0];
            var body = BuildAlternation(branches);
            return EmitNamedRule(hint + "-alt", body);
        }

        private static string BuildAlternation(IEnumerable<string> branches) =>
            string.Join(" | ", branches);

        private string EmitNamedRule(string hint, string body)
        {
            var hash = HashBody(body);
            if (_nameByContentHash.TryGetValue(hash, out var existing))
                return existing;

            var name = AllocateRuleName(hint);
            _rules.Add((name, body));
            _nameByContentHash[hash] = name;

            if (_rules.Count >= MaxRulesPerConversion)
            {
                _ruleCountExceeded = true;
                throw new ConversionAbortedException();
            }
            return name;
        }

        private void AddOrAliasRule(string name, string targetRuleName)
        {
            _rules.Add((name, targetRuleName));
        }

        private string AllocateRuleName(string hint)
        {
            var candidate = Sanitize(hint);
            if (string.IsNullOrEmpty(candidate)) candidate = "rule";
            if (!string.IsNullOrEmpty(_prefix))
                candidate = _prefix + candidate;
            if (!_reservedNames.Contains(candidate) && !NameInUse(candidate))
            {
                _reservedNames.Add(candidate);
                return candidate;
            }
            var i = 2;
            while (true)
            {
                var next = $"{candidate}-{i}";
                if (!_reservedNames.Contains(next) && !NameInUse(next))
                {
                    _reservedNames.Add(next);
                    return next;
                }
                i++;
            }
        }

        private bool NameInUse(string name)
        {
            foreach (var (n, _) in _rules)
                if (string.Equals(n, name, StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (char.IsAsciiLetterOrDigit(ch))
                    sb.Append(char.ToLowerInvariant(ch));
                else
                    sb.Append('-');
            }
            var result = sb.ToString().Trim('-');
            while (result.Contains("--", StringComparison.Ordinal))
                result = result.Replace("--", "-", StringComparison.Ordinal);
            return result;
        }

        private static string HashBody(string body)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(body));
            return Convert.ToHexString(bytes);
        }

        private void TrackUnsupported(string pointer)
        {
            if (_unsupportedSet.Add(pointer))
                _unsupported.Add(pointer);
        }

        private void TrackObjectUnsupported(JsonElement schema, string pointer)
        {
            if (schema.TryGetProperty("patternProperties", out _))
                TrackUnsupported($"{pointer}/patternProperties");
            if (schema.TryGetProperty("propertyNames", out _))
                TrackUnsupported($"{pointer}/propertyNames");
            if (schema.TryGetProperty("dependentSchemas", out _))
                TrackUnsupported($"{pointer}/dependentSchemas");
            if (schema.TryGetProperty("dependentRequired", out _))
                TrackUnsupported($"{pointer}/dependentRequired");
            if (schema.TryGetProperty("minProperties", out _))
                TrackUnsupported($"{pointer}/minProperties");
            if (schema.TryGetProperty("maxProperties", out _))
                TrackUnsupported($"{pointer}/maxProperties");
        }

        private void TrackArrayUnsupported(JsonElement schema, string pointer)
        {
            if (schema.TryGetProperty("uniqueItems", out _))
                TrackUnsupported($"{pointer}/uniqueItems");
            if (schema.TryGetProperty("contains", out _))
                TrackUnsupported($"{pointer}/contains");
            if (schema.TryGetProperty("prefixItems", out _))
                TrackUnsupported($"{pointer}/prefixItems");
        }

        private static List<KeyValuePair<string, JsonElement>> ReadProperties(JsonElement schema)
        {
            var list = new List<KeyValuePair<string, JsonElement>>();
            if (schema.TryGetProperty("properties", out var p) &&
                p.ValueKind == JsonValueKind.Object)
                foreach (var pp in p.EnumerateObject())
                    list.Add(new KeyValuePair<string, JsonElement>(pp.Name, pp.Value));
            return list;
        }

        private static List<string> ReadRequired(JsonElement schema)
        {
            var list = new List<string>();
            if (schema.TryGetProperty("required", out var r) &&
                r.ValueKind == JsonValueKind.Array)
                foreach (var rr in r.EnumerateArray())
                    if (rr.ValueKind == JsonValueKind.String)
                        list.Add(rr.GetString()!);
            return list;
        }

        private static (bool? Allowed, JsonElement? Schema) ReadAdditional(JsonElement schema)
        {
            if (!schema.TryGetProperty("additionalProperties", out var a))
                return (null, null);
            if (a.ValueKind == JsonValueKind.False) return (false, null);
            if (a.ValueKind == JsonValueKind.True) return (true, null);
            if (a.ValueKind == JsonValueKind.Object) return (null, a);
            return (null, null);
        }

        private static int? TryGetInt(JsonElement schema, string name)
        {
            if (!schema.TryGetProperty(name, out var el)) return null;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v)) return v;
            return null;
        }

        private static string JsonLiteralToGbnf(JsonElement v)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
                v.WriteTo(w);
            var json = Encoding.UTF8.GetString(ms.ToArray());
            return FormatStringLiteral(json, alreadyJsonEncoded: true);
        }

        private static string FormatStringLiteral(string s, bool alreadyJsonEncoded = false)
        {
            string json;
            if (alreadyJsonEncoded)
            {
                json = s;
            }
            else
            {
                using var ms = new MemoryStream();
                using (var w = new Utf8JsonWriter(ms))
                    w.WriteStringValue(s);
                json = Encoding.UTF8.GetString(ms.ToArray());
            }
            var sb = new StringBuilder(json.Length + 2);
            sb.Append('"');
            foreach (var ch in json)
            {
                if (ch == '"') sb.Append("\\\"");
                else if (ch == '\\') sb.Append("\\\\");
                else sb.Append(ch);
            }
            sb.Append('"');
            return sb.ToString();
        }
    }

    // Test hook: clear the grammar cache between unit-test runs.
    internal static void ResetCache() => _grammarCache.Clear();
}
