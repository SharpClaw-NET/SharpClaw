using Mk8.Shell.Engine;
using Mk8.Shell.Isolation;
using Mk8.Shell.Safety;

namespace Mk8.Shell.Models;

/// <summary>
/// Per-command execution envelope for a single mk8.shell script.
/// Created fresh for every command — env vars, vocabularies, and
/// blacklists are rebuilt each time so sandbox-level overrides are
/// never stale. The underlying OS-level sandbox container (cgroup,
/// iptables chains, resource limits) is persistent and managed by
/// <see cref="Mk8ContainerManager"/> — it was started when the
/// sandbox was registered and runs continuously until the sandbox
/// is deleted or the mk8.shell process shuts down.
/// <para>
/// Lifecycle for every command execution:
/// <list type="number">
///   <item>Load global env from <c>%APPDATA%/mk8.shell/mk8.shell.base.env</c> (cached at startup).</item>
///   <item>Look up sandbox ID in local <c>%APPDATA%/mk8.shell/sandboxes.json</c>.</item>
///   <item>Resolve sandbox root path.</item>
///   <item>Read <c>mk8.shell.signed.env</c> from sandbox root (fresh every execution).</item>
///   <item>Verify signature against local key.</item>
///   <item>Extract env vars from the verified signed content.</item>
///   <item>Merge vocabularies + FreeText config from global + sandbox.</item>
///   <item>Build gigablacklist from compile-time + global + sandbox patterns.</item>
///   <item>Build container isolation config (global tightened by sandbox).</item>
///   <item>Build <see cref="Mk8WorkspaceContext"/> with merged env.</item>
///   <item>Get the already-running sandbox container from <see cref="Mk8ContainerManager"/>.</item>
///   <item>Execute the command.</item>
///   <item>Dispose — per-command state is discarded. The container continues running.</item>
/// </list>
/// </para>
/// </summary>
public sealed class Mk8TaskContainer : IAsyncDisposable, IDisposable
{
    /// <summary>Sandbox identity for this task.</summary>
    public Mk8Sandbox Sandbox { get; }

    /// <summary>
    /// Workspace context built from global + sandbox env.
    /// Used by the compiler and executor.
    /// </summary>
    public Mk8WorkspaceContext Workspace { get; }

    /// <summary>
    /// Runtime config built from global env. Used to construct the
    /// command whitelist for this task.
    /// </summary>
    public Mk8RuntimeConfig RuntimeConfig { get; }

    /// <summary>
    /// FreeText configuration merged from global + sandbox env.
    /// </summary>
    public Mk8FreeTextConfig FreeTextConfig { get; }

    /// <summary>
    /// Env-sourced vocabularies merged from global + sandbox env.
    /// Keys are list names, values are word arrays. Additive merge.
    /// </summary>
    public Dictionary<string, string[]> EnvVocabularies { get; }

    /// <summary>
    /// Gigablacklist instance with compile-time patterns + custom
    /// patterns from global base.env + sandbox env. Built fresh
    /// per execution so sandbox-level patterns are never stale.
    /// </summary>
    public Mk8GigaBlacklist GigaBlacklist { get; }

    /// <summary>
    /// OS-level container isolation configuration. Controls process
    /// isolation, resource limits, and network iron curtain for sandbox
    /// processes. May be disabled (default).
    /// </summary>
    public Mk8ContainerConfig ContainerConfig { get; }

    /// <summary>
    /// Active sandbox container providing OS-level process containment,
    /// resource limits, network filtering, and filesystem isolation.
    /// Owned by <see cref="Mk8ContainerManager"/> — the task container
    /// holds a reference, not ownership. The container was started when
    /// the sandbox was registered and runs continuously. Always present
    /// — container isolation is mandatory.
    /// </summary>
    public Mk8SandboxContainer SandboxContainer { get; }

