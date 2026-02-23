namespace Mk8.Shell.Safety;

/// <summary>
/// Centralized gigablacklist enforcement. If ANY pattern in this list
/// appears ANYWHERE in ANY argument of ANY command, the operation fails
/// before compilation with <see cref="Mk8GigaBlacklistException"/>.
/// <para>
/// This is a separate, unconditional safety layer that runs on the
/// full materialized command — after variable expansion, after slot
/// validation, on every string the agent produces.
/// </para>
/// <para>
/// The compile-time patterns are split into two groups:
/// <list type="bullet">
///   <item><b>mk8.shell env patterns</b> — the 4 sandbox env/key filenames
///     (<c>mk8.shell.env</c>, <c>mk8.shell.signed.env</c>, <c>mk8.shell.base.env</c>,
///     <c>mk8.shell.key</c>). Disabled only via <c>DisableMk8shellEnvsGigablacklist</c>
///     in base.env.</item>
///   <item><b>Hardcoded patterns</b> — destructive commands, block devices,
///     system control, SQL destruction, etc. Disabled via
///     <c>DisableHardcodedGigablacklist</c> in base.env.</item>
/// </list>
/// Both flags are base.env-only (ignored in sandbox env) and default to
/// <c>false</c>. Disabling either is strongly discouraged outside test
/// environments.
/// </para>
/// <para>
/// Additional patterns can be loaded additively from
/// <c>mk8.shell.base.env</c> (global — loaded once at startup, cached
/// until restart) and per-sandbox signed env (loaded fresh on every
/// script execution). Custom patterns are validated at load time —
/// empty/whitespace entries are ignored, patterns shorter than 2
/// characters are rejected.
/// </para>
/// </summary>
public sealed class Mk8GigaBlacklist
{
    /// <summary>Minimum length for a custom blacklist pattern to be accepted.</summary>
    public const int MinCustomPatternLength = 2;

    /// <summary>
    /// mk8.shell infrastructure patterns — sandbox env filenames and the
    /// signing key. These protect the sandbox's own configuration files
    /// from agent access. Disabled independently from the rest of the
    /// hardcoded list via <c>DisableMk8shellEnvsGigablacklist</c>.
    /// </summary>
    private static readonly string[] Mk8ShellEnvPatterns =
    [
        "mk8.shell.env",
        "mk8.shell.signed.env",
        "mk8.shell.base.env",
        "mk8.shell.key",
    ];

    /// <summary>
    /// Compile-time hardcoded patterns covering destructive commands,
    /// shell injection markers, block devices, system control, SQL
    /// destruction, registry/service manipulation, and privilege
    /// escalation. Disabled via <c>DisableHardcodedGigablacklist</c>.
    /// </summary>
    private static readonly string[] HardcodedPatterns =
    [
        // ── Shell injection markers (defense-in-depth) ────────────
        "$(", "`", "&&", "||", ">>", "<<", "${",
        "; rm ", ";rm ", "| ", " |",

        // ── Destructive filesystem — Linux/macOS ──────────────────
        "rm -rf /",
        "rm -rf /*",
        "rm -rf ~",
        "rm -rf .",
        "--no-preserve-root",
        "mkfs.",
        "dd if=/dev/",
        "wipefs",
        "shred ",
        "shred -",

        // ── Destructive filesystem — Windows ──────────────────────
        "format c:",
        "format d:",
        "rd /s /q",
        "rmdir /s /q",
        "del /s /q",
        "del /f /s /q",
        "cipher /w:",
        "diskpart",
        "bcdedit",
        "sfc /scannow",
        "dism ",

        // ── Raw block-device access ───────────────────────────────
        "/dev/sda",
        "/dev/sdb",
        "/dev/sdc",
        "/dev/nvme",
        "/dev/vda",
        "/dev/hda",
        "/dev/xvda",
        "/dev/loop",
        "/dev/dm-",
        "/dev/md",
        "/dev/mmcblk",
        "\\\\.\\PhysicalDrive",
        "\\\\.\\HarddiskVolume",
        "\\\\.\\GLOBALROOT",

        // ── System shutdown / reboot / halt ───────────────────────
        "shutdown -h",
        "shutdown -r",
        "shutdown /s",
        "shutdown /r",
        "shutdown /f",
        "poweroff",
        "halt",
        "reboot",
        "init 0",
        "init 6",
        "systemctl reboot",
        "systemctl poweroff",

        // ── Process kill-all ──────────────────────────────────────
        "kill -9 -1",
        "killall -9",
        "taskkill /f /im *",
        "pkill -9",

        // ── Sensitive system files ────────────────────────────────
        "/etc/shadow",
        "/etc/sudoers",
        "/etc/gshadow",
        "SAM database",
        "SYSTEM hive",

        // ── Fork bomb patterns ────────────────────────────────────
        ":(){ ",
        "%0|%0",

        // ── SQL destruction ───────────────────────────────────────
        "DROP DATABASE",
        "DROP TABLE",
        "TRUNCATE TABLE",
        "DROP SCHEMA",
        "DROP ALL",
        "xp_cmdshell",
        "EXEC xp_",
        "sp_configure",

        // ── Windows registry attacks ──────────────────────────────
        "reg delete",
        "reg add hklm",
        "reg add hkcu",

        // ── Service manipulation ──────────────────────────────────
        "sc delete",
        "sc stop",
        "net stop",
        "net user ",
        "schtasks /delete",

        // ── Privilege escalation markers ──────────────────────────
        "visudo",
        "passwd ",
        "usermod ",
        "useradd ",
        "userdel ",
        "groupmod ",
        "chpasswd",
    ];

