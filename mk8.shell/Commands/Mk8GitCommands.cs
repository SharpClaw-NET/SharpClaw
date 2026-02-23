using Mk8.Shell.Safety;

namespace Mk8.Shell;

/// <summary>
/// <c>git</c> command templates and word lists for <see cref="Mk8CommandWhitelist"/>.
/// <para>
/// Git was previously removed from mk8.shell entirely because its flag
/// interpreter surface was too large for a blocklist model.  The strict
/// command-template whitelist makes re-introduction safe — only the EXACT
/// templates below can be executed, and no unregistered flag can reach git.
/// </para>
/// <para>
/// <b>Defence-in-depth:</b> <see cref="Mk8PathSanitizer.ResolveForWrite"/>
/// blocks all writes to <c>.git/</c> paths, preventing hook injection
/// (<c>.git/hooks/pre-commit</c>) and config tampering (<c>.git/config</c>).
/// </para>
/// <para>
/// All data is compile-time constant.  To add or modify allowed commands,
/// edit this file and recompile — there is no runtime registration.
/// </para>
/// </summary>
public static class Mk8GitCommands
{
    // ── Word lists (edit these arrays to change what agents can use) ──

    /// <summary>
    /// Vocabulary for composing commit messages via <c>git commit -m</c>.
    /// The agent combines words from this list with spaces between them
    /// (max <see cref="Mk8CommandWhitelist.MaxComposedWords"/> words).
    /// <para>
    /// Spaces are safe — <c>ProcessStartInfo.ArgumentList.Add("Fix auth errors")</c>
    /// passes the entire string as one OS-level argument. No shell tokenizes it.
    /// </para>
    /// <para>
    /// Example compositions: <c>"Fix build errors"</c>, <c>"Add auth middleware"</c>,
    /// <c>"Refactor agent handler A"</c>, <c>"WIP"</c>.
    /// </para>
    /// </summary>
    public static readonly string[] CommitWords =
    [
        // ── Verbs ─────────────────────────────────────────────────
        "Add", "Fix", "Update", "Remove", "Delete", "Refactor",
        "Move", "Rename", "Clean", "Optimize", "Configure",
        "Wire", "Scaffold", "Extract", "Inline", "Format",
        "Implement", "Replace", "Merge", "Split", "Revert",
        "Enable", "Disable", "Restore", "Sync", "Simplify",
        "Improve", "Reorganize", "Consolidate", "Introduce",
        "Deprecate", "Drop", "Bump", "Pin", "Unpin",

        // ── Adjectives / modifiers ────────────────────────────────
        "Initial", "Minor", "Major", "Quick", "Temporary",
        "Deprecated", "Breaking", "Critical", "Partial",
        "Missing", "Unused", "Duplicate", "Stale", "Draft",
        "Experimental", "Internal", "Public", "Private",
        "New", "Old", "Default", "Custom", "Base",

        // ── Nouns — general ───────────────────────────────────────
        "commit", "migration", "migrations", "schema",
        "tests", "test", "build", "builds",
        "errors", "error", "typos", "typo",
        "code", "files", "file", "config", "configuration",
        "dependencies", "dependency", "imports", "import",
        "comments", "comment", "documentation", "docs",
        "README", "CHANGELOG",
        "endpoints", "endpoint", "commands", "command",
        "middleware", "project", "projects",
        "services", "service", "method", "methods",
        "class", "classes", "interface", "interfaces",
        "logging", "validation", "handling",
        "namespace", "namespaces", "package", "packages",
        "module", "modules", "component", "components",
        "feature", "features", "bugfix", "hotfix",
        "release", "version", "setup", "cleanup",
        "logic", "flow", "pipeline", "workflow",
        "response", "request", "route", "routes",
        "handler", "handlers", "controller",
        "query", "queries", "index", "indexes",
        "table", "tables", "column", "columns",
        "constraint", "constraints", "relation", "relations",
        "key", "keys", "property", "properties",
        "field", "fields", "parameter", "parameters",
        "type", "types", "enum", "enums",
        "seed", "data", "baseline", "snapshot",
        "template", "templates", "factory", "helper",
        "options", "settings", "constants",

        // ── Nouns — domain (SharpClaw-specific) ───────────────────
        "auth", "authentication", "authorization", "permissions",
        "agents", "agent", "channels", "channel",
        "contexts", "context", "models", "model",
        "providers", "provider", "jobs", "job",
        "tasks", "task", "skills", "skill",
        "resources", "resource", "containers", "container",
        "devices", "device", "messages", "message",
        "roles", "role", "users", "user",
        "transcription", "audio", "scheduler",
        "tokens", "token", "encryption",
        "API", "CLI", "DI", "EF", "DB", "UI",
        "REST", "gRPC", "JSON", "YAML", "XML",

        // ── Connectors / prepositions ─────────────────────────────
        "up", "in", "for", "to", "from", "with", "and", "of",
        "on", "at", "by", "as", "into", "across",

        // ── States / phrases ──────────────────────────────────────
        "work", "progress", "checkpoint", "WIP",

        // ── Single letters and digits (case-insensitive) ──────────
        "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
        "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
    ];