    private Mk8TaskContainer(
        Mk8Sandbox sandbox,
        Mk8WorkspaceContext workspace,
        Mk8RuntimeConfig runtimeConfig,
        Mk8FreeTextConfig freeTextConfig,
        Dictionary<string, string[]> envVocabularies,
        Mk8GigaBlacklist gigaBlacklist,
        Mk8ContainerConfig containerConfig,
        Mk8SandboxContainer sandboxContainer)
    {
        Sandbox = sandbox;
        Workspace = workspace;
        RuntimeConfig = runtimeConfig;
        FreeTextConfig = freeTextConfig;
        EnvVocabularies = envVocabularies;
        GigaBlacklist = gigaBlacklist;
        ContainerConfig = containerConfig;
        SandboxContainer = sandboxContainer;
    }

    /// <summary>
    /// Creates a per-command execution envelope for the given sandbox.
    /// Performs the full initialization sequence: global env → registry
    /// lookup → signature verification → env loading → vocabulary merge.
    /// The sandbox container must already be running (started at
    /// sandbox registration by <see cref="Mk8ContainerManager"/>).
    /// </summary>
    public static async Task<Mk8TaskContainer> CreateAsync(
        string sandboxId,
        Mk8SandboxRegistry? registry = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxId);

        // Step 1: Load global env.
        var globalEnv = Mk8GlobalEnv.Load();
        var runtimeConfig = globalEnv.ToRuntimeConfig();

        // Step 2: Resolve sandbox from local registry.
        registry ??= new Mk8SandboxRegistry();
        var entry = registry.Resolve(sandboxId);

        var sandboxRoot = Path.GetFullPath(entry.RootPath);
        if (!Directory.Exists(sandboxRoot))
            throw new DirectoryNotFoundException(
                $"Sandbox root directory '{sandboxRoot}' for sandbox " +
                $"'{sandboxId}' does not exist.");

        // Step 3: Load and verify signed env.
        var signedEnvPath = Path.Combine(
            sandboxRoot, Mk8SandboxRegistry.SandboxSignedEnvFileName);

        if (!File.Exists(signedEnvPath))
            throw new Mk8SandboxSignatureException(
                $"Signed env file not found at '{signedEnvPath}'. " +
                "The sandbox may not have been initialized by mk8.shell.startup.");

        var key = registry.LoadKey();
        var signedContent = File.ReadAllText(signedEnvPath);
        var envContent = Mk8SandboxEnvSigner.VerifyAndExtract(signedContent, key);

        // Step 4: Parse env vars from verified content.
        var sandboxVars = Mk8SandboxEnvParser.Parse(envContent);

        // Step 5: Merge FreeText config (sandbox overrides global).
        var sandboxFreeTextJson = sandboxVars.GetValueOrDefault("MK8_FREETEXT_CONFIG");
        var sandboxFreeTextConfig = sandboxFreeTextJson is not null
            ? Mk8FreeTextConfig.Parse(sandboxFreeTextJson)
            : null;
        var mergedFreeTextConfig = globalEnv.FreeText.MergeWith(sandboxFreeTextConfig);

        // Step 6: Merge vocabularies (global + sandbox, additive).
        var mergedVocabs = MergeVocabularies(globalEnv.Vocabularies, sandboxVars);

        // Step 7: Build gigablacklist (compile-time + global + sandbox, additive).
        // Disable flags are base.env-only — sandbox env cannot override them.
        var mergedBlacklistPatterns = MergeBlacklist(
            globalEnv.CustomBlacklist, sandboxVars);
        var gigaBlacklist = new Mk8GigaBlacklist(
            mergedBlacklistPatterns,
            disableHardcoded: globalEnv.DisableHardcodedGigablacklist,
            disableMk8shellEnvs: globalEnv.DisableMk8shellEnvsGigablacklist);