    /// <summary>
    /// All effective patterns for this instance.
    /// </summary>
    private readonly string[] _allPatterns;

    /// <summary>
    /// Returns all effective patterns (compile-time + env-sourced).
    /// Read-only view for diagnostic introspection.
    /// </summary>
    public IReadOnlyList<string> EffectivePatterns => _allPatterns;

    /// <summary>
    /// Creates a gigablacklist with all compile-time patterns (no env extras).
    /// </summary>
    public Mk8GigaBlacklist() =>
        _allPatterns = [.. Mk8ShellEnvPatterns, .. HardcodedPatterns];

    /// <summary>
    /// Creates a gigablacklist with configurable compile-time groups plus
    /// custom patterns from env files.
    /// </summary>
    /// <param name="customPatterns">
    /// Additional patterns from <c>MK8_BLACKLIST</c> in base.env and/or
    /// sandbox env. Merged additively — both sources contribute.
    /// </param>
    /// <param name="disableHardcoded">
    /// When <c>true</c>, the hardcoded destructive-command patterns are
    /// excluded. The mk8.shell env patterns remain active unless
    /// <paramref name="disableMk8shellEnvs"/> is also <c>true</c>.
    /// <para>
    /// <b>WARNING:</b> This should essentially never be set to <c>true</c>
    /// except in a dedicated test environment. The hardcoded patterns
    /// exist for a reason — they prevent agents from producing arguments
    /// that reference catastrophically destructive commands, raw block
    /// devices, system control sequences, and privilege escalation tools.
    /// Disabling them removes a critical defense-in-depth layer.
    /// </para>
    /// </param>
    /// <param name="disableMk8shellEnvs">
    /// When <c>true</c>, the mk8.shell env/key filename patterns are
    /// also excluded. This means agents can reference
    /// <c>mk8.shell.env</c>, <c>mk8.shell.signed.env</c>,
    /// <c>mk8.shell.base.env</c>, and <c>mk8.shell.key</c> in their
    /// arguments — which could allow them to read or manipulate sandbox
    /// configuration and signing keys.
    /// <para>
    /// <b>WARNING:</b> This is a separate opt-out that only takes effect
    /// when <paramref name="disableHardcoded"/> is also <c>true</c> (if
    /// hardcoded patterns are active, the env filenames are always
    /// enforced regardless of this flag). Even when hardcoded patterns
    /// are disabled, the env filenames remain blocked by default.
    /// Disabling both flags simultaneously removes ALL compile-time
    /// protection and is strongly discouraged even in test environments.
    /// </para>
    /// </param>
    public Mk8GigaBlacklist(
        IEnumerable<string>? customPatterns,
        bool disableHardcoded = false,
        bool disableMk8shellEnvs = false)
    {
        var patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Compile-time patterns (conditional) ───────────────────
        // mk8.shell env patterns are included unless BOTH disable
        // flags are set. When only disableHardcoded is true, the env
        // patterns still protect sandbox infrastructure.
        if (!disableHardcoded)
        {
            foreach (var p in Mk8ShellEnvPatterns)
                patterns.Add(p);
            foreach (var p in HardcodedPatterns)
                patterns.Add(p);
        }
        else if (!disableMk8shellEnvs)
        {
            // Hardcoded disabled, but env patterns still active
            foreach (var p in Mk8ShellEnvPatterns)
                patterns.Add(p);
        }

        // ── Custom patterns (always additive) ─────────────────────
        if (customPatterns is not null)
        {
            foreach (var p in customPatterns)
            {
                if (string.IsNullOrWhiteSpace(p))
                    continue;

                var trimmed = p.Trim();
                if (trimmed.Length < MinCustomPatternLength)
                    continue;

                patterns.Add(trimmed);
            }
        }

        _allPatterns = [.. patterns];
    }