    /// <summary>
    /// Pre-approved branch names for <c>git checkout</c>, <c>git switch</c>.
    /// Agents cannot create or switch to arbitrary branch names.
    /// <para>
    /// <b>INTENTIONALLY EXCLUDED — protected branches:</b>
    /// <c>main</c>, <c>master</c>, <c>develop</c>, <c>staging</c>,
    /// <c>production</c>, <c>live</c>, <c>release</c>, <c>release/*</c>,
    /// <c>trunk</c>.
    /// Agents must NEVER operate on branches used for live or master
    /// development.  All agent work must happen in a dedicated
    /// feature/bugfix/hotfix branch.  Merging to protected branches
    /// requires the dangerous-shell path with human approval.
    /// </para>
    /// </summary>
    public static readonly string[] BranchNames =
    [
        // ── Protected branches — BANNED (do NOT add these) ────────
        // "main", "master", "develop", "staging", "production",
        // "live", "release", "trunk"
        // This includes release/* branches — release branches track
        // live-bound code and are equally dangerous.
        // Agents must never checkout, switch to, or create these.
        // All git write operations must happen in a safe branch.

        // Features
        "feature/auth", "feature/agents", "feature/channels",
        "feature/models", "feature/providers", "feature/jobs",
        "feature/tasks", "feature/skills", "feature/resources",
        "feature/api", "feature/cli", "feature/tests",
        "feature/docs", "feature/config", "feature/logging",
        "feature/permissions", "feature/transcription",
        "feature/shell", "feature/containers",

        // Bug fixes
        "bugfix/build", "bugfix/tests", "bugfix/auth",
        "bugfix/api", "bugfix/cli", "bugfix/data",
        "bugfix/permissions", "bugfix/models",

        // Hotfixes
        "hotfix/security", "hotfix/critical", "hotfix/data",

        // Single letters and digits (matching is case-insensitive).
        "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
        "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
    ];

    /// <summary>
    /// Pre-approved remote names for <c>git remote add</c> /
    /// <c>git remote remove</c>.  URLs come from
    /// <see cref="Mk8RuntimeConfig.GitRemoteUrls"/> (runtime).
    /// </summary>
    public static readonly string[] RemoteNames =
    [
        "origin", "upstream", "fork", "backup", "mirror",
    ];

    /// <summary>
    /// Pre-approved line range specifiers for <c>git blame -L</c>.
    /// Format: <c>start,end</c> where both are line numbers.
    /// Only a fixed set of commonly-used ranges to avoid free text.
    /// </summary>
    public static readonly string[] BlameLineRanges =
    [
        "1,10", "1,20", "1,50", "1,100",
        "1,200", "1,500", "1,1000",
    ];

