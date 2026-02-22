namespace Mk8.Shell.Safety;

// ═══════════════════════════════════════════════════════════════════
// Strict command-template whitelist for ProcRun
// ═══════════════════════════════════════════════════════════════════
//
// Every ProcRun invocation must match a registered command template
// EXACTLY.  There is no generic "allowed binary + blocked flags"
// model — the agent can only run commands from a closed, enumerated
// set of patterns.
//
// Templates and word lists are compile-time constants defined in the
// Commands/ directory (one file per tool category).  They CANNOT be
// modified at runtime — to change the allowed surface, a developer
// edits the source file and recompiles.
//
// The ONLY runtime exception: project base names and git remote URLs
// are provided via Mk8RuntimeConfig at construction time, then sealed.
//
// ═════════════════════════════════════════════════════════════════

// ── Data model ────────────────────────────────────────────────────

/// <summary>
/// Describes what kind of value a parameter slot accepts.
/// </summary>
public enum Mk8SlotKind
{
    /// <summary>Value must exactly match one of <see cref="Mk8Slot.AllowedValues"/>.</summary>
    Choice,

    /// <summary>Value must resolve inside the sandbox via <see cref="Mk8PathSanitizer"/>.</summary>
    SandboxPath,

    /// <summary>Value must come from a named word list (compile-time constant).</summary>
    AdminWord,

    /// <summary>Value must be an integer in [<see cref="Mk8Slot.MinValue"/>, <see cref="Mk8Slot.MaxValue"/>].</summary>
    IntRange,

    /// <summary>
    /// Value is split on whitespace; each word must be in the named word list.
    /// Max <see cref="Mk8CommandWhitelist.MaxComposedWords"/> words.
    /// Spaces are safe because <c>ProcessStartInfo.ArgumentList</c> passes
    /// each argument individually — no shell splitting occurs.
    /// </summary>
    ComposedWords,

    /// <summary>
    /// Value must be a runtime-registered project base name, optionally
    /// composed with a compile-time suffix via direct concatenation or
    /// dot separator.  E.g., base <c>"Banana"</c> + suffix <c>"Api"</c>
    /// → <c>"BananaApi"</c> or <c>"Banana.Api"</c>.
    /// <para>
    /// This is the ONLY runtime exception in the whitelist — base names
    /// come from <see cref="Mk8RuntimeConfig.ProjectBases"/>, suffixes
    /// from <see cref="Mk8DotnetCommands.ProjectSuffixes"/>.
    /// </para>
    /// </summary>
    CompoundName,
}

/// <summary>
/// A typed parameter slot in a command template.
/// </summary>
public sealed record Mk8Slot(
    string Name,
    Mk8SlotKind Kind,
    string[]? AllowedValues = null,
    string? WordListName = null,
    int MinValue = 0,
    int MaxValue = int.MaxValue,
    bool Required = true,
    /// <summary>
    /// If <c>true</c>, this slot consumes ALL remaining trailing args.
    /// Only valid on the last <see cref="Mk8AllowedCommand.Params"/> slot.
    /// At least one value is required when <see cref="Required"/> is true.
    /// </summary>
    bool Variadic = false);

/// <summary>
/// An optional flag definition within a command template.
/// </summary>
public sealed record Mk8FlagDef(
    /// <summary>The flag string, e.g. <c>"--configuration"</c> or <c>"-n"</c>.</summary>
    string Flag,
    /// <summary>If non-null, the flag takes a typed value as the next arg.</summary>
    Mk8Slot? Value = null);

/// <summary>
/// A single allowed command invocation pattern.
/// </summary>
public sealed record Mk8AllowedCommand(
    string Description,
    string Binary,
    /// <summary>Fixed literal args that must appear in order after the binary.</summary>
    string[] Prefix,
    /// <summary>Optional flags that may appear in any order after the prefix.</summary>
    Mk8FlagDef[]? Flags = null,
    /// <summary>Positional parameters after the prefix (and any flags).</summary>
    Mk8Slot[]? Params = null);

// ── Whitelist engine (immutable after construction) ───────────────

/// <summary>
/// Strict command-template whitelist for ProcRun.  Constructed once
/// from compile-time constants in the <c>Commands/</c> files, plus
/// optional runtime config (<see cref="Mk8RuntimeConfig"/>), then
/// sealed — no commands or word lists can be added after creation.
/// </summary>
public sealed class Mk8CommandWhitelist
{
    private readonly Mk8AllowedCommand[] _commands;
    private readonly Dictionary<string, HashSet<string>> _wordLists;

