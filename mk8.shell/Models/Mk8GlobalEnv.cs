using System.Text.Json;
using System.Text.Json.Serialization;
using Mk8.Shell.Isolation;
using Mk8.Shell.Safety;

namespace Mk8.Shell.Models;

/// <summary>
/// Global environment loaded from <c>%APPDATA%/mk8.shell/mk8.shell.base.env</c>.
/// This file is a JSON document containing project bases, git remote URLs,
/// vocabularies, FreeText config, and any other environment-wide settings.
/// <para>
/// On first startup, if the file does not exist or is empty, the
/// hardcoded compile-time vocabularies from the <c>Commands/</c> files
/// are written into it as the default. After that, vocabularies are
/// read from the file (and merged with sandbox env files).
/// </para>
/// </summary>
public sealed class Mk8GlobalEnv
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null, // preserve exact property names
    };

    /// <summary>
    /// Base project names available for <c>dotnet new -n</c> compound
    /// names across all sandboxes.
    /// </summary>
    [JsonPropertyName("ProjectBases")]
    public string[] ProjectBases { get; set; } = [];

    /// <summary>
    /// Allowed git remote URLs across all sandboxes.
    /// </summary>
    [JsonPropertyName("GitRemoteUrls")]
    public string[] GitRemoteUrls { get; set; } = [];

    /// <summary>
    /// Allowed git clone URLs across all sandboxes.
    /// </summary>
    [JsonPropertyName("GitCloneUrls")]
    public string[] GitCloneUrls { get; set; } = [];

    /// <summary>
    /// FreeText configuration. Controls whether free-form text is
    /// allowed in FreeText-typed slots, with per-verb granularity.
    /// </summary>
    [JsonPropertyName("FreeText")]
    public Mk8FreeTextConfig FreeText { get; set; } = new();

    /// <summary>
    /// Vocabularies for ComposedWords / FreeText-fallback word lists.
    /// Keys are list names (e.g., <c>"CommitWords"</c>, <c>"BranchNames"</c>),
    /// values are arrays of words. These are merged additively with
    /// compile-time constants — env words ADD, they never replace.
    /// </summary>
    [JsonPropertyName("Vocabularies")]
    public Dictionary<string, string[]> Vocabularies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Custom gigablacklist patterns. These are loaded additively into
    /// the <see cref="Mk8GigaBlacklist"/> alongside compile-time patterns.
    /// <para>
    /// Each entry is a case-insensitive substring pattern. Entries
    /// shorter than <see cref="Mk8GigaBlacklist.MinCustomPatternLength"/>
    /// characters or empty/whitespace are silently ignored.
    /// </para>
    /// </summary>
    [JsonPropertyName("CustomBlacklist")]
    public string[] CustomBlacklist { get; set; } = [];

    /// <summary>
    /// When <c>true</c>, the hardcoded compile-time gigablacklist patterns
    /// (destructive commands, block devices, system control, etc.) are
    /// disabled. The mk8.shell env/key patterns remain active unless
    /// <see cref="DisableMk8shellEnvsGigablacklist"/> is also <c>true</c>.
    /// <para>
    /// <b>⚠ WARNING — TEST ENVIRONMENTS ONLY:</b> This should essentially
    /// never be set to <c>true</c> in production. The hardcoded
    /// gigablacklist exists for a reason — it prevents agents from
    /// producing arguments referencing catastrophically destructive
    /// commands (<c>rm -rf /</c>, <c>format c:</c>, <c>dd if=/dev/</c>),
    /// raw block devices, system shutdown/reboot, SQL destruction
    /// (<c>DROP DATABASE</c>), privilege escalation, and more. Disabling
    /// it removes a critical defense-in-depth layer. If you disable this
    /// in production, you accept full responsibility for any agent-produced
    /// destructive output that would normally be caught.
    /// </para>
    /// <para>
    /// This flag is base.env-only — it is ignored in sandbox env files.
    /// Custom patterns from <c>CustomBlacklist</c> and <c>MK8_BLACKLIST</c>
    /// remain active regardless of this setting.
    /// </para>
    /// </summary>
    [JsonPropertyName("DisableHardcodedGigablacklist")]
    public bool DisableHardcodedGigablacklist { get; set; }

    /// <summary>
    /// When <c>true</c> AND <see cref="DisableHardcodedGigablacklist"/>
    /// is also <c>true</c>, the mk8.shell env/key filename patterns
    /// (<c>mk8.shell.env</c>, <c>mk8.shell.signed.env</c>,
    /// <c>mk8.shell.base.env</c>, <c>mk8.shell.key</c>) are also
    /// disabled. This means agents can reference sandbox configuration
    /// and signing key filenames in their arguments.
    /// <para>
    /// <b>⚠ WARNING — EXTREME RISK:</b> This flag has no effect unless
    /// <see cref="DisableHardcodedGigablacklist"/> is also <c>true</c>.
    /// When both are <c>true</c>, ALL compile-time gigablacklist
    /// protection is removed. The only remaining patterns are custom
    /// entries from <c>CustomBlacklist</c> / <c>MK8_BLACKLIST</c>.
    /// This is strongly discouraged even in test environments because
    /// it allows agents to discover and reference the sandbox's own
    /// signed env files and cryptographic key.
    /// </para>
    /// <para>
    /// This flag is base.env-only — it is ignored in sandbox env files.
    /// </para>
    /// </summary>
    [JsonPropertyName("DisableMk8shellEnvsGigablacklist")]
    public bool DisableMk8shellEnvsGigablacklist { get; set; }

    // ── Container isolation ───────────────────────────────────────

    /// <summary>
    /// OS-level container isolation configuration. Controls resource
    /// limits, network filtering, and process isolation boundaries for
    /// sandbox processes. Container isolation is mandatory for all
    /// mk8.shell sandboxes — macOS is denied.
    /// <para>
    /// Requires root/admin privileges on the host for full isolation.
    /// </para>
    /// </summary>
    [JsonPropertyName("ContainerIsolation")]
    public Mk8ContainerConfigJson ContainerIsolation { get; set; } = new();

    /// <summary>
    /// Network whitelist for sandbox processes. When container isolation
    /// is enabled, ALL outbound network traffic is blocked by default
    /// (iron curtain). Only destinations listed here are permitted.
    /// <para>
    /// Format: comma-separated <c>host:port/protocol</c> entries.
    /// Example: <c>"nuget.org:443/tcp, api.github.com:443/tcp"</c>
    /// Use <c>"*"</c> to allow all (disables iron curtain).
    /// </para>
    /// </summary>
    [JsonPropertyName("NetworkWhitelist")]
    public string NetworkWhitelist { get; set; } = "";

    // ── Startup cache
    // Base env is loaded ONCE at startup and cached. Changes require
    // a process restart. Sandbox env is loaded fresh on every script
    // execution via Mk8TaskContainer.Create().

    private static readonly object CacheLock = new();
    private static Mk8GlobalEnv? _cached;

    /// <summary>
    /// Returns the cached global env, loading it from disk on first
    /// call. Subsequent calls return the same instance — changes to
    /// <c>mk8.shell.base.env</c> require a process restart.
    /// </summary>
    public static Mk8GlobalEnv Load()
    {
        if (_cached is not null)
            return _cached;

        lock (CacheLock)
        {
            if (_cached is not null)
                return _cached;

            _cached = LoadFromDisk();
            return _cached;
        }
    }

    /// <summary>
    /// Forces a reload of the global env from disk on next
    /// <see cref="Load"/> call. Used only for testing.
    /// </summary>
    internal static void ResetCache()
    {
        lock (CacheLock)
            _cached = null;
    }

    private static readonly string BaseEnvPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "mk8.shell",
        "mk8.shell.base.env");

    private static Mk8GlobalEnv LoadFromDisk()
    {
        var envPath = BaseEnvPath;

        // Auto-seed: if file missing or empty, write defaults
        if (!File.Exists(envPath) || string.IsNullOrWhiteSpace(File.ReadAllText(envPath)))
        {
            var defaults = CreateDefaults();
            var dir = Path.GetDirectoryName(envPath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(envPath, JsonSerializer.Serialize(defaults, WriteOptions));
        }

        var json = File.ReadAllText(envPath);
        return JsonSerializer.Deserialize<Mk8GlobalEnv>(json, JsonOptions)
            ?? throw new InvalidOperationException(
                "mk8.shell.base.env deserialized to null.");
    }

    /// <summary>
    /// Builds an <see cref="Mk8RuntimeConfig"/> from the loaded global env.
    /// </summary>
    public Mk8RuntimeConfig ToRuntimeConfig() => new()
    {
        ProjectBases = ProjectBases,
        GitRemoteUrls = GitRemoteUrls,
        GitCloneUrls = GitCloneUrls,
    };

    /// <summary>
    /// Builds an <see cref="Mk8ContainerConfig"/> from the loaded global env.
    /// </summary>
    public Mk8ContainerConfig ToContainerConfig() => new()
    {
        MemoryLimitBytes = ContainerIsolation.MemoryLimitBytes,
        CpuPercentLimit = ContainerIsolation.CpuPercentLimit,
        MaxProcesses = ContainerIsolation.MaxProcesses,
        MaxWriteBytes = ContainerIsolation.MaxWriteBytes,
        NetworkWhitelist = Mk8NetworkWhitelist.Parse(NetworkWhitelist),
        MappedDirectories = ContainerIsolation.MappedDirectories
            .Select(d => new MappedDirectory(d.HostPath, d.ContainerPath, d.ReadOnly))
            .ToList(),
    };

    /// <summary>
    /// Creates a default global env with all hardcoded vocabularies
    /// from the compile-time <c>Commands/</c> files. Used to seed
    /// the base.env file on first startup.
    /// </summary>
    public static Mk8GlobalEnv CreateDefaults()
    {
        var vocabs = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        // Collect all word lists from all command categories
        AggregateVocab(vocabs, Mk8GitCommands.GetWordLists());
        AggregateVocab(vocabs, Mk8DotnetCommands.GetWordLists());

        return new Mk8GlobalEnv
        {
            ProjectBases = ["Banana"],
            GitRemoteUrls =
            [
                "https://github.com/mkn8rn/BananaApp",
            ],
            GitCloneUrls =
            [
                "https://github.com/mkn8rn/BananaApp",
            ],
            FreeText = new Mk8FreeTextConfig
            {
                Enabled = false,
                MaxLength = 200,
                PerVerb = new Dictionary<string, Mk8FreeTextVerbPolicy>(StringComparer.OrdinalIgnoreCase)
                {
                    ["git commit"] = new() { Enabled = false, MaxLength = 200 },
                    ["git tag create"] = new() { Enabled = false, MaxLength = 128 },
                    ["git tag annotated"] = new() { Enabled = false, MaxLength = 200 },
                    ["git tag delete"] = new() { Enabled = false, MaxLength = 128 },
                    ["git merge"] = new() { Enabled = false, MaxLength = 200 },
                    ["dotnet ef migrations add"] = new() { Enabled = false, MaxLength = 128 },
                },
            },
            Vocabularies = vocabs,
            CustomBlacklist = [],
            DisableHardcodedGigablacklist = false,
            DisableMk8shellEnvsGigablacklist = false,
            ContainerIsolation = new Mk8ContainerConfigJson(),
            NetworkWhitelist = "",
        };
    }

    private static void AggregateVocab(
        Dictionary<string, string[]> target,
        KeyValuePair<string, string[]>[] wordLists)
    {
        foreach (var (name, words) in wordLists)
            target[name] = words;
    }
}