    /// <summary>
    /// Pre-approved tag names for <c>git tag</c> create/delete.
    /// Agents cannot create or delete arbitrary tag names.
    /// <para>
    /// When FreeText is enabled for <c>"git tag create"</c>, the agent
    /// can write descriptive tag names subject to git-ref safety
    /// validation (no spaces, no <c>..</c>, no <c>~^:</c>, no control
    /// chars, max 128 chars). When disabled, falls back to this list.
    /// </para>
    /// </summary>
    public static readonly string[] TagNames =
    [
        // Semantic version tags
        "v0.1.0", "v0.2.0", "v0.3.0", "v0.4.0", "v0.5.0",
        "v0.6.0", "v0.7.0", "v0.8.0", "v0.9.0",
        "v1.0.0", "v1.1.0", "v1.2.0", "v1.3.0", "v1.4.0", "v1.5.0",
        "v2.0.0", "v2.1.0", "v2.2.0", "v2.3.0",
        "v3.0.0", "v3.1.0",

        // Pre-release variants
        "v0.1.0-alpha", "v0.1.0-beta", "v0.1.0-rc1", "v0.1.0-rc2",
        "v1.0.0-alpha", "v1.0.0-beta", "v1.0.0-rc1", "v1.0.0-rc2",
        "v2.0.0-alpha", "v2.0.0-beta", "v2.0.0-rc1",

        // Milestone tags
        "baseline", "checkpoint", "snapshot", "draft",
        "initial", "stable", "latest",
    ];

    // ── Registration (called once at startup by Mk8CommandWhitelist) ──

    internal static KeyValuePair<string, string[]>[] GetWordLists() =>
    [
        new("CommitWords", CommitWords),
        new("BranchNames", BranchNames),
        new("RemoteNames", RemoteNames),
        new("BlameLineRanges", BlameLineRanges),
        new("TagNames", TagNames),
    ];