    /// <summary>
    /// Pre-computed set of all valid compound project names
    /// (base, base+suffix, base.suffix).  Null if no runtime
    /// project bases were configured.
    /// </summary>
    private readonly HashSet<string>? _validProjectNames;

    private Mk8CommandWhitelist(
        Mk8AllowedCommand[] commands,
        Dictionary<string, HashSet<string>> wordLists,
        HashSet<string>? validProjectNames)
    {
        _commands = commands;
        _wordLists = wordLists;
        _validProjectNames = validProjectNames;
    }

    /// <summary>
    /// Returns the compile-time contents of a named word list, or
    /// empty if the list does not exist.
    /// </summary>
    public IReadOnlySet<string> GetWordList(string name) =>
        _wordLists.TryGetValue(name, out var set) ? set : new HashSet<string>();

    // ── Validation ────────────────────────────────────────────────

    /// <summary>
    /// Validates a ProcRun invocation against all registered templates.
    /// Returns <c>null</c> if a match is found, or a detailed error
    /// message if no template matches.
    /// </summary>
    public string? Validate(string binary, string[] args, string sandboxRoot)
    {
        var name = Path.GetFileName(binary);

        if (Mk8BinaryAllowlist.IsPermanentlyBlocked(name))
        {
            // Narrow carve-out: version-check-only commands on
            // otherwise-blocked binaries (e.g. python3 --version).
            if (!Mk8BinaryAllowlist.IsVersionCheckException(name, args))
                return $"Binary '{name}' is permanently blocked.";
        }

        var candidates = _commands
            .Where(c => c.Binary.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
            return $"Binary '{name}' has no registered command templates. " +
                   "Only binaries with explicit templates can be used.";

        List<string>? errors = null;
        foreach (var cmd in candidates)
        {
            var error = TryMatch(cmd, args, sandboxRoot);
            if (error is null)
                return null;

            errors ??= [];
            errors.Add($"  • {cmd.Description}: {error}");
        }

        return $"No matching template for '{name} {string.Join(' ', args)}'.\n" +
               $"Tried {candidates.Count} template(s):\n" +
               string.Join('\n', errors!);
    }

    // ── Template matching ─────────────────────────────────────────

    private string? TryMatch(
        Mk8AllowedCommand cmd, string[] args, string sandboxRoot)
    {
        if (args.Length < cmd.Prefix.Length)
            return $"Too few arguments (expected prefix: {string.Join(' ', cmd.Prefix)}).";

        for (var i = 0; i < cmd.Prefix.Length; i++)
        {
            if (!cmd.Prefix[i].Equals(args[i], StringComparison.OrdinalIgnoreCase))
                return $"Expected '{cmd.Prefix[i]}' at position {i}, got '{args[i]}'.";
        }

        var remaining = args.AsSpan(cmd.Prefix.Length);
        var usedFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var trailing = new List<string>();

        for (var i = 0; i < remaining.Length; i++)
        {
            var arg = remaining[i];

            if (arg.StartsWith('-'))
            {
                var eqIdx = arg.IndexOf('=');
                var flagName = eqIdx >= 0 ? arg[..eqIdx] : arg;

                var flagDef = cmd.Flags?.FirstOrDefault(f =>
                    f.Flag.Equals(flagName, StringComparison.OrdinalIgnoreCase));

                if (flagDef is null)
                    return $"Unrecognized flag '{flagName}'.";

                if (!usedFlags.Add(flagName))
                    return $"Duplicate flag '{flagName}'.";

                if (flagDef.Value is not null)
                {
                    string flagValue;
                    if (eqIdx >= 0)
                    {
                        flagValue = arg[(eqIdx + 1)..];
                    }
                    else
                    {
                        if (i + 1 >= remaining.Length)
                            return $"Flag '{flagName}' requires a value.";
                        i++;
                        flagValue = remaining[i];
                    }

                    var valError = ValidateSlot(flagDef.Value, flagValue, sandboxRoot);
                    if (valError is not null)
                        return $"Flag '{flagName}' value: {valError}";
                }
            }
            else
            {
                trailing.Add(arg);
            }
        }

        var paramDefs = cmd.Params ?? [];
        var hasVariadic = paramDefs.Length > 0 && paramDefs[^1].Variadic;
        var fixedCount = hasVariadic ? paramDefs.Length - 1 : paramDefs.Length;

        if (!hasVariadic && trailing.Count > paramDefs.Length)
            return $"Too many arguments: expected at most {paramDefs.Length}, got {trailing.Count}.";

        for (var i = 0; i < fixedCount; i++)
        {
            if (i < trailing.Count)
            {
                var error = ValidateSlot(paramDefs[i], trailing[i], sandboxRoot);
                if (error is not null)
                    return $"Parameter '{paramDefs[i].Name}': {error}";
            }
            else if (paramDefs[i].Required)
            {
                return $"Missing required parameter '{paramDefs[i].Name}'.";
            }
        }

        if (hasVariadic)
        {
            var varSlot = paramDefs[^1];
            var varArgs = trailing.Skip(fixedCount).ToList();

            if (varSlot.Required && varArgs.Count == 0)
                return $"Missing required parameter '{varSlot.Name}' (at least one value required).";

            foreach (var va in varArgs)
            {
                var error = ValidateSlot(varSlot, va, sandboxRoot);
                if (error is not null)
                    return $"Parameter '{varSlot.Name}': {error}";
            }
        }

        return null;
    }

    /// <summary>Maximum words in a <see cref="Mk8SlotKind.ComposedWords"/> value.</summary>
    public const int MaxComposedWords = 12;

    private string? ValidateSlot(Mk8Slot slot, string value, string sandboxRoot)
    {
        return slot.Kind switch
        {
            Mk8SlotKind.Choice => slot.AllowedValues is not null &&
                slot.AllowedValues.Any(v => v.Equals(value, StringComparison.OrdinalIgnoreCase))
                    ? null
                    : $"Must be one of: {string.Join(", ", slot.AllowedValues ?? [])}. Got '{value}'.",

            Mk8SlotKind.SandboxPath => ValidateSandboxPath(value, sandboxRoot),

            Mk8SlotKind.AdminWord => ValidateAdminWord(slot.WordListName!, value),

            Mk8SlotKind.IntRange => int.TryParse(value, out var n) &&
                n >= slot.MinValue && n <= slot.MaxValue
                    ? null
                    : $"Must be an integer {slot.MinValue}–{slot.MaxValue}. Got '{value}'.",

            Mk8SlotKind.ComposedWords => ValidateComposedWords(slot.WordListName!, value),

            Mk8SlotKind.CompoundName => ValidateCompoundName(value),

            _ => $"Unknown slot kind: {slot.Kind}."
        };
    }

    private static string? ValidateSandboxPath(string value, string sandboxRoot)
    {
        try
        {
            Mk8PathSanitizer.Resolve(value, sandboxRoot);
            return null;
        }
        catch (Exception ex)
        {
            return $"Path '{value}' failed sandbox validation: {ex.Message}";
        }
    }

    private string? ValidateAdminWord(string wordListName, string value)
    {
        if (!_wordLists.TryGetValue(wordListName, out var set))
            return $"Word list '{wordListName}' is not configured.";

        if (!set.Contains(value))
            return $"'{value}' is not in the '{wordListName}' word list. " +
                   $"Allowed: {string.Join(", ", set.Order().Take(20))}" +
                   (set.Count > 20 ? $" ... ({set.Count} total)" : "") + ".";

        return null;
    }

    /// <summary>
    /// Validates a space-separated composed value.  Each word must be
    /// in the named word list independently.  Spaces are safe because
    /// <c>ProcessStartInfo.ArgumentList</c> passes each argument as
    /// a single OS-level argument — no shell tokenizes the spaces.
    /// </summary>
    private string? ValidateComposedWords(string wordListName, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Value cannot be empty.";

        if (!_wordLists.TryGetValue(wordListName, out var set))
            return $"Word list '{wordListName}' is not configured.";

        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length > MaxComposedWords)
            return $"Too many words ({words.Length}). Maximum is {MaxComposedWords}.";

        foreach (var word in words)
        {
            if (!set.Contains(word))
                return $"Word '{word}' is not in the '{wordListName}' vocabulary. " +
                       $"Allowed: {string.Join(", ", set.Order().Take(20))}" +
                       (set.Count > 20 ? $" ... ({set.Count} total)" : "") + ".";
        }

        return null;
    }