/// <summary>
/// JSON-serializable container isolation settings for base.env.
/// Maps to <see cref="Mk8ContainerConfig"/> at runtime.
/// Container isolation is mandatory — this controls resource limits
/// and mapped directories, not whether isolation is active.
/// </summary>
public sealed class Mk8ContainerConfigJson
{
    [JsonPropertyName("MemoryLimitBytes")]
    public long MemoryLimitBytes { get; set; }

    [JsonPropertyName("CpuPercentLimit")]
    public int CpuPercentLimit { get; set; }

    [JsonPropertyName("MaxProcesses")]
    public int MaxProcesses { get; set; } = 32;

    [JsonPropertyName("MaxWriteBytes")]
    public long MaxWriteBytes { get; set; }

    /// <summary>
    /// Host directories mapped into the container (tool directories).
    /// The sandbox directory is always mapped implicitly — do not
    /// include it here.
    /// </summary>
    [JsonPropertyName("MappedDirectories")]
    public List<MappedDirectoryJson> MappedDirectories { get; set; } = [];
}

/// <summary>
/// JSON-serializable directory mapping for base.env.
/// </summary>
public sealed class MappedDirectoryJson
{
    [JsonPropertyName("HostPath")]
    public string HostPath { get; set; } = "";

    [JsonPropertyName("ContainerPath")]
    public string ContainerPath { get; set; } = "";

    [JsonPropertyName("ReadOnly")]
    public bool ReadOnly { get; set; } = true;
}
