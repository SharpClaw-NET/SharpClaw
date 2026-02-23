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

    /// <summary>
    /// Sanitized free-form text with length limits. Only allowed when
    /// <see cref="Mk8FreeTextConfig.Enabled"/> is <c>true</c> AND the
    /// specific command is not in <see cref="Mk8FreeTextConfig.UnsafeBinaries"/>.
    /// <para>
    /// When FreeText is disabled (globally or per-verb), validation
    /// falls back to <see cref="ComposedWords"/> using the same
    /// <see cref="Mk8Slot.WordListName"/>.
    /// </para>
    /// <para>
    /// Sanitization: max length enforced, control characters blocked,
    /// gigablacklist patterns checked, env-secret patterns
    /// (<c>KEY=</c>, <c>TOKEN=</c>, etc.) blocked.
    /// </para>
    /// </summary>
    FreeText,
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
    bool Variadic = false,
    /// <summary>
    /// Maximum character length for <see cref="Mk8SlotKind.FreeText"/> slots.
    /// Overrides the global <see cref="Mk8FreeTextConfig.MaxLength"/> when
    /// set to a positive value.
    /// </summary>
    int MaxFreeTextLength = 0);

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

    /// <summary>
    /// FreeText configuration. Controls whether FreeText slots are
    /// enabled and per-verb granular overrides.
    /// </summary>
    private readonly Mk8FreeTextConfig _freeTextConfig;

    /// <summary>
    /// Gigablacklist instance with compile-time + env-sourced patterns.
    /// </summary>
    private readonly Mk8GigaBlacklist _gigaBlacklist;

    private Mk8CommandWhitelist(
        Mk8AllowedCommand[] commands,
        Dictionary<string, HashSet<string>> wordLists,
        HashSet<string>? validProjectNames,
        Mk8FreeTextConfig freeTextConfig,
        Mk8GigaBlacklist gigaBlacklist)
    {
        _commands = commands;
        _wordLists = wordLists;
        _validProjectNames = validProjectNames;
        _freeTextConfig = freeTextConfig;
        _gigaBlacklist = gigaBlacklist;
    }

    /// <summary>
    /// Returns the gigablacklist instance used by this whitelist,
    /// so the compiler can use the same instance for in-memory verbs.
    /// </summary>
    public Mk8GigaBlacklist GigaBlacklist => _gigaBlacklist;

    /// <summary>
    /// Returns the compile-time contents of a named word list, or
    /// empty if the list does not exist.
    /// </summary>
    public IReadOnlySet<string> GetWordList(string name) =>
        _wordLists.TryGetValue(name, out var set) ? set : new HashSet<string>();

    /// <summary>
    /// Returns all word list names (sorted).
    /// </summary>
    public IReadOnlyList<string> GetWordListNames() =>
        _wordLists.Keys.Order().ToList();

    /// <summary>
    /// Returns the FreeText configuration.
    /// </summary>
    public Mk8FreeTextConfig FreeTextConfig => _freeTextConfig;

    /// <summary>
    /// Returns human-readable descriptions of all registered command
    /// templates.
    /// </summary>
    public IReadOnlyList<string> GetTemplateDescriptions()
    {
        var result = new List<string>(_commands.Length);
        foreach (var cmd in _commands)
            result.Add(cmd.Description);
        return result;
    }

    // ── Validation ────────────────────────────────────────────────

    /// <summary>
    /// Validates a ProcRun invocation against all registered templates.
    /// Returns <c>null</c> if a match is found, or a detailed error
    /// message if no template matches.
    /// <para>
    /// Enforces the gigablacklist on ALL arguments before template
    /// matching — any match throws <see cref="Mk8GigaBlacklistException"/>.
    /// </para>
    /// </summary>
    public string? Validate(string binary, string[] args, string sandboxRoot)
    {
        // ── Gigablacklist — unconditional, runs first ─────────────
        _gigaBlacklist.EnforceAll(binary, args);

        var name = Path.GetFileName(binary);

        if (Mk8BinaryAllowlist.IsPermanentlyBlocked(name))
        {
            if (!Mk8BinaryAllowlist.IsVersionCheckException(name, args))
                return $"Binary '{name}' is permanently blocked. " +
                       "mk8.shell only allows a closed set of binaries — interpreters " +
                       "(bash, cmd, powershell, python, node, etc.) and dangerous system " +
                       "tools (curl, wget, sudo, chmod, etc.) can never be invoked.\n" +
                       "  ✓ Correct: { \"verb\": \"ProcRun\", \"args\": [\"dotnet\", \"build\"] }\n" +
                       $"  ✗ Wrong:   {{ \"verb\": \"ProcRun\", \"args\": [\"{name}\", ...] }}\n" +
                       "Run { \"verb\": \"Mk8Templates\", \"args\": [] } to see all allowed commands. " +
                       "For file/text operations, use dedicated in-memory verbs " +
                       "(FileRead, HttpGet, TextRegex, etc.) — run { \"verb\": \"Mk8Verbs\", \"args\": [] } to list them.";
        }

        var candidates = _commands
            .Where(c => c.Binary.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
            return $"Binary '{name}' has no registered command templates.\n" +
                   "ProcRun uses a strict command-template whitelist — only binaries with " +
                   "explicit templates can execute. Every invocation must match a registered " +
                   "pattern exactly (binary + fixed prefix + typed flags + typed parameters).\n" +
                   $"  ✗ No templates exist for '{name}'.\n" +
                   "Run { \"verb\": \"Mk8Templates\", \"args\": [] } to see all registered templates. " +
                   "If you need text/file/HTTP operations, use dedicated in-memory verbs instead " +
                   "— run { \"verb\": \"Mk8Verbs\", \"args\": [] } to list them.";

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
               $"Tried {candidates.Count} template(s) for '{name}' — none matched:\n" +
               string.Join('\n', errors!) + "\n\n" +
               "Each ProcRun invocation must match a registered template exactly. " +
               "Run { \"verb\": \"Mk8Templates\", \"args\": [] } to see all registered templates. " +
               "If a flag or argument was rejected, run { \"verb\": \"Mk8Vocab\", \"args\": [\"<listName>\"] } " +
               "to see allowed words for ComposedWords/AdminWord slots, or " +
               "{ \"verb\": \"Mk8FreeText\", \"args\": [\"<command>\"] } to check FreeText status.";
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
                {
                    var allowedFlags = cmd.Flags is { Length: > 0 }
                        ? string.Join(", ", cmd.Flags.Select(f => f.Flag))
                        : "(none)";
                    return $"Unrecognized flag '{flagName}'.\n" +
                           $"This template ('{cmd.Description}') allows these flags: {allowedFlags}\n" +
                           $"  \u2717 Got: '{flagName}'";
                }

                if (!usedFlags.Add(flagName))
                    return $"Duplicate flag '{flagName}'. Each flag can only be specified once.";

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
                            return $"Flag '{flagName}' requires a value but none was provided.\n" +
                                   $"  ✓ Correct: \"{flagName}\", \"<value>\" (as two separate args) " +
                                   $"or \"{flagName}=<value>\" (combined)";
                        i++;
                        flagValue = remaining[i];
                    }

                    var valError = ValidateSlot(
                        flagDef.Value, flagValue, sandboxRoot,
                        cmd.Description, cmd.Binary);
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
            return $"Too many positional arguments: template expects at most {paramDefs.Length}, got {trailing.Count}.\n" +
                   $"Extra args: [{string.Join(", ", trailing.Skip(paramDefs.Length).Select(a => $"\"{a}\""))}]\n" +
                   "Check that flags start with '-' (otherwise they're treated as positional arguments).";

        for (var i = 0; i < fixedCount; i++)
        {
            if (i < trailing.Count)
            {
                var error = ValidateSlot(
                    paramDefs[i], trailing[i], sandboxRoot,
                    cmd.Description, cmd.Binary);
                if (error is not null)
                    return $"Parameter '{paramDefs[i].Name}': {error}";
            }
            else if (paramDefs[i].Required)
            {
                return $"Missing required parameter '{paramDefs[i].Name}' (slot type: {paramDefs[i].Kind}).\n" +
                       $"This template ('{cmd.Description}') requires this parameter at position {i + 1}.";
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
                var error = ValidateSlot(
                    varSlot, va, sandboxRoot,
                    cmd.Description, cmd.Binary);
                if (error is not null)
                    return $"Parameter '{varSlot.Name}': {error}";
            }
        }

        return null;
    }

    /// <summary>Maximum words in a <see cref="Mk8SlotKind.ComposedWords"/> value.</summary>
    public const int MaxComposedWords = 12;

    private string? ValidateSlot(
        Mk8Slot slot, string value, string sandboxRoot,
        string commandDescription, string binary)
    {
        return slot.Kind switch
        {
            Mk8SlotKind.Choice => slot.AllowedValues is not null &&
                slot.AllowedValues.Any(v => v.Equals(value, StringComparison.OrdinalIgnoreCase))
                    ? null
                    : $"Must be one of: {string.Join(", ", slot.AllowedValues ?? [])}. Got '{value}'.\n" +
                      "Choice slots accept only a fixed set of exact values (case-insensitive).\n" +
                      $"  ✓ Correct: one of [{string.Join(", ", slot.AllowedValues ?? [])}]\n" +
                      $"  ✗ Got:     \"{value}\"",

            Mk8SlotKind.SandboxPath => ValidateSandboxPath(value, sandboxRoot),

            Mk8SlotKind.AdminWord => ValidateAdminWord(slot.WordListName!, value),

            Mk8SlotKind.IntRange => int.TryParse(value, out var n) &&
                n >= slot.MinValue && n <= slot.MaxValue
                    ? null
                    : $"Must be an integer {slot.MinValue}–{slot.MaxValue}. Got '{value}'.\n" +
                      $"  ✓ Correct: any integer from {slot.MinValue} to {slot.MaxValue}\n" +
                      $"  ✗ Got:     \"{value}\"",

            Mk8SlotKind.ComposedWords => ValidateComposedWords(slot.WordListName!, value),

            Mk8SlotKind.CompoundName => ValidateCompoundName(value),

            Mk8SlotKind.FreeText => ValidateFreeText(
                slot, value, commandDescription, binary),

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
            return $"Path '{value}' failed sandbox validation: {ex.Message}\n" +
                   "SandboxPath slots require a path that resolves inside the workspace.\n" +
                   "  ✓ Correct: \"$WORKSPACE/src/app.cs\" or \"$WORKSPACE/output\"\n" +
                   $"  ✗ Got:     \"{value}\"\n" +
                   "Use $WORKSPACE as the base. Run { \"verb\": \"Mk8Info\", \"args\": [] } " +
                   "to see the sandbox root, or { \"verb\": \"DirList\", \"args\": [\"$WORKSPACE\"] } " +
                   "to explore.";
        }
    }

    private string? ValidateAdminWord(string wordListName, string value)
    {
        if (!_wordLists.TryGetValue(wordListName, out var set))
            return $"Word list '{wordListName}' is not configured. " +
                   "Run { \"verb\": \"Mk8VocabList\", \"args\": [] } to see available word lists.";

        if (!set.Contains(value))
            return $"'{value}' is not in the '{wordListName}' word list.\n" +
                   "AdminWord slots require an exact match from a fixed vocabulary.\n" +
                   $"  ✓ Allowed: {string.Join(", ", set.Order().Take(20))}" +
                   (set.Count > 20 ? $" ... ({set.Count} total)" : "") + "\n" +
                   $"  ✗ Got:     \"{value}\"\n" +
                   $"Run {{ \"verb\": \"Mk8Vocab\", \"args\": [\"{wordListName}\"] }} to see the full list.";

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
            return $"Word list '{wordListName}' is not configured. " +
                   "Run { \"verb\": \"Mk8VocabList\", \"args\": [] } to see available lists.";

        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length > MaxComposedWords)
            return $"Too many words ({words.Length}). Maximum is {MaxComposedWords}.\n" +
                   "ComposedWords slots split on spaces — each word is validated independently.\n" +
                   $"  ✓ Correct: up to {MaxComposedWords} space-separated words from the '{wordListName}' vocabulary\n" +
                   $"  ✗ Got:     {words.Length} words\n" +
                   "Shorten your message or use fewer words.";

        foreach (var word in words)
        {
            if (!set.Contains(word))
                return $"Word '{word}' is not in the '{wordListName}' vocabulary.\n" +
                       "ComposedWords validates EACH word independently against the word list. " +
                       $"The full value was: \"{value}\"\n" +
                       $"  ✓ Each word must be in the '{wordListName}' vocabulary\n" +
                       $"  ✗ \"{word}\" was not found\n" +
                       $"Run {{ \"verb\": \"Mk8Vocab\", \"args\": [\"{wordListName}\"] }} to see all allowed words. " +
                       "If FreeText is enabled for this command, you can use free-form text instead " +
                       "— run { \"verb\": \"Mk8FreeText\", \"args\": [\"<command>\"] } to check.";
        }

        return null;
    }

    private string? ValidateCompoundName(string value)
    {
        if (_validProjectNames is null || _validProjectNames.Count == 0)
            return "No project base names configured.\n" +
                   "An administrator must provide ProjectBases in Mk8RuntimeConfig at startup.\n" +
                   "  Example: new Mk8RuntimeConfig { ProjectBases = [\"Banana\", \"SharpClaw\"] }\n" +
                   "Until configured, 'dotnet new -n <name>' is unavailable. " +
                   "'dotnet new <template>' (without -n) still works — it uses the directory name.\n" +
                   "Run { \"verb\": \"Mk8Info\", \"args\": [] } to see the current runtime configuration.";

        if (_validProjectNames.Contains(value))
            return null;

        return $"'{value}' is not a valid project name.\n" +
               "CompoundName slots accept a runtime-registered base name, optionally " +
               "composed with a compile-time suffix (direct or dot-separated).\n" +
               $"  ✓ Examples: {string.Join(", ", _validProjectNames.Order().Take(10))}" +
               (_validProjectNames.Count > 10 ? $" ... ({_validProjectNames.Count} total)" : "") + "\n" +
               $"  ✗ Got:      \"{value}\"\n" +
               "The base names are configured by the administrator at startup. " +
               "Suffixes include App, Api, Core, Infrastructure, Contracts, Tests, etc.";
    }

    // ── FreeText validation ───────────────────────────────────────

    /// <summary>
    /// Patterns in FreeText values that indicate embedded secrets.
    /// Case-insensitive check — blocks <c>KEY=xxx</c>, <c>token:xxx</c>, etc.
    /// </summary>
    private static readonly string[] SecretPatterns =
    [
        "KEY=", "SECRET=", "TOKEN=", "PASSWORD=", "PASSWD=",
        "CREDENTIAL=", "CONN=", "CONNECTION_STRING=",
        "PRIVATE=", "ENCRYPT=", "JWT=", "BEARER=",
        "CERTIFICATE=", "APIKEY=", "API_KEY=",
        "KEY:", "SECRET:", "TOKEN:", "PASSWORD:",
        "AUTHORIZATION:", "BEARER:",
    ];

    private string? ValidateFreeText(
        Mk8Slot slot, string value,
        string commandDescription, string binary)
    {
        // If FreeText is disabled for this command, fall back to ComposedWords
        if (!_freeTextConfig.IsEnabledFor(commandDescription, binary))
        {
            if (slot.WordListName is null)
                return "FreeText is disabled and no fallback word list is configured.\n" +
                       "When FreeText is disabled, the slot falls back to ComposedWords validation " +
                       "which requires a word list. This slot has no fallback.\n" +
                       $"Run {{ \"verb\": \"Mk8FreeText\", \"args\": [\"{commandDescription}\"] }} " +
                       "to check FreeText status for this command.";

            return ValidateComposedWords(slot.WordListName, value);
        }

        if (string.IsNullOrWhiteSpace(value))
            return "Value cannot be empty.";

        // Max length: per-slot override > per-verb config > global config
        var maxLen = slot.MaxFreeTextLength > 0
            ? slot.MaxFreeTextLength
            : _freeTextConfig.GetMaxLength(commandDescription);

        if (value.Length > maxLen)
            return $"FreeText too long ({value.Length} chars). Maximum is {maxLen}.\n" +
                   $"  ✓ Correct: text up to {maxLen} characters\n" +
                   $"  ✗ Got:     {value.Length} characters\n" +
                   $"Run {{ \"verb\": \"Mk8FreeText\", \"args\": [\"{commandDescription}\"] }} " +
                   "to see the configured max length for this command.";

        // Block control characters (null, newlines, tabs, etc.)
        foreach (var ch in value)
        {
            if (char.IsControl(ch) && ch != ' ')
                return $"FreeText contains control character (U+{(int)ch:X4}).\n" +
                       "Only printable characters and spaces are allowed in FreeText values.\n" +
                       "  ✓ Correct: \"Fix authentication error\" (printable text)\n" +
                       "  ✗ Wrong:   text with newlines, tabs, null bytes, or escape sequences";
        }

        // Block secret patterns
        foreach (var pattern in SecretPatterns)
        {
            if (value.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return $"FreeText contains secret-like pattern '{pattern}'.\n" +
                       "Secrets must never appear in command arguments — they could be " +
                       "logged, captured in output, or persisted in audit trails.\n" +
                       "  ✓ Correct: \"Update database config\" (no secrets)\n" +
                       $"  ✗ Wrong:   text containing \"{pattern}\" patterns\n" +
                       "Remove secret-like patterns from your text.";
        }

        // Gigablacklist defense-in-depth
        var gbMatch = _gigaBlacklist.Check(value);
        if (gbMatch is not null)
            throw new Mk8GigaBlacklistException(gbMatch,
                $"FreeText value for '{commandDescription}' contains gigablacklisted term.");

        // ── Command-specific extra validation ─────────────────────
        var extraError = ValidateFreeTextExtra(slot, value, commandDescription);
        if (extraError is not null)
            return extraError;

        return null;
    }

    // ── Command-specific FreeText constraints ─────────────────────

    /// <summary>
    /// Additional validation rules for specific command descriptions.
    /// These enforce domain constraints beyond generic sanitization,
    /// e.g. C# identifier rules for migration names, git-ref rules
    /// for tag names.
    /// </summary>
    private static string? ValidateFreeTextExtra(
        Mk8Slot slot, string value, string commandDescription)
    {
        return commandDescription switch
        {
            "dotnet ef migrations add" => ValidateCSharpIdentifier(value),
            "git tag create" or "git tag annotated" or "git tag delete"
                when slot.Name == "name" => ValidateGitRefName(value),
            _ => null
        };
    }

    /// <summary>
    /// Validates that the value is a valid C# identifier: starts with a
    /// letter or underscore, contains only letters, digits, and
    /// underscores, no spaces, no special characters. EF generates a
    /// class from this name.
    /// </summary>
    private static string? ValidateCSharpIdentifier(string value)
    {
        if (value.Contains(' '))
            return "Migration name cannot contain spaces.\n" +
                   "EF Core generates a C# class from the migration name, so it must " +
                   "be a valid C# identifier (PascalCase, no spaces, no special chars).\n" +
                   "  ✓ Correct: \"AddUserPreferences\", \"InitialCreate\", \"V2_AddIndexes\"\n" +
                   $"  ✗ Got:     \"{value}\" (contains spaces)\n" +
                   "Use PascalCase like 'AddUserTable' or 'UpdateSchemaV2'.";

        if (!char.IsLetter(value[0]) && value[0] != '_')
            return $"Migration name must start with a letter or underscore.\n" +
                   "EF Core generates a C# class from this name — C# identifiers " +
                   "cannot start with a digit or special character.\n" +
                   "  ✓ Correct: \"AddUsers\", \"_TempMigration\"\n" +
                   $"  ✗ Got:     \"{value}\" (starts with '{value[0]}')";

        foreach (var ch in value)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '_')
                return $"Migration name contains invalid character '{ch}'.\n" +
                       "Only letters, digits, and underscores are allowed in C# identifiers.\n" +
                       "  ✓ Correct: \"AddUserRoles\", \"Update_V2\", \"Seed123\"\n" +
                       $"  ✗ Got:     \"{value}\" (contains '{ch}')\n" +
                       "If FreeText is disabled, the fallback vocabulary is 'MigrationNames' " +
                       "— run { \"verb\": \"Mk8Vocab\", \"args\": [\"MigrationNames\"] } to see allowed names.";
        }

        return null;
    }

    /// <summary>
    /// Characters forbidden in git ref names (tag names, branch names).
    /// See <c>git check-ref-format</c>.
    /// </summary>
    private static readonly char[] GitRefForbiddenChars =
        [' ', '~', '^', ':', '?', '*', '[', '\\', '\t', '\n', '\r'];

    /// <summary>
    /// Validates that the value is a valid git ref name for tags.
    /// No spaces, no <c>..</c>, no <c>~^:?*[\</c>, no control chars,
    /// cannot start/end with <c>.</c> or <c>/</c>, cannot end with
    /// <c>.lock</c>.
    /// </summary>
    private static string? ValidateGitRefName(string value)
    {
        if (value.Contains(".."))
            return "Tag name cannot contain '..'.\n" +
                   "Git ref names forbid '..' (range notation in git).\n" +
                   "  ✓ Correct: \"v1.0.0\", \"release-beta\"\n" +
                   $"  ✗ Got:     \"{value}\" (contains '..')\n" +
                   "If FreeText is disabled, use the 'TagNames' vocabulary " +
                   "— run { \"verb\": \"Mk8Vocab\", \"args\": [\"TagNames\"] } to see allowed names.";

        if (value.Contains("@{"))
            return "Tag name cannot contain '@{'.\n" +
                   "Git ref names forbid '@{' (reflog notation in git).\n" +
                   $"  ✗ Got: \"{value}\"";

        if (value.StartsWith('.') || value.StartsWith('/'))
            return $"Tag name cannot start with '{value[0]}'.\n" +
                   "Git ref names must not start with '.' or '/'.\n" +
                   "  ✓ Correct: \"v1.0.0\", \"release-1\"\n" +
                   $"  ✗ Got:     \"{value}\"";

        if (value.EndsWith('.') || value.EndsWith('/'))
            return $"Tag name cannot end with '{value[^1]}'.\n" +
                   "Git ref names must not end with '.' or '/'.\n" +
                   "  ✓ Correct: \"v1.0.0\" (dot in middle OK), \"release\"\n" +
                   $"  ✗ Got:     \"{value}\"";

        if (value.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            return "Tag name cannot end with '.lock'.\n" +
                   "Git uses '.lock' files internally for locking.\n" +
                   $"  ✗ Got: \"{value}\"";

        if (value.Contains("//"))
            return "Tag name cannot contain consecutive slashes.\n" +
                   $"  ✗ Got: \"{value}\"";

        foreach (var ch in GitRefForbiddenChars)
        {
            if (value.Contains(ch))
                return $"Tag name contains forbidden character '{ch}'.\n" +
                       "Git ref names forbid: space, ~, ^, :, ?, *, [, \\, and control chars.\n" +
                       "  ✓ Correct: \"v1.0.0-beta\", \"release/2.0\"\n" +
                       $"  ✗ Got:     \"{value}\" (contains '{ch}')";
        }

        return null;
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
    /// <param name="runtime">Runtime config (project bases, git URLs).</param>
    /// <param name="freeTextConfig">
    /// FreeText config merged from global + sandbox env. If <c>null</c>,
    /// FreeText is disabled (all FreeText slots fall back to ComposedWords).
    /// </param>
    /// <param name="envVocabularies">
    /// Additional vocabularies loaded from env files. Keys are word list
    /// names (e.g., <c>"CommitWords"</c>), values are arrays of words.
    /// These are merged additively with compile-time constants — env
    /// words ADD to the list, they never replace it.
    /// </param>
    /// <param name="gigaBlacklist">
    /// Gigablacklist instance with compile-time + env-sourced patterns.
    /// If <c>null</c>, a default instance (compile-time only) is used.
    /// </param>
    public static Mk8CommandWhitelist CreateDefault(
        Mk8RuntimeConfig? runtime = null,
        Mk8FreeTextConfig? freeTextConfig = null,
        Dictionary<string, string[]>? envVocabularies = null,
        Mk8GigaBlacklist? gigaBlacklist = null)
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

        // Aggregate every command category (compile-time constants).
        Aggregate(wordLists, commands, Mk8DotnetCommands.GetWordLists(), Mk8DotnetCommands.GetCommands());
        Aggregate(wordLists, commands, Mk8GitCommands.GetWordLists(), Mk8GitCommands.GetCommands());
        Aggregate(wordLists, commands, Mk8NodeNpmCommands.GetWordLists(), Mk8NodeNpmCommands.GetCommands());
        Aggregate(wordLists, commands, Mk8CargoCommands.GetWordLists(), Mk8CargoCommands.GetCommands());
        Aggregate(wordLists, commands, Mk8ArchiveCommands.GetWordLists(), Mk8ArchiveCommands.GetCommands());
        Aggregate(wordLists, commands, Mk8ReadOnlyToolCommands.GetWordLists(), Mk8ReadOnlyToolCommands.GetCommands());
        Aggregate(wordLists, commands, Mk8VersionCheckCommands.GetWordLists(), Mk8VersionCheckCommands.GetCommands());
        Aggregate(wordLists, commands, Mk8OpensslCommands.GetWordLists(), Mk8OpensslCommands.GetCommands());
        Aggregate(wordLists, commands, Mk8ToolCheckCommands.GetWordLists(), Mk8ToolCheckCommands.GetCommands());

        // ── Env-sourced vocabularies (additive merge) ─────────────
        // Words from env files ADD to the compile-time lists — they
        // never replace. This lets users extend vocabularies per-sandbox
        // by adding words to their mk8.shell.env file.

        if (envVocabularies is not null)
        {
            foreach (var (name, words) in envVocabularies)
            {
                if (!wordLists.TryGetValue(name, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    wordLists[name] = set;
                }
                foreach (var w in words)
                    set.Add(w);
            }
        }

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

        return new Mk8CommandWhitelist(
            [.. commands], wordLists, validProjectNames,
            freeTextConfig ?? new Mk8FreeTextConfig(),
            gigaBlacklist ?? new Mk8GigaBlacklist());
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