    private string? ValidateCompoundName(string value)
    {
        if (_validProjectNames is null || _validProjectNames.Count == 0)
            return "No project base names configured. " +
                   "An administrator must provide ProjectBases in Mk8RuntimeConfig.";

        if (_validProjectNames.Contains(value))
            return null;

        return $"'{value}' is not a valid project name. " +
               "It must be a registered base name optionally followed by a " +
               "compile-time suffix (direct concatenation or dot-separated).";
    }

    // ═══════════════════════════════════════════════════════════════
    // Factory — aggregates compile-time constants + runtime config
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates the singleton whitelist from compile-time constant
    /// templates defined in the <c>Commands/</c> directory, plus
    /// optional runtime configuration for project names and git
    /// remote URLs.
    /// </summary>
    public static Mk8CommandWhitelist CreateDefault(Mk8RuntimeConfig? runtime = null)
    {
        if (runtime?.ProjectBases.Length > Mk8RuntimeConfig.MaxProjectBases)
            throw new ArgumentException(
                $"Too many project bases ({runtime.ProjectBases.Length}). " +
                $"Maximum is {Mk8RuntimeConfig.MaxProjectBases}.");
        if (runtime?.GitRemoteUrls.Length > Mk8RuntimeConfig.MaxGitRemoteUrls)
            throw new ArgumentException(
                $"Too many git remote URLs ({runtime.GitRemoteUrls.Length}). " +
                $"Maximum is {Mk8RuntimeConfig.MaxGitRemoteUrls}.");

        var wordLists = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var commands = new List<Mk8AllowedCommand>();

        // Aggregate every command category.
        Aggregate(wordLists, commands, Mk8DotnetCommands.GetWordLists(), Mk8DotnetCommands.GetCommands());
        Aggregate(wordLists, commands, Mk8GitCommands.GetWordLists(), Mk8GitCommands.GetCommands());
        Aggregate(wordLists, commands, Mk8NodeNpmCommands.GetWordLists(), Mk8NodeNpmCommands.GetCommands());
        Aggregate(wordLists, commands, Mk8CargoCommands.GetWordLists(), Mk8CargoCommands.GetCommands());
        Aggregate(wordLists, commands, Mk8ArchiveCommands.GetWordLists(), Mk8ArchiveCommands.GetCommands());
        Aggregate(wordLists, commands, Mk8ReadOnlyToolCommands.GetWordLists(), Mk8ReadOnlyToolCommands.GetCommands());
        Aggregate(wordLists, commands, Mk8VersionCheckCommands.GetWordLists(), Mk8VersionCheckCommands.GetCommands());
        Aggregate(wordLists, commands, Mk8OpensslCommands.GetWordLists(), Mk8OpensslCommands.GetCommands());

        // ── Runtime word lists (the ONLY runtime exception) ───────

        if (runtime?.GitRemoteUrls is { Length: > 0 } urls)
        {
            var urlSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var url in urls)
                urlSet.Add(url);
            wordLists["GitRemoteUrls"] = urlSet;
        }

        // ── Pre-compute valid compound project names ──────────────

        HashSet<string>? validProjectNames = null;
        if (runtime?.ProjectBases is { Length: > 0 } bases)
        {
            validProjectNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var suffixes = wordLists.TryGetValue("ProjectSuffixes", out var s) ? s : [];

            foreach (var baseName in bases)
            {
                validProjectNames.Add(baseName);
                foreach (var suffix in suffixes)
                {
                    validProjectNames.Add(baseName + suffix);
                    validProjectNames.Add(baseName + "." + suffix);
                }
            }
        }

        return new Mk8CommandWhitelist([.. commands], wordLists, validProjectNames);
    }

    private static void Aggregate(
        Dictionary<string, HashSet<string>> wordLists,
        List<Mk8AllowedCommand> commands,
        KeyValuePair<string, string[]>[] wl,
        Mk8AllowedCommand[] cmds)
    {
        foreach (var (name, words) in wl)
        {
            if (!wordLists.TryGetValue(name, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                wordLists[name] = set;
            }

            foreach (var w in words)
                set.Add(w);
        }

        commands.AddRange(cmds);
    }
}
