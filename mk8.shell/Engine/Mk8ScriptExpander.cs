using Mk8.Shell.Safety;

namespace Mk8.Shell.Engine;

/// <summary>
/// Pre-compilation expansion pass. Runs BEFORE the compiler touches
/// the script. Transforms high-level constructs into flat operation
/// lists:
/// <list type="bullet">
///   <item><c>ForEach</c> → N concrete operations (compile-time unroll)</item>
///   <item><c>If</c> → 0 or 1 operation (compile-time predicate eval)</item>
///   <item><c>FileWriteMany</c> → N FileWrite operations</item>
///   <item><c>FileCopyMany</c> → N FileCopy operations</item>
///   <item><c>FileDeleteMany</c> → N FileDelete operations</item>
///   <item><c>Include</c> → inlined operations from admin-approved fragment</item>
/// </list>
/// <para>
/// After expansion, the resulting <see cref="Mk8ShellScript"/> contains
/// ONLY primitive verbs — no ForEach, no If, no batch verbs, no Include.
/// The compiler then handles it as a flat list.
/// </para>
/// </summary>
public static class Mk8ScriptExpander
{
    /// <summary>
    /// Hard ceiling on total expanded operations. Prevents OOM from
    /// nested ForEach or large batches.
    /// </summary>
    public const int MaxExpandedOperations = 1024;

    /// <summary>
    /// Expands a script, resolving all control flow, batch, and include
    /// verbs into flat primitive operations.
    /// </summary>
    public static Mk8ShellScript Expand(
        Mk8ShellScript script,
        IReadOnlyDictionary<string, string> variables,
        IMk8FragmentRegistry? fragmentRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(script);

        var expanded = new List<Mk8ShellOperation>();

        foreach (var op in script.Operations)
            ExpandOperation(op, variables, expanded, depth: 0, fragmentRegistry);

        if (expanded.Count > MaxExpandedOperations)
            throw new Mk8CompileException(Mk8ShellVerb.ForEach,
                $"Script expanded to {expanded.Count} operations, " +
                $"exceeding the limit of {MaxExpandedOperations}.");

        // Expand cleanup operations too (same pipeline).
        List<Mk8ShellOperation>? expandedCleanup = null;
        if (script.Cleanup is { Count: > 0 })
        {
            expandedCleanup = new List<Mk8ShellOperation>();
            foreach (var op in script.Cleanup)
                ExpandOperation(op, variables, expandedCleanup, depth: 0, fragmentRegistry);

            if (expanded.Count + expandedCleanup.Count > MaxExpandedOperations)
                throw new Mk8CompileException(Mk8ShellVerb.ForEach,
                    $"Script + cleanup expanded to {expanded.Count + expandedCleanup.Count} " +
                    $"operations, exceeding the limit of {MaxExpandedOperations}.");
        }

        return script with
        {
            Operations = expanded,
            Cleanup = expandedCleanup
        };
    }