    /// <summary>
    /// Checks a single value against the gigablacklist. Returns the
    /// matching pattern if found, or <c>null</c> if clean.
    /// </summary>
    public string? Check(string value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        foreach (var pattern in _allPatterns)
        {
            if (value.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return pattern;
        }

        return null;
    }

    /// <summary>
    /// Checks all arguments in a command invocation. Throws
    /// <see cref="Mk8GigaBlacklistException"/> on first match.
    /// </summary>
    public void EnforceAll(string binary, string[] args)
    {
        var match = Check(binary);
        if (match is not null)
            throw new Mk8GigaBlacklistException(match,
                $"Binary name '{binary}' contains gigablacklisted term '{match}'.\n" +
                "The gigablacklist runs on ALL arguments (including the binary name) " +
                "before any other validation. This is non-bypassable.\n" +
                $"  ✗ Binary '{binary}' matched pattern: \"{match}\"\n" +
                "Run { \"verb\": \"Mk8Templates\", \"args\": [] } to see allowed commands, " +
                "or { \"verb\": \"Mk8Blacklist\", \"args\": [] } to see all blocked patterns.");

        foreach (var arg in args)
        {
            match = Check(arg);
            if (match is not null)
                throw new Mk8GigaBlacklistException(match,
                    $"ProcRun argument '{Truncate(arg)}' contains gigablacklisted term '{match}'.\n" +
                    "The gigablacklist blocks dangerous patterns in ALL arguments of ALL commands.\n" +
                    $"  ✗ Argument matched pattern: \"{match}\"\n" +
                    "Rephrase the argument to avoid this pattern. " +
                    "Run { \"verb\": \"Mk8Blacklist\", \"args\": [] } to see all blocked patterns.");
        }
    }

    /// <summary>
    /// Checks all arguments in an in-memory verb invocation. Throws
    /// <see cref="Mk8GigaBlacklistException"/> on first match.
    /// </summary>
    public void EnforceAllInMemory(string verb, string[] args)
    {
        foreach (var arg in args)
        {
            var match = Check(arg);
            if (match is not null)
                throw new Mk8GigaBlacklistException(match,
                    $"Argument to in-memory verb '{verb}' contains gigablacklisted term '{match}'.\n" +
                    "The gigablacklist runs on ALL arguments of ALL commands (including " +
                    "in-memory verbs like FileRead, FileWrite, DirList, etc.), not just ProcRun.\n" +
                    $"  ✗ Argument matched pattern: \"{match}\"\n" +
                    "Rephrase the argument to avoid this pattern. " +
                    "Run { \"verb\": \"Mk8Blacklist\", \"args\": [] } to see all blocked patterns.");
        }
    }

    /// <summary>
    /// Validates candidate custom patterns. Returns only those that are
    /// non-empty and meet the minimum length requirement.
    /// </summary>
    public static string[] ValidateCustomPatterns(string[]? raw)
    {
        if (raw is null || raw.Length == 0)
            return [];

        return raw
            .Where(p => !string.IsNullOrWhiteSpace(p) && p.Trim().Length >= MinCustomPatternLength)
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string Truncate(string s) =>
        s.Length > 40 ? s[..40] + "..." : s;
}