    internal static Mk8AllowedCommand[] GetCommands()
    {
        var branchSlot = new Mk8Slot("branch", Mk8SlotKind.AdminWord, WordListName: "BranchNames");
        var pathSlot = new Mk8Slot("path", Mk8SlotKind.SandboxPath);

        return
        [
            // ═════════════════════════════════════════════════════════
            // READ-ONLY — no repository state is changed
            // ═════════════════════════════════════════════════════════

            new("git version", "git", ["--version"]),

            // init (no params — always runs in the sandbox $CWD)
            new("git init", "git", ["init"]),

            // status
            new("git status", "git", ["status"],
                Flags: [new("--short"), new("-s"), new("--porcelain"), new("--branch")]),

            // log (always --oneline to bound output size)
            new("git log", "git", ["log", "--oneline"],
                Flags: [
                    new("-n", new Mk8Slot("count", Mk8SlotKind.IntRange, MinValue: 1, MaxValue: 100)),
                    new("--all"),
                    new("--no-decorate"),
                    new("--graph"),
                    new("--reverse"),
                ]),

            // log for a specific file (file history)
            new("git log file", "git", ["log", "--oneline", "--"],
                Flags: [
                    new("-n", new Mk8Slot("count", Mk8SlotKind.IntRange, MinValue: 1, MaxValue: 100)),
                    new("--all"),
                    new("--no-decorate"),
                ],
                Params: [pathSlot]),

            // diff (working tree)
            new("git diff", "git", ["diff"],
                Flags: [new("--staged"), new("--cached"), new("--stat"),
                        new("--name-only"), new("--name-status"), new("--word-diff")]),
            new("git diff file", "git", ["diff"],
                Flags: [new("--staged"), new("--cached")],
                Params: [pathSlot]),

            // branch listing
            new("git branch", "git", ["branch"],
                Flags: [new("--list"), new("-a"), new("--all"), new("-r")]),

            // remote listing
            new("git remote", "git", ["remote"],
                Flags: [new("-v")]),

            // remote add/remove (URL MUST come from runtime config)
            new("git remote add", "git", ["remote", "add"],
                Params: [
                    new Mk8Slot("name", Mk8SlotKind.AdminWord, WordListName: "RemoteNames"),
                    new Mk8Slot("url", Mk8SlotKind.AdminWord, WordListName: "GitRemoteUrls"),
                ]),
            new("git remote remove", "git", ["remote", "remove"],
                Params: [
                    new Mk8Slot("name", Mk8SlotKind.AdminWord, WordListName: "RemoteNames"),
                ]),

            // rev-parse
            new("git rev-parse HEAD", "git", ["rev-parse", "HEAD"]),
            new("git rev-parse short HEAD", "git", ["rev-parse", "--short", "HEAD"]),

            // ls-files
            new("git ls-files", "git", ["ls-files"],
                Flags: [new("--modified"), new("--deleted"), new("--others"), new("--ignored")]),

            // tag listing
            new("git tag list", "git", ["tag", "--list"]),
            new("git tag list short", "git", ["tag", "-l"]),

            // describe
            new("git describe", "git", ["describe"],
                Flags: [new("--tags"), new("--always"), new("--long"),
                        new("--abbrev", new Mk8Slot("length", Mk8SlotKind.IntRange, MinValue: 1, MaxValue: 40))]),

            // ── New read-only templates ───────────────────────────────

            // stash show (completes existing stash/pop/list/drop set)
            new("git stash show", "git", ["stash", "show"],
                Flags: [new("--stat"), new("--patch"), new("-p")]),

            // blame (line-by-line attribution)
            new("git blame", "git", ["blame"],
                Flags: [
                    new("-L", new Mk8Slot("range", Mk8SlotKind.AdminWord, WordListName: "BlameLineRanges")),
                ],
                Params: [pathSlot]),

            // clean --dry-run (read-only — NO -f slot, can never delete)
            new("git clean dry-run", "git", ["clean", "-n"],
                Flags: [new("-d")]),
            new("git clean dry-run long", "git", ["clean", "--dry-run"],
                Flags: [new("-d")]),

            // count-objects (repo size statistics)
            new("git count-objects", "git", ["count-objects"],
                Flags: [new("-v"), new("-H")]),

            // cherry (unpushed commits)
            new("git cherry", "git", ["cherry"],
                Flags: [new("-v")]),

            // shortlog (contributor summary)
            new("git shortlog", "git", ["shortlog", "-sn"],
                Flags: [new("--all"), new("--no-merges")]),

            // rev-list count (total commit count)
            new("git rev-list count", "git", ["rev-list", "--count", "HEAD"]),

            // ═════════════════════════════════════════════════════════
            // WRITE — constrained to word-list values only
            // ═════════════════════════════════════════════════════════

            // add (sandbox paths only, or the fixed tokens "." / "-A")
            new("git add paths", "git", ["add"],
                Params: [new Mk8Slot("paths", Mk8SlotKind.SandboxPath, Variadic: true)]),
            new("git add all dot", "git", ["add", "."]),
            new("git add all flag", "git", ["add", "-A"]),

            // commit — FreeText with ComposedWords fallback.
            // When FreeText is enabled (via base.env/sandbox env), the
            // agent can write free-form commit messages (sanitized: max
            // length, control chars blocked, secret patterns blocked,
            // gigablacklist enforced). When disabled, falls back to
            // ComposedWords from the CommitWords vocabulary.
            new("git commit", "git", ["commit"],
                Flags: [new("-m", new Mk8Slot("message", Mk8SlotKind.FreeText,
                    WordListName: "CommitWords", MaxFreeTextLength: 200))]),

            // stash
            new("git stash", "git", ["stash"]),
            new("git stash pop", "git", ["stash", "pop"]),
            new("git stash list", "git", ["stash", "list"],
                Flags: [new("--oneline")]),
            new("git stash drop", "git", ["stash", "drop"]),

            // tag create — FreeText with TagNames fallback.
            // Lightweight tag (no message):
            new("git tag create", "git", ["tag"],
                Params: [new Mk8Slot("name", Mk8SlotKind.FreeText,
                    WordListName: "TagNames", MaxFreeTextLength: 128)]),

            // Annotated tag with message — FreeText for both name and message.
            new("git tag annotated", "git", ["tag", "-a"],
                Flags: [new("-m", new Mk8Slot("message", Mk8SlotKind.FreeText,
                    WordListName: "CommitWords", MaxFreeTextLength: 200))],
                Params: [new Mk8Slot("name", Mk8SlotKind.FreeText,
                    WordListName: "TagNames", MaxFreeTextLength: 128)]),

            // tag delete (local only — push requires dangerous-shell path)
            new("git tag delete", "git", ["tag", "-d"],
                Params: [new Mk8Slot("name", Mk8SlotKind.FreeText,
                    WordListName: "TagNames", MaxFreeTextLength: 128)]),

            // checkout / switch (branch MUST come from word list)
            new("git checkout branch", "git", ["checkout"],
                Params: [branchSlot]),
            new("git checkout new branch", "git", ["checkout", "-b"],
                Params: [branchSlot]),
            new("git switch branch", "git", ["switch"],
                Params: [branchSlot]),
            new("git switch new branch", "git", ["switch", "-c"],
                Params: [branchSlot]),

            // clone (URL MUST come from runtime config GitCloneUrls)
            new("git clone", "git", ["clone"],
                Params: [
                    new Mk8Slot("url", Mk8SlotKind.AdminWord, WordListName: "GitCloneUrls"),
                ]),
            new("git clone into path", "git", ["clone"],
                Params: [
                    new Mk8Slot("url", Mk8SlotKind.AdminWord, WordListName: "GitCloneUrls"),
                    pathSlot,
                ]),

            // push (branch MUST come from word list — agents can only
            // push the branches they can checkout/create)
            new("git push", "git", ["push"],
                Params: [
                    new Mk8Slot("remote", Mk8SlotKind.AdminWord, WordListName: "RemoteNames"),
                    branchSlot,
                ]),
            new("git push set-upstream", "git", ["push"],
                Flags: [new("-u"), new("--set-upstream")],
                Params: [
                    new Mk8Slot("remote", Mk8SlotKind.AdminWord, WordListName: "RemoteNames"),
                    branchSlot,
                ]),
            new("git push tags", "git", ["push", "--tags"],
                Params: [
                    new Mk8Slot("remote", Mk8SlotKind.AdminWord, WordListName: "RemoteNames"),
                ]),

            // pull (remote + branch from word lists)
            new("git pull", "git", ["pull"],
                Flags: [new("--rebase"), new("--no-rebase"), new("--ff-only")],
                Params: [
                    new Mk8Slot("remote", Mk8SlotKind.AdminWord, WordListName: "RemoteNames"),
                    branchSlot,
                ]),

            // merge (branch MUST come from word list — agents can only
            // merge branches they're allowed to work with)
            new("git merge", "git", ["merge"],
                Flags: [new("--no-ff"), new("--ff-only"), new("--squash"),
                        new("-m", new Mk8Slot("message", Mk8SlotKind.FreeText,
                            WordListName: "CommitWords", MaxFreeTextLength: 200))],
                Params: [branchSlot]),
            new("git merge abort", "git", ["merge", "--abort"]),

            // ═════════════════════════════════════════════════════════
            // NOT WHITELISTED (require dangerous-shell path):
            //   rebase, reset, clean -f, config, submodule, am,
            //   apply, filter-branch, cherry-pick, bisect, gc,
            //   fsck, reflog
            // ═════════════════════════════════════════════════════════
        ];
    }
}