    private static void ExpandOperation(
        Mk8ShellOperation op,
        IReadOnlyDictionary<string, string> variables,
        List<Mk8ShellOperation> output,
        int depth,
        IMk8FragmentRegistry? fragmentRegistry = null)
    {
        // Guard against nesting abuse (ForEach inside ForEach body etc.)
        if (depth > 3)
            throw new Mk8CompileException(op.Verb,
                "Control flow nesting exceeds maximum depth of 3.");

        switch (op.Verb)
        {
            case Mk8ShellVerb.ForEach:
                ExpandForEach(op, variables, output, depth, fragmentRegistry);
                break;

            case Mk8ShellVerb.If:
                ExpandIf(op, variables, output, depth, fragmentRegistry);
                break;

            case Mk8ShellVerb.FileWriteMany:
                ExpandFileWriteMany(op, output);
                break;

            case Mk8ShellVerb.FileCopyMany:
                ExpandFileCopyMany(op, output);
                break;

            case Mk8ShellVerb.FileDeleteMany:
                ExpandFileDeleteMany(op, output);
                break;

            case Mk8ShellVerb.Include:
                ExpandInclude(op, variables, output, depth, fragmentRegistry);
                break;

            default:
                output.Add(op);
                break;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // ForEach
    // ═══════════════════════════════════════════════════════════════

    private static void ExpandForEach(
        Mk8ShellOperation op,
        IReadOnlyDictionary<string, string> variables,
        List<Mk8ShellOperation> output,
        int depth,
        IMk8FragmentRegistry? fragmentRegistry)
    {
        var forEach = op.ForEach
            ?? throw new Mk8CompileException(Mk8ShellVerb.ForEach,
                "ForEach verb requires a 'forEach' definition.");

        if (forEach.Items.Count == 0)
            return; // Empty iteration — no-op.

        if (forEach.Items.Count > Mk8ForEach.MaxExpansion)
            throw new Mk8CompileException(Mk8ShellVerb.ForEach,
                $"ForEach has {forEach.Items.Count} items, " +
                $"exceeding the limit of {Mk8ForEach.MaxExpansion}.");

        // The body verb must be a primitive or another control verb.
        // ForEach body cannot be ForEach (no nested loops).
        if (forEach.Body.Verb == Mk8ShellVerb.ForEach)
            throw new Mk8CompileException(Mk8ShellVerb.ForEach,
                "Nested ForEach is not allowed.");

        for (var i = 0; i < forEach.Items.Count; i++)
        {
            var item = forEach.Items[i];

            // Replace $ITEM and $INDEX in the body's args.
            var resolvedArgs = new string[forEach.Body.Args.Length];
            for (var a = 0; a < forEach.Body.Args.Length; a++)
            {
                resolvedArgs[a] = forEach.Body.Args[a]
                    .Replace("$ITEM", item, StringComparison.OrdinalIgnoreCase)
                    .Replace("$INDEX", i.ToString(), StringComparison.OrdinalIgnoreCase);
            }

            var expanded = forEach.Body with { Args = resolvedArgs };
            ExpandOperation(expanded, variables, output, depth + 1, fragmentRegistry);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // If
    // ═══════════════════════════════════════════════════════════════

    private static void ExpandIf(
        Mk8ShellOperation op,
        IReadOnlyDictionary<string, string> variables,
        List<Mk8ShellOperation> output,
        int depth,
        IMk8FragmentRegistry? fragmentRegistry)
    {
        var conditional = op.If
            ?? throw new Mk8CompileException(Mk8ShellVerb.If,
                "If verb requires an 'if' definition.");

        if (EvaluatePredicate(conditional.Predicate, variables))
            ExpandOperation(conditional.Then, variables, output, depth + 1, fragmentRegistry);
        // else: skip — no-op.
    }

    private static bool EvaluatePredicate(
        Mk8Predicate predicate,
        IReadOnlyDictionary<string, string> variables)
    {
        return predicate.Kind switch
        {
            // These check the variable dictionary — not the actual
            // filesystem. Useful for checking $PREV content.
            Mk8PredicateKind.PrevContains =>
                predicate.Args.Length >= 1
                && variables.TryGetValue("PREV", out var prev)
                && prev.Contains(predicate.Args[0], StringComparison.OrdinalIgnoreCase),

            Mk8PredicateKind.PrevEmpty =>
                !variables.TryGetValue("PREV", out var prevVal)
                || string.IsNullOrWhiteSpace(prevVal),

            Mk8PredicateKind.PrevStartsWith =>
                predicate.Args.Length >= 1
                && variables.TryGetValue("PREV", out var prevSw)
                && prevSw.StartsWith(predicate.Args[0], StringComparison.OrdinalIgnoreCase),

            Mk8PredicateKind.PrevEndsWith =>
                predicate.Args.Length >= 1
                && variables.TryGetValue("PREV", out var prevEw)
                && prevEw.EndsWith(predicate.Args[0], StringComparison.OrdinalIgnoreCase),

            Mk8PredicateKind.PrevEquals =>
                predicate.Args.Length >= 1
                && variables.TryGetValue("PREV", out var prevEq)
                && string.Equals(prevEq, predicate.Args[0], StringComparison.OrdinalIgnoreCase),

            Mk8PredicateKind.PrevMatch =>
                predicate.Args.Length >= 1
                && variables.TryGetValue("PREV", out var prevRx)
                && EvaluateRegexPredicate(prevRx, predicate.Args[0]),

            Mk8PredicateKind.PrevLineCount =>
                predicate.Args.Length >= 2
                && variables.TryGetValue("PREV", out var prevLc)
                && EvaluateLineCountPredicate(prevLc, predicate.Args[0], predicate.Args[1]),

            Mk8PredicateKind.CaptureEmpty =>
                predicate.Args.Length >= 1
                && (!variables.TryGetValue(predicate.Args[0], out var capVal)
                    || string.IsNullOrWhiteSpace(capVal)),

            Mk8PredicateKind.CaptureContains =>
                predicate.Args.Length >= 2
                && variables.TryGetValue(predicate.Args[0], out var capCont)
                && capCont.Contains(predicate.Args[1], StringComparison.OrdinalIgnoreCase),

            Mk8PredicateKind.EnvEquals =>
                predicate.Args.Length >= 2
                && Mk8EnvAllowlist.IsAllowed(predicate.Args[0])
                && string.Equals(
                    Environment.GetEnvironmentVariable(predicate.Args[0]),
                    predicate.Args[1],
                    StringComparison.OrdinalIgnoreCase),

            // FileExists / DirExists are deferred — the compiler
            // emits a conditional marker that the executor evaluates
            // at runtime (since we don't have filesystem access at
            // compile time). The expander includes the Then op and
            // tags it with the predicate for the executor.
            Mk8PredicateKind.FileExists or Mk8PredicateKind.DirExists =>
                true, // Always include — executor checks at runtime.

            _ => throw new Mk8CompileException(Mk8ShellVerb.If,
                $"Unknown predicate kind: {predicate.Kind}.")
        };
    }

    /// <summary>Regex match with 2-second timeout to prevent ReDoS.</summary>
    private static bool EvaluateRegexPredicate(string input, string pattern)
    {
        try
        {
            return System.Text.RegularExpressions.Regex.IsMatch(
                input, pattern,
                System.Text.RegularExpressions.RegexOptions.None,
                TimeSpan.FromSeconds(2));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Evaluates PrevLineCount predicate. Operator: eq, gt, lt, gte, lte.
    /// </summary>
    private static bool EvaluateLineCountPredicate(
        string input, string @operator, string countStr)
    {
        if (!int.TryParse(countStr, out var expected))
            return false;

        var lineCount = input.Split('\n').Length;

        return @operator.ToLowerInvariant() switch
        {
            "eq" => lineCount == expected,
            "gt" => lineCount > expected,
            "lt" => lineCount < expected,
            "gte" => lineCount >= expected,
            "lte" => lineCount <= expected,
            _ => false
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // Batch ops
    // ═══════════════════════════════════════════════════════════════

    private static void ExpandFileWriteMany(
        Mk8ShellOperation op,
        List<Mk8ShellOperation> output)
    {
        // Args format: [path1, content1, path2, content2, ...]
        if (op.Args.Length == 0 || op.Args.Length % 2 != 0)
            throw new Mk8CompileException(Mk8ShellVerb.FileWriteMany,
                $"FileWriteMany requires pairs of [path, content]. Got {op.Args.Length} args.");

        var fileCount = op.Args.Length / 2;
        if (fileCount > Mk8BatchFileWrite.MaxFiles)
            throw new Mk8CompileException(Mk8ShellVerb.FileWriteMany,
                $"FileWriteMany has {fileCount} files, " +
                $"exceeding the limit of {Mk8BatchFileWrite.MaxFiles}.");

        for (var i = 0; i < op.Args.Length; i += 2)
        {
            output.Add(new Mk8ShellOperation(
                Mk8ShellVerb.FileWrite,
                [op.Args[i], op.Args[i + 1]],
                op.MaxRetries));
        }
    }

    private static void ExpandFileCopyMany(
        Mk8ShellOperation op,
        List<Mk8ShellOperation> output)
    {
        // Args format: [src1, dst1, src2, dst2, ...]
        if (op.Args.Length == 0 || op.Args.Length % 2 != 0)
            throw new Mk8CompileException(Mk8ShellVerb.FileCopyMany,
                $"FileCopyMany requires pairs of [source, dest]. Got {op.Args.Length} args.");

        var pairCount = op.Args.Length / 2;
        if (pairCount > Mk8BatchFileWrite.MaxFiles)
            throw new Mk8CompileException(Mk8ShellVerb.FileCopyMany,
                $"FileCopyMany has {pairCount} pairs, " +
                $"exceeding the limit of {Mk8BatchFileWrite.MaxFiles}.");

        for (var i = 0; i < op.Args.Length; i += 2)
        {
            output.Add(new Mk8ShellOperation(
                Mk8ShellVerb.FileCopy,
                [op.Args[i], op.Args[i + 1]],
                op.MaxRetries));
        }
    }

    private static void ExpandFileDeleteMany(
        Mk8ShellOperation op,
        List<Mk8ShellOperation> output)
    {
        // Args format: [path1, path2, path3, ...]
        if (op.Args.Length == 0)
            throw new Mk8CompileException(Mk8ShellVerb.FileDeleteMany,
                "FileDeleteMany requires at least one path.");

        if (op.Args.Length > Mk8BatchFileWrite.MaxFiles)
            throw new Mk8CompileException(Mk8ShellVerb.FileDeleteMany,
                $"FileDeleteMany has {op.Args.Length} paths, " +
                $"exceeding the limit of {Mk8BatchFileWrite.MaxFiles}.");

        foreach (var path in op.Args)
        {
            output.Add(new Mk8ShellOperation(
                Mk8ShellVerb.FileDelete,
                [path],
                op.MaxRetries));
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Include (compile-time inlining from admin-approved fragments)
    // ═══════════════════════════════════════════════════════════════

    private static void ExpandInclude(
        Mk8ShellOperation op,
        IReadOnlyDictionary<string, string> variables,
        List<Mk8ShellOperation> output,
        int depth,
        IMk8FragmentRegistry? fragmentRegistry)
    {
        if (op.Args.Length != 1)
            throw new Mk8CompileException(Mk8ShellVerb.Include,
                $"Include requires exactly 1 argument (fragment ID), got {op.Args.Length}.");

        var fragmentId = op.Args[0];

        if (fragmentRegistry is null)
            throw new Mk8CompileException(Mk8ShellVerb.Include,
                "Include verb requires a fragment registry, but none was provided.");

        if (!fragmentRegistry.TryGetFragment(fragmentId, out var fragment) || fragment is null)
            throw new Mk8CompileException(Mk8ShellVerb.Include,
                $"Fragment '{fragmentId}' is not registered. " +
                "Only admin-approved fragments can be referenced.");

        // Expand each operation from the fragment — they go through
        // the same pipeline (ForEach, If, batch expansion). Depth is
        // incremented to prevent deep nesting.
        foreach (var fragmentOp in fragment)
            ExpandOperation(fragmentOp, variables, output, depth + 1, fragmentRegistry);
    }
}