        // Step 8: Build container isolation config.
        // Global config from base.env; sandbox env can tighten but not loosen.
        var globalContainerConfig = globalEnv.ToContainerConfig();
        var sandboxNetworkWhitelist = sandboxVars.GetValueOrDefault("MK8_NETWORK_WHITELIST");
        var sandboxContainerConfig = sandboxNetworkWhitelist is not null
            ? new Mk8ContainerConfig
              {
                  NetworkWhitelist = Mk8NetworkWhitelist.Parse(sandboxNetworkWhitelist),
              }
            : null;
        var containerConfig = globalContainerConfig.TightenWith(sandboxContainerConfig);

        // Step 9: Build sandbox model.
        var sandbox = new Mk8Sandbox
        {
            Id = sandboxId,
            RootPath = sandboxRoot,
            RegisteredAtUtc = entry.RegisteredAtUtc,
        };

        // Step 10: Build workspace context.
        var workspace = new Mk8WorkspaceContext(
            SandboxId: sandboxId,
            SandboxRoot: sandboxRoot,
            WorkingDirectory: sandboxRoot,
            RunAsUser: Environment.UserName,
            Variables: sandboxVars);

        // Step 11: Get the already-running sandbox container.
        // The container was started when the sandbox was registered and
        // runs continuously. If the container is not active, the sandbox
        // is not usable — Mk8ContainerManager.GetContainer throws.
        var sandboxContainer = Mk8ContainerManager.Instance.GetContainer(sandboxId);

        return new Mk8TaskContainer(
            sandbox, workspace, runtimeConfig,
            mergedFreeTextConfig, mergedVocabs, gigaBlacklist,
            containerConfig, sandboxContainer);
    }

    /// <summary>
    /// Merges vocabularies from global env + sandbox env vars.
    /// Sandbox env keys like <c>MK8_VOCAB_CommitWords=word1,word2,word3</c>
    /// add to the global vocabulary (never replace).
    /// </summary>
    private static Dictionary<string, string[]> MergeVocabularies(
        Dictionary<string, string[]> globalVocabs,
        Dictionary<string, string> sandboxVars)
    {
        const string vocabPrefix = "MK8_VOCAB_";
        var merged = new Dictionary<string, string[]>(
            globalVocabs, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in sandboxVars)
        {
            if (!key.StartsWith(vocabPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var listName = key[vocabPrefix.Length..];
            if (string.IsNullOrWhiteSpace(listName))
                continue;

            var sandboxWords = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (sandboxWords.Length == 0)
                continue;

            if (merged.TryGetValue(listName, out var existing))
            {
                // Additive merge: combine both arrays (dedup at whitelist level)
                var combined = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
                foreach (var w in sandboxWords)
                    combined.Add(w);
                merged[listName] = [.. combined];
            }
            else
            {
                merged[listName] = sandboxWords;
            }
        }

        return merged;
    }

    /// <summary>
    /// Merges custom blacklist patterns from global env + sandbox env vars.
    /// Sandbox env key <c>MK8_BLACKLIST</c> is a comma-separated list of
    /// additional patterns. Both sources are additive.
    /// </summary>
    private static string[]? MergeBlacklist(
        string[] globalPatterns,
        Dictionary<string, string> sandboxVars)
    {
        var sandboxRaw = sandboxVars.GetValueOrDefault("MK8_BLACKLIST");
        var sandboxPatterns = sandboxRaw?.Split(',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var hasGlobal = globalPatterns is { Length: > 0 };
        var hasSandbox = sandboxPatterns is { Length: > 0 };

        if (!hasGlobal && !hasSandbox)
            return null;

        var all = new List<string>();
        if (hasGlobal)
            all.AddRange(globalPatterns);
        if (hasSandbox)
            all.AddRange(sandboxPatterns!);

        return Mk8GigaBlacklist.ValidateCustomPatterns([.. all]);
    }

    public ValueTask DisposeAsync()
    {
        // Per-command state only — the sandbox container is NOT ours
        // to stop. It runs continuously under Mk8ContainerManager.
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
    }
}
