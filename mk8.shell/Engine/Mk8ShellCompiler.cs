using Mk8.Shell.Safety;

namespace Mk8.Shell.Engine;

/// <summary>
/// Compiles an <see cref="Mk8ShellScript"/> into a sequence of
/// <see cref="Mk8CompiledCommand"/>s.
/// <para>
/// <b>RED-TEAM DESIGN DECISIONS</b>:
/// <list type="bullet">
///   <item>
///     ALL file I/O, text, HTTP, env, and sysinfo verbs compile to
///     in-memory markers. The executor handles them via .NET APIs
///     (File.ReadAllTextAsync, HttpClient, etc.) — never via
///     external processes like cat, type, or powershell.
///   </item>
///   <item>
///     Only ProcRun produces real process invocations. This is the
///     only code path that calls ProcessStartInfo.
///   </item>
///   <item>
///     ProcRun uses a strict command-template whitelist
///     (<see cref="Mk8CommandWhitelist"/>) — only explicitly
///     registered command patterns with typed parameter slots are
///     allowed.  The agent cannot inject free text into any argument.
///   </item>
///   <item>
///     The compiler never emits bash, cmd, or powershell as an
///     executable. Period.
///   </item>
///   <item>
///     <c>$PREV</c> is blocked in ProcRun arguments via
///     <see cref="Mk8VariableResolver.ResolveArgsForProc"/>.
///   </item>
///   <item>
///     ForEach/If/Batch verbs are expanded BEFORE compilation by
///     <see cref="Mk8ScriptExpander"/>. The compiler only sees
///     primitive verbs.
///   </item>
/// </list>
/// </para>
/// </summary>
public sealed class Mk8ShellCompiler
{
    private readonly IMk8FragmentRegistry? _fragmentRegistry;
    private readonly Mk8CommandWhitelist _whitelist;

    /// <summary>
    /// Set during <see cref="Compile(Mk8ShellScript, Mk8WorkspaceContext, Mk8ExecutionOptions)"/>
    /// so <see cref="CompileProcRun"/> can validate paths against the sandbox.
    /// </summary>
    private string _sandboxRoot = "";

    /// <summary>
    /// Set during <see cref="Compile(Mk8ShellScript, Mk8WorkspaceContext, Mk8ExecutionOptions)"/>
    /// so diagnostic verbs (<c>Mk8Info</c>, <c>Mk8Env</c>) can access
    /// the workspace context at compile time.
    /// </summary>
    private Mk8WorkspaceContext? _workspace;

    public Mk8ShellCompiler(Mk8CommandWhitelist? whitelist = null)
    {
        _whitelist = whitelist ?? Mk8CommandWhitelist.CreateDefault();
    }

    public Mk8ShellCompiler(
        IMk8FragmentRegistry fragmentRegistry,
        Mk8CommandWhitelist? whitelist = null)
    {
        _fragmentRegistry = fragmentRegistry;
        _whitelist = whitelist ?? Mk8CommandWhitelist.CreateDefault();
    }

    /// <summary>
    /// Compile with full workspace context and variable resolution.
    /// Runs the expander first, then compiles the flat result.
    /// </summary>
    public Mk8CompiledScript Compile(
        Mk8ShellScript script,
        Mk8WorkspaceContext workspace,
        Mk8ExecutionOptions effectiveOptions)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(workspace);

        _sandboxRoot = workspace.SandboxRoot;
        _workspace = workspace;

        if (script.Operations.Count == 0)
            throw new Mk8CompileException(Mk8ShellVerb.FileRead,
                "Script has no operations.");

        var variables = Mk8VariableResolver.BuildVariables(workspace);

        // Phase 1: Expand ForEach/If/Batch/Include into flat primitive list.
        var expanded = Mk8ScriptExpander.Expand(script, variables, _fragmentRegistry);

        // Phase 2: Validate named captures and build ProcRun-blocked vars.
        var processCaptures = Mk8VariableResolver.ValidateCaptures(expanded.Operations);
        var procRunBlocked = Mk8VariableResolver.BuildProcRunBlockedVars(processCaptures);

        // Phase 3: Validate labels and jump targets.
        var labelIndex = Mk8LabelValidator.Validate(expanded.Operations);

        // Phase 4: Validate FileTemplate and FilePatch definitions.
        ValidateTemplatesAndPatches(expanded.Operations);

        // Phase 5: Resolve variables and compile each operation.
        var commands = new List<Mk8CompiledCommand>(expanded.Operations.Count);
        for (var i = 0; i < expanded.Operations.Count; i++)
        {
            var op = expanded.Operations[i];

            // Use blocked-var resolution for ProcRun.
            var resolvedArgs = op.Verb == Mk8ShellVerb.ProcRun
                ? Mk8VariableResolver.ResolveArgsForProc(op.Args, variables, procRunBlocked)
                : Mk8VariableResolver.ResolveArgs(op.Args, variables);

            // Gigablacklist — unconditional on ALL resolved args.
            _whitelist.GigaBlacklist.EnforceAllInMemory(op.Verb.ToString(), resolvedArgs);

            // Path validation against sandbox.
            resolvedArgs = ValidatePathArgs(op.Verb, resolvedArgs, workspace.SandboxRoot);

            // Validate FileTemplate source path is in sandbox.
            if (op.Verb == Mk8ShellVerb.FileTemplate && op.Template is not null)
            {
                Mk8PathSanitizer.Resolve(
                    Mk8VariableResolver.ResolveArg(op.Template.Source, variables),
                    workspace.SandboxRoot);
            }

            var resolvedOp = op with { Args = resolvedArgs };
            commands.Add(CompileOperation(resolvedOp));
        }

        // Phase 6: Compile cleanup operations (same pipeline).
        List<Mk8CompiledCommand>? cleanupCommands = null;
        if (expanded.Cleanup is { Count: > 0 })
        {
            cleanupCommands = new List<Mk8CompiledCommand>(expanded.Cleanup.Count);
            for (var i = 0; i < expanded.Cleanup.Count; i++)
            {
                var op = expanded.Cleanup[i];

                var resolvedArgs = op.Verb == Mk8ShellVerb.ProcRun
                    ? Mk8VariableResolver.ResolveArgsForProc(op.Args, variables, procRunBlocked)
                    : Mk8VariableResolver.ResolveArgs(op.Args, variables);

                // Gigablacklist — unconditional on cleanup args too.
                _whitelist.GigaBlacklist.EnforceAllInMemory(op.Verb.ToString(), resolvedArgs);

                resolvedArgs = ValidatePathArgs(op.Verb, resolvedArgs, workspace.SandboxRoot);

                var resolvedOp = op with { Args = resolvedArgs };
                cleanupCommands.Add(CompileOperation(resolvedOp));
            }
        }

        return new Mk8CompiledScript(
            commands,
            effectiveOptions,
            workspace,
            cleanupCommands,
            labelIndex.Count > 0 ? labelIndex : null);
    }

    /// <summary>
    /// Compile without workspace (testing only).
    /// </summary>
    public Mk8CompiledScript Compile(Mk8ShellScript script)
    {
        ArgumentNullException.ThrowIfNull(script);

        _sandboxRoot = Directory.GetCurrentDirectory();

        var commands = new List<Mk8CompiledCommand>(script.Operations.Count);
        foreach (var op in script.Operations)
            commands.Add(CompileOperation(op));

        var options = script.Options ?? Mk8ExecutionOptions.Default;
        var workspace = new Mk8WorkspaceContext(
            "test",
            Directory.GetCurrentDirectory(),
            Directory.GetCurrentDirectory(),
            Environment.UserName,
            new Dictionary<string, string>());

        return new Mk8CompiledScript(commands, options, workspace);
    }

    public Mk8CompiledCommand CompileOperation(Mk8ShellOperation op)
    {
        ArgumentNullException.ThrowIfNull(op);

        return op.Verb switch
        {
            // ── ALL file/dir ops → in-memory ──────────────────────
            Mk8ShellVerb.FileRead   => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.FileWrite  => InMemory(op.Verb, RequireArgs(op, 2, 2)),
            Mk8ShellVerb.FileAppend => InMemory(op.Verb, RequireArgs(op, 2, 2)),
            Mk8ShellVerb.FileDelete => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.FileExists => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.FileList   => InMemory(op.Verb, RequireArgs(op, 1, 2)),
            Mk8ShellVerb.FileCopy   => InMemory(op.Verb, RequireArgs(op, 2, 2)),
            Mk8ShellVerb.FileMove   => InMemory(op.Verb, RequireArgs(op, 2, 2)),
            Mk8ShellVerb.FileInfo   => InMemory(op.Verb, RequireArgs(op, 1, 1)),

            Mk8ShellVerb.DirCreate  => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.DirDelete  => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.DirList    => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.DirExists  => InMemory(op.Verb, RequireArgs(op, 1, 1)),

            // ── Process (the ONLY verb that spawns a process) ─────
            Mk8ShellVerb.ProcRun    => CompileProcRun(op.Args, _sandboxRoot),

            // Git verbs removed — all git operations now require the
            // dangerous-shell path.  See Mk8ShellVerb comments.

            // ── ALL remaining → in-memory ─────────────────────────
            Mk8ShellVerb.HttpGet    => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.HttpPost   => InMemory(op.Verb, RequireArgs(op, 1, 2)),
            Mk8ShellVerb.HttpPut    => InMemory(op.Verb, RequireArgs(op, 1, 2)),
            Mk8ShellVerb.HttpDelete => InMemory(op.Verb, RequireArgs(op, 1, 1)),

            Mk8ShellVerb.TextRegex   => InMemory(op.Verb, RequireArgs(op, 2, 2)),
            Mk8ShellVerb.TextReplace => InMemory(op.Verb, RequireArgs(op, 3, 3)),
            Mk8ShellVerb.JsonParse   => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.JsonQuery   => InMemory(op.Verb, RequireArgs(op, 2, 2)),

            // ── Extended text manipulation (pure string ops) ──────
            Mk8ShellVerb.TextSplit        => InMemory(op.Verb, RequireArgs(op, 2, 2)),
            Mk8ShellVerb.TextJoin         => InMemory(op.Verb, RequireArgs(op, 2, 33)),
            Mk8ShellVerb.TextTrim         => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.TextLength       => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.TextSubstring    => InMemory(op.Verb, RequireArgs(op, 2, 3)),
            Mk8ShellVerb.TextLines        => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.TextToUpper      => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.TextToLower      => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.TextBase64Encode => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.TextBase64Decode => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.TextUrlEncode    => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.TextUrlDecode    => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.TextHtmlEncode   => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.TextContains     => InMemory(op.Verb, RequireArgs(op, 2, 2)),
            Mk8ShellVerb.TextStartsWith   => InMemory(op.Verb, RequireArgs(op, 2, 2)),
            Mk8ShellVerb.TextEndsWith     => InMemory(op.Verb, RequireArgs(op, 2, 2)),
            Mk8ShellVerb.TextMatch        => InMemory(op.Verb, RequireArgs(op, 2, 2)),
            Mk8ShellVerb.TextHash         => CompileTextHash(op),
            Mk8ShellVerb.TextSort         => InMemory(op.Verb, RequireArgs(op, 1, 2)),
            Mk8ShellVerb.TextUniq         => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.TextCount        => InMemory(op.Verb, RequireArgs(op, 1, 2)),
            Mk8ShellVerb.TextIndexOf      => InMemory(op.Verb, RequireArgs(op, 2, 2)),
            Mk8ShellVerb.TextLastIndexOf  => InMemory(op.Verb, RequireArgs(op, 2, 2)),
            Mk8ShellVerb.TextRemove       => InMemory(op.Verb, RequireArgs(op, 2, 2)),
            Mk8ShellVerb.TextWordCount    => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.TextReverse      => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.TextPadLeft      => CompileTextPad(op),
            Mk8ShellVerb.TextPadRight     => CompileTextPad(op),
            Mk8ShellVerb.TextRepeat       => CompileTextRepeat(op),
            Mk8ShellVerb.JsonMerge        => InMemory(op.Verb, RequireArgs(op, 2, 2)),
            Mk8ShellVerb.JsonKeys         => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.JsonCount        => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.JsonType         => InMemory(op.Verb, RequireArgs(op, 1, 1)),

            // ── JSON construction/mutation ─────────────────────────
            Mk8ShellVerb.JsonFromPairs    => CompileJsonFromPairs(op),
            Mk8ShellVerb.JsonSet          => InMemory(op.Verb, RequireArgs(op, 3, 3)),
            Mk8ShellVerb.JsonRemoveKey    => InMemory(op.Verb, RequireArgs(op, 2, 2)),
            Mk8ShellVerb.JsonGet          => InMemory(op.Verb, RequireArgs(op, 2, 2)),
            Mk8ShellVerb.JsonCompact      => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.JsonStringify    => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.JsonArrayFrom    => CompileJsonArrayFrom(op),

            // ── File inspection (read-only, in-memory) ────────────
            Mk8ShellVerb.FileLineCount => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.FileHead      => CompileFileHeadTail(op),
            Mk8ShellVerb.FileTail      => CompileFileHeadTail(op),
            Mk8ShellVerb.FileSearch    => InMemory(op.Verb, RequireArgs(op, 2, 2)),
            Mk8ShellVerb.FileDiff      => InMemory(op.Verb, RequireArgs(op, 2, 2)),
            Mk8ShellVerb.FileGlob      => CompileFileGlob(op),

            // ── Directory inspection ──────────────────────────────
            Mk8ShellVerb.DirFileCount  => InMemory(op.Verb, RequireArgs(op, 1, 2)),
            Mk8ShellVerb.DirEmpty      => InMemory(op.Verb, RequireArgs(op, 1, 1)),

            // ── File type detection (read-only, in-memory) ────────
            Mk8ShellVerb.FileMimeType  => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.FileEncoding  => InMemory(op.Verb, RequireArgs(op, 1, 1)),

            // ── File comparison (read-only) ───────────────────────
            Mk8ShellVerb.FileEqual     => InMemory(op.Verb, RequireArgs(op, 2, 2)),
            Mk8ShellVerb.FileChecksum  => CompileFileChecksum(op),

            // ── Path manipulation (pure string, no I/O) ───────────
            Mk8ShellVerb.PathJoin      => InMemory(op.Verb, RequireArgs(op, 2, 16)),
            Mk8ShellVerb.PathDir       => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.PathFile      => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.PathExt       => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.PathStem      => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.PathChangeExt => InMemory(op.Verb, RequireArgs(op, 2, 2)),

            // ── Identity/value generation ─────────────────────────
            Mk8ShellVerb.GuidNew       => InMemory(op.Verb, []),
            Mk8ShellVerb.GuidNewShort  => InMemory(op.Verb, []),
            Mk8ShellVerb.RandomInt     => CompileRandomInt(op),

            // ── Time arithmetic ───────────────────────────────────
            Mk8ShellVerb.TimeFormat    => CompileTimeFormat(op),
            Mk8ShellVerb.TimeParse     => InMemory(op.Verb, RequireArgs(op, 1, 2)),
            Mk8ShellVerb.TimeAdd       => InMemory(op.Verb, RequireArgs(op, 2, 2)),
            Mk8ShellVerb.TimeDiff      => InMemory(op.Verb, RequireArgs(op, 2, 2)),

            // ── Version comparison ────────────────────────────────
            Mk8ShellVerb.VersionCompare => InMemory(op.Verb, RequireArgs(op, 2, 2)),
            Mk8ShellVerb.VersionParse   => InMemory(op.Verb, RequireArgs(op, 1, 1)),

            // ── Encoding/conversion ───────────────────────────────
            Mk8ShellVerb.HexEncode     => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.HexDecode     => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.BaseConvert   => InMemory(op.Verb, RequireArgs(op, 3, 3)),

            // ── Regex capture groups ──────────────────────────────
            Mk8ShellVerb.TextRegexGroups => InMemory(op.Verb, RequireArgs(op, 2, 2)),

            // ── Script control/debugging ──────────────────────────
            Mk8ShellVerb.Echo          => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.Sleep         => CompileSleep(op),
            Mk8ShellVerb.Assert        => InMemory(op.Verb, RequireArgs(op, 2, 3)),
            Mk8ShellVerb.Fail          => InMemory(op.Verb, RequireArgs(op, 1, 1)),

            Mk8ShellVerb.EnvGet     => InMemory(op.Verb, RequireArgs(op, 1, 1)),

            Mk8ShellVerb.SysWhoAmI   => InMemory(op.Verb, []),
            Mk8ShellVerb.SysPwd      => InMemory(op.Verb, []),
            Mk8ShellVerb.SysHostname => InMemory(op.Verb, []),
            Mk8ShellVerb.SysUptime   => InMemory(op.Verb, []),
            Mk8ShellVerb.SysDate     => InMemory(op.Verb, []),
            Mk8ShellVerb.SysDiskUsage => InMemory(op.Verb, RequireArgs(op, 0, 1)),
            Mk8ShellVerb.SysDirSize   => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.SysMemory    => InMemory(op.Verb, []),
            Mk8ShellVerb.SysProcessList => InMemory(op.Verb, []),

            // ── Extended system info ──────────────────────────────
            Mk8ShellVerb.SysDateFormat  => CompileSysDateFormat(op),
            Mk8ShellVerb.SysTimestamp   => InMemory(op.Verb, []),
            Mk8ShellVerb.SysOsInfo      => InMemory(op.Verb, []),
            Mk8ShellVerb.SysCpuCount    => InMemory(op.Verb, []),
            Mk8ShellVerb.SysTempDir     => InMemory(op.Verb, []),

            // ── Advanced filesystem (in-memory) ───────────────────
            Mk8ShellVerb.FileTemplate => CompileFileTemplate(op),
            Mk8ShellVerb.FilePatch    => CompileFilePatch(op),
            Mk8ShellVerb.FileHash     => CompileFileHash(op),

            // ── Recursive directory listing (in-memory, read-only) ─
            Mk8ShellVerb.DirTree      => CompileDirTree(op),

            // ── Clipboard (in-memory, write-only) ─────────────────
            Mk8ShellVerb.ClipboardSet => InMemory(op.Verb, RequireArgs(op, 1, 1)),

            // ── Math (in-memory, safe arithmetic) ─────────────────
            Mk8ShellVerb.MathEval     => CompileMathEval(op),

            // ── URL validation (in-memory) ────────────────────────
            Mk8ShellVerb.OpenUrl      => CompileOpenUrl(op),

            // ── Network diagnostics (in-memory) ───────────────────
            Mk8ShellVerb.NetPing      => CompileNetPing(op),
            Mk8ShellVerb.NetDns       => CompileNetDns(op),
            Mk8ShellVerb.NetTlsCert   => CompileNetTlsCert(op),
            Mk8ShellVerb.NetHttpStatus => CompileNetHttpStatus(op),
            Mk8ShellVerb.NetTcpConnect => CompileNetTcpConnect(op),
            Mk8ShellVerb.HttpLatency   => CompileHttpLatency(op),

            // ── Sysadmin: file age/staleness ──────────────────────
            Mk8ShellVerb.FileAge       => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.FileNewerThan => CompileFileNewerThan(op),

            // ── Sysadmin: process search ──────────────────────────
            Mk8ShellVerb.ProcessFind   => InMemory(op.Verb, RequireArgs(op, 1, 1)),

            // ── Sysadmin: system discovery ────────────────────────
            Mk8ShellVerb.SysDriveList  => InMemory(op.Verb, []),
            Mk8ShellVerb.SysNetInfo    => InMemory(op.Verb, []),
            Mk8ShellVerb.EnvList       => InMemory(op.Verb, []),

            // ── Sysadmin: regex file search ───────────────────────
            Mk8ShellVerb.FileSearchRegex => InMemory(op.Verb, RequireArgs(op, 2, 2)),

            // ── Sysadmin: tabular text ────────────────────────────
            Mk8ShellVerb.TextColumn    => CompileTextColumn(op),
            Mk8ShellVerb.TextTable     => InMemory(op.Verb, RequireArgs(op, 1, 2)),

            // ── Sysadmin: directory comparison ────────────────────
            Mk8ShellVerb.DirCompare    => InMemory(op.Verb, RequireArgs(op, 2, 2)),
            Mk8ShellVerb.DirHash       => CompileDirHash(op),

            // ── Sysadmin: human-readable formatting ───────────────
            Mk8ShellVerb.FormatBytes    => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.FormatDuration => InMemory(op.Verb, RequireArgs(op, 1, 1)),

            // ── Sysadmin: system log viewing (read-only, redacted) ─
            Mk8ShellVerb.SysLogRead    => CompileSysLogRead(op),
            Mk8ShellVerb.SysLogSources => InMemory(op.Verb, []),

            // ── Sysadmin: service status (read-only) ──────────────
            Mk8ShellVerb.SysServiceList   => InMemory(op.Verb, RequireArgs(op, 0, 1)),
            Mk8ShellVerb.SysServiceStatus => InMemory(op.Verb, RequireArgs(op, 1, 1)),

            // ── Archive extraction (in-memory, pre-validated) ─────
            Mk8ShellVerb.ArchiveExtract => CompileArchiveExtract(op),

            // ── mk8.shell introspection (compile-time resolved) ───
            Mk8ShellVerb.Mk8Blacklist  => CompileMk8Blacklist(op),
            Mk8ShellVerb.Mk8Vocab     => CompileMk8Vocab(op),
            Mk8ShellVerb.Mk8VocabList => CompileMk8VocabList(op),
            Mk8ShellVerb.Mk8FreeText  => CompileMk8FreeText(op),
            Mk8ShellVerb.Mk8Env       => CompileMk8Env(op),
            Mk8ShellVerb.Mk8Info      => CompileMk8Info(op),
            Mk8ShellVerb.Mk8Templates => CompileMk8Templates(op),
            Mk8ShellVerb.Mk8Verbs     => CompileMk8Verbs(op),
            Mk8ShellVerb.Mk8Skills    => CompileMk8EmbeddedDoc(op, Mk8ShellVerb.Mk8Skills, "Mk8.Shell.mk8.shell.skill.txt"),
            Mk8ShellVerb.Mk8Docs      => CompileMk8EmbeddedDoc(op, Mk8ShellVerb.Mk8Docs, "Mk8.Shell.mk8.shell.md"),

            // ── Batch/Control/Include verbs should never reach the compiler ─
            Mk8ShellVerb.ForEach or Mk8ShellVerb.If
            or Mk8ShellVerb.FileWriteMany or Mk8ShellVerb.FileCopyMany
            or Mk8ShellVerb.FileDeleteMany or Mk8ShellVerb.Include
                => throw new Mk8CompileException(op.Verb,
                    $"'{op.Verb}' must be expanded before compilation. " +
                    "This is an internal error — the expander should have " +
                    "removed all control flow, batch, and include verbs."),

            _ => throw new Mk8CompileException(op.Verb,
                $"Unknown verb '{op.Verb}'. This verb is not recognized by the mk8.shell compiler.\n" +
                "Run { \"verb\": \"Mk8Verbs\", \"args\": [] } to see all available verbs, " +
                "or { \"verb\": \"Mk8Skills\", \"args\": [] } for the full verb reference with args and descriptions.")
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // ProcRun — the ONLY external process path
    // ═══════════════════════════════════════════════════════════════
    //
    // ProcRun uses a strict command-template whitelist.  Every
    // invocation must match a registered Mk8AllowedCommand template
    // exactly — there is no "allowed binary + blocked flags" fallback.
    // The agent cannot inject free text into any argument position.
    //

    private Mk8CompiledCommand CompileProcRun(string[] args, string sandboxRoot)
    {
        if (args.Length < 1 || args.Length > 64)
            throw new Mk8CompileException(Mk8ShellVerb.ProcRun,
                $"Expected 1–64 argument(s), got {args.Length}.\n" +
                "ProcRun args must be: [binary, arg0, arg1, ...]. The first arg is " +
                "always the binary name, followed by its arguments.\n" +
                "  ✓ Correct: { \"verb\": \"ProcRun\", \"args\": [\"dotnet\", \"build\"] }\n" +
                "  ✗ Wrong:   { \"verb\": \"ProcRun\", \"args\": [] } (no binary specified)\n" +
                "Run { \"verb\": \"Mk8Templates\", \"args\": [] } to see all allowed commands.");

        var binary = args[0];
        var procArgs = args.Length > 1 ? args[1..] : [];

        var error = _whitelist.Validate(binary, procArgs, sandboxRoot);
        if (error is not null)
            throw new Mk8CompileException(Mk8ShellVerb.ProcRun, error);

        return new Mk8CompiledCommand(Mk8CommandKind.Process, binary, procArgs);
    }

    // ═══════════════════════════════════════════════════════════════
    // Security: path validation per verb
    // ═══════════════════════════════════════════════════════════════

    private static string[] ValidatePathArgs(
        Mk8ShellVerb verb, string[] args, string sandboxRoot)
    {
        return verb switch
        {
            Mk8ShellVerb.FileRead or Mk8ShellVerb.FileExists
            or Mk8ShellVerb.FileList or Mk8ShellVerb.FileDelete
            or Mk8ShellVerb.DirList or Mk8ShellVerb.DirExists
            or Mk8ShellVerb.DirCreate or Mk8ShellVerb.DirDelete
            or Mk8ShellVerb.FileHash
            or Mk8ShellVerb.DirTree
            or Mk8ShellVerb.FileInfo
            or Mk8ShellVerb.SysDirSize
            or Mk8ShellVerb.FileLineCount
            or Mk8ShellVerb.FileHead or Mk8ShellVerb.FileTail
            or Mk8ShellVerb.DirFileCount
            or Mk8ShellVerb.FileGlob
            or Mk8ShellVerb.DirEmpty
            or Mk8ShellVerb.FileMimeType
            or Mk8ShellVerb.FileEncoding
            or Mk8ShellVerb.FileAge
            or Mk8ShellVerb.FileSearchRegex
                => ValidateReadPaths(args, sandboxRoot),

            // FileSearch: args[0] = path (read), args[1] = search literal (not a path).
            Mk8ShellVerb.FileSearch
                => ValidateReadPaths(args, sandboxRoot),

            // FileDiff: both args are read paths.
            Mk8ShellVerb.FileDiff
                => ValidateDualReadPaths(args, sandboxRoot),

            // FileEqual: both args are read paths.
            Mk8ShellVerb.FileEqual
                => ValidateDualReadPaths(args, sandboxRoot),

            // FileChecksum: args[0] = path (read), args[1] = expected hash (not a path), args[2] = algo (not a path).
            Mk8ShellVerb.FileChecksum
                => ValidateReadPaths(args, sandboxRoot),

            // FileNewerThan: args[0] = path (read), args[1] = seconds (not a path).
            Mk8ShellVerb.FileNewerThan
                => ValidateReadPaths(args, sandboxRoot),

            // DirCompare: both args are read paths.
            Mk8ShellVerb.DirCompare
                => ValidateDualReadPaths(args, sandboxRoot),

            // DirHash: args[0] = path (read), args[1..] not paths.
            Mk8ShellVerb.DirHash
                => ValidateReadPaths(args, sandboxRoot),

            Mk8ShellVerb.SysDiskUsage when args.Length > 0
                => ValidateReadPaths(args, sandboxRoot),

            // ArchiveExtract: args[0] = archive (read), args[1] = output dir (write).
            Mk8ShellVerb.ArchiveExtract
                => ValidateArchiveExtractPaths(args, sandboxRoot),

            Mk8ShellVerb.FileWrite or Mk8ShellVerb.FileAppend
                => ValidateWritePaths(args, sandboxRoot),

            Mk8ShellVerb.FileCopy or Mk8ShellVerb.FileMove
                => ValidateCopyMovePaths(args, sandboxRoot),

            // FileTemplate: args[0] is the output path (write target).
            Mk8ShellVerb.FileTemplate
                => ValidateWritePaths(args, sandboxRoot),

            // FilePatch: args[0] is the target file (read + write).
            Mk8ShellVerb.FilePatch
                => ValidateWritePaths(args, sandboxRoot),

            _ => args
        };
    }

    private static string[] ValidateReadPaths(string[] args, string sandboxRoot)
    {
        if (args.Length > 0)
            args[0] = Mk8PathSanitizer.Resolve(args[0], sandboxRoot);
        return args;
    }

    private static string[] ValidateWritePaths(string[] args, string sandboxRoot)
    {
        if (args.Length > 0)
            args[0] = Mk8PathSanitizer.ResolveForWrite(args[0], sandboxRoot);
        return args;
    }

    private static string[] ValidateCopyMovePaths(string[] args, string sandboxRoot)
    {
        if (args.Length > 0)
            args[0] = Mk8PathSanitizer.Resolve(args[0], sandboxRoot);
        if (args.Length > 1)
            args[1] = Mk8PathSanitizer.ResolveForWrite(args[1], sandboxRoot);
        return args;
    }

    private static string[] ValidateDualReadPaths(string[] args, string sandboxRoot)
    {
        if (args.Length > 0)
            args[0] = Mk8PathSanitizer.Resolve(args[0], sandboxRoot);
        if (args.Length > 1)
            args[1] = Mk8PathSanitizer.Resolve(args[1], sandboxRoot);
        return args;
    }

    // ═══════════════════════════════════════════════════════════════
    // New verb compilation (all in-memory — no external processes)
    // ═══════════════════════════════════════════════════════════════

    private static Mk8CompiledCommand CompileFileTemplate(Mk8ShellOperation op)
    {
        // args[0] = output path (already validated by ValidatePathArgs).
        RequireArgs(op, 1, 1);

        if (op.Template is null)
            throw new Mk8CompileException(Mk8ShellVerb.FileTemplate,
                "FileTemplate verb requires a 'template' definition in the operation.\n" +
                "  ✓ Correct: { \"verb\": \"FileTemplate\", \"args\": [\"$WORKSPACE/out.json\"], " +
                "\"template\": { \"source\": \"$WORKSPACE/tmpl.json\", \"values\": { \"KEY\": \"val\" } } }\n" +
                "  ✗ Wrong:   { \"verb\": \"FileTemplate\", \"args\": [\"$WORKSPACE/out.json\"] } (missing template field)");

        if (string.IsNullOrWhiteSpace(op.Template.Source))
            throw new Mk8CompileException(Mk8ShellVerb.FileTemplate,
                "Template 'source' path is required.");

        if (op.Template.Values.Count > Mk8FileTemplate.MaxKeys)
            throw new Mk8CompileException(Mk8ShellVerb.FileTemplate,
                $"Template has {op.Template.Values.Count} keys, " +
                $"exceeding the limit of {Mk8FileTemplate.MaxKeys}.");

        // Ensure no variable references ($PREV etc.) inside template values.
        foreach (var (key, value) in op.Template.Values)
        {
            if (value.Contains('$'))
                throw new Mk8CompileException(Mk8ShellVerb.FileTemplate,
                    $"Template value for key '{key}' contains a '$' character.\n" +
                    "Variable expansion inside template values is blocked to prevent " +
                    "injection (e.g., {{$PREV}} attack).\n" +
                    "  ✓ Correct: \"values\": { \"PORT\": \"8080\" } (literal string)\n" +
                    $"  ✗ Wrong:   \"values\": {{ \"{key}\": \"{value}\" }} (contains '$')\n" +
                    "Use literal values only. If you need to insert captured output, " +
                    "first write it to a file with FileWrite, then read it separately.");
        }

        return InMemory(Mk8ShellVerb.FileTemplate, op.Args);
    }

    private static Mk8CompiledCommand CompileFilePatch(Mk8ShellOperation op)
    {
        // args[0] = target file path (already validated by ValidatePathArgs).
        RequireArgs(op, 1, 1);

        if (op.Patches is null || op.Patches.Count == 0)
            throw new Mk8CompileException(Mk8ShellVerb.FilePatch,
                "FilePatch verb requires at least one patch entry.\n" +
                "  ✓ Correct: { \"verb\": \"FilePatch\", \"args\": [\"$WORKSPACE/config.yaml\"], " +
                "\"patches\": [{ \"find\": \"port: 3000\", \"replace\": \"port: 8080\" }] }\n" +
                "  ✗ Wrong:   { \"verb\": \"FilePatch\", \"args\": [\"$WORKSPACE/config.yaml\"] } (missing patches field)");

        if (op.Patches.Count > Mk8PatchEntry.MaxPatches)
            throw new Mk8CompileException(Mk8ShellVerb.FilePatch,
                $"FilePatch has {op.Patches.Count} patches, " +
                $"exceeding the limit of {Mk8PatchEntry.MaxPatches}.");

        foreach (var patch in op.Patches)
        {
            if (string.IsNullOrEmpty(patch.Find))
                throw new Mk8CompileException(Mk8ShellVerb.FilePatch,
                    "Patch 'find' value cannot be empty. Each patch must have a " +
                    "non-empty literal string to search for.\n" +
                    "  ✓ Correct: { \"find\": \"debug: true\", \"replace\": \"debug: false\" }\n" +
                    "  ✗ Wrong:   { \"find\": \"\", \"replace\": \"anything\" }");

            // No variable expansion inside patch values.
            if (patch.Find.Contains('$') || patch.Replace.Contains('$'))
                throw new Mk8CompileException(Mk8ShellVerb.FilePatch,
                    "Patch find/replace values contain a '$' character.\n" +
                    "Variable expansion inside patches is blocked to prevent injection.\n" +
                    "  ✓ Correct: { \"find\": \"port: 3000\", \"replace\": \"port: 8080\" }\n" +
                    "  ✗ Wrong:   { \"find\": \"$PREV\", \"replace\": \"new value\" }\n" +
                    "Use literal strings only.");
        }

        return InMemory(Mk8ShellVerb.FilePatch, op.Args);
    }

    /// <summary>
    /// Supported hash algorithms for <see cref="Mk8ShellVerb.FileHash"/>.
    /// </summary>
    private static readonly HashSet<string> AllowedHashAlgorithms =
        new(StringComparer.OrdinalIgnoreCase) { "sha256", "sha512", "md5" };

    private static Mk8CompiledCommand CompileFileHash(Mk8ShellOperation op)
    {
        // args[0] = file path, args[1] = algorithm (optional, defaults to sha256).
        if (op.Args.Length < 1 || op.Args.Length > 2)
            throw new Mk8CompileException(Mk8ShellVerb.FileHash,
                $"Expected 1–2 argument(s), got {op.Args.Length}.");

        if (op.Args.Length > 1 && !AllowedHashAlgorithms.Contains(op.Args[1]))
            throw new Mk8CompileException(Mk8ShellVerb.FileHash,
                $"Unsupported hash algorithm '{op.Args[1]}'. " +
                "Supported: sha256, sha512, md5.");

        return InMemory(Mk8ShellVerb.FileHash, op.Args);
    }

    /// <summary>Max depth for DirTree recursive listing.</summary>
    private const int DirTreeMaxDepth = 5;

    private static Mk8CompiledCommand CompileDirTree(Mk8ShellOperation op)
    {
        // args[0] = directory path, args[1] = max depth (optional, defaults to 3).
        if (op.Args.Length < 1 || op.Args.Length > 2)
            throw new Mk8CompileException(Mk8ShellVerb.DirTree,
                $"Expected 1–2 argument(s), got {op.Args.Length}.");

        if (op.Args.Length > 1)
        {
            if (!int.TryParse(op.Args[1], out var depth) || depth < 1)
                throw new Mk8CompileException(Mk8ShellVerb.DirTree,
                    $"Depth must be a positive integer, got '{op.Args[1]}'.");

            if (depth > DirTreeMaxDepth)
                throw new Mk8CompileException(Mk8ShellVerb.DirTree,
                    $"Depth {depth} exceeds maximum of {DirTreeMaxDepth}.");
        }

        return InMemory(Mk8ShellVerb.DirTree, op.Args);
    }

    /// <summary>Max expression length for <see cref="Mk8ShellVerb.MathEval"/>.</summary>
    private const int MathEvalMaxLength = 256;

    /// <summary>
    /// Allowed characters in a MathEval expression. Only digits, decimal
    /// point, arithmetic operators, parentheses, and whitespace. No
    /// letters, no functions, no variables — pure arithmetic.
    /// </summary>
    private static readonly HashSet<char> MathEvalAllowed =
    [
        '0', '1', '2', '3', '4', '5', '6', '7', '8', '9',
        '.', '+', '-', '*', '/', '%', '(', ')', ' ',
    ];

    private static Mk8CompiledCommand CompileMathEval(Mk8ShellOperation op)
    {
        RequireArgs(op, 1, 1);
        var expr = op.Args[0];

        if (expr.Length > MathEvalMaxLength)
            throw new Mk8CompileException(Mk8ShellVerb.MathEval,
                $"Expression length {expr.Length} exceeds maximum of {MathEvalMaxLength}.\n" +
                "MathEval evaluates pure arithmetic — keep expressions concise.");

        if (string.IsNullOrWhiteSpace(expr))
            throw new Mk8CompileException(Mk8ShellVerb.MathEval,
                "Expression cannot be empty.\n" +
                "  ✓ Correct: { \"verb\": \"MathEval\", \"args\": [\"(100 + 50) * 2\"] }\n" +
                "  ✗ Wrong:   { \"verb\": \"MathEval\", \"args\": [\"\"  ] }");

        foreach (var ch in expr)
        {
            if (!MathEvalAllowed.Contains(ch))
                throw new Mk8CompileException(Mk8ShellVerb.MathEval,
                    $"Invalid character '{ch}' in expression.\n" +
                    "MathEval accepts ONLY pure arithmetic: digits, decimal point, " +
                    "+, -, *, /, %, (), and spaces. No variables, no functions, no letters.\n" +
                    "  ✓ Correct: \"(100 + 50) * 2\" or \"3.14 * 10 * 10\"\n" +
                    $"  ✗ Wrong:   \"{expr}\" (contains '{ch}')\n" +
                    "For text manipulation use TextReplace/TextRegex. " +
                    "For string operations use the Text* verb family.");
        }

        return InMemory(Mk8ShellVerb.MathEval, op.Args);
    }

    // ═══════════════════════════════════════════════════════════════
    // Extended text/file/system verb compilation
    // ═══════════════════════════════════════════════════════════════

    private static Mk8CompiledCommand CompileTextHash(Mk8ShellOperation op)
    {
        if (op.Args.Length < 1 || op.Args.Length > 2)
            throw new Mk8CompileException(Mk8ShellVerb.TextHash,
                $"Expected 1–2 argument(s), got {op.Args.Length}.");

        if (op.Args.Length > 1 && !AllowedHashAlgorithms.Contains(op.Args[1]))
            throw new Mk8CompileException(Mk8ShellVerb.TextHash,
                $"Unsupported hash algorithm '{op.Args[1]}'. " +
                "Supported: sha256, sha512, md5.");

        return InMemory(Mk8ShellVerb.TextHash, op.Args);
    }

    /// <summary>Max repeat count for <see cref="Mk8ShellVerb.TextRepeat"/>.</summary>
    private const int TextRepeatMaxCount = 256;

    private static Mk8CompiledCommand CompileTextRepeat(Mk8ShellOperation op)
    {
        RequireArgs(op, 2, 2);

        if (!int.TryParse(op.Args[1], out var count) || count < 0)
            throw new Mk8CompileException(Mk8ShellVerb.TextRepeat,
                $"Count must be a non-negative integer, got '{op.Args[1]}'.");

        if (count > TextRepeatMaxCount)
            throw new Mk8CompileException(Mk8ShellVerb.TextRepeat,
                $"Count {count} exceeds maximum of {TextRepeatMaxCount}.");

        return InMemory(Mk8ShellVerb.TextRepeat, op.Args);
    }

    private static Mk8CompiledCommand CompileTextPad(Mk8ShellOperation op)
    {
        // args: [input, totalWidth, padChar?]
        if (op.Args.Length < 2 || op.Args.Length > 3)
            throw new Mk8CompileException(op.Verb,
                $"Expected 2–3 argument(s), got {op.Args.Length}.");

        if (!int.TryParse(op.Args[1], out var width) || width < 0 || width > 10000)
            throw new Mk8CompileException(op.Verb,
                $"Total width must be 0–10000, got '{op.Args[1]}'.");

        if (op.Args.Length > 2)
        {
            var padChar = op.Args[2];
            if (padChar.Length != 1 || char.IsControl(padChar[0]))
                throw new Mk8CompileException(op.Verb,
                    "Pad character must be a single printable character.");
        }

        return InMemory(op.Verb, op.Args);
    }

    /// <summary>Max lines for FileHead/FileTail.</summary>
    private const int FileHeadTailMaxLines = 1000;

    private static Mk8CompiledCommand CompileFileHeadTail(Mk8ShellOperation op)
    {
        if (op.Args.Length < 1 || op.Args.Length > 2)
            throw new Mk8CompileException(op.Verb,
                $"Expected 1–2 argument(s), got {op.Args.Length}.");

        if (op.Args.Length > 1)
        {
            if (!int.TryParse(op.Args[1], out var lines) || lines < 1)
                throw new Mk8CompileException(op.Verb,
                    $"Line count must be a positive integer, got '{op.Args[1]}'.");

            if (lines > FileHeadTailMaxLines)
                throw new Mk8CompileException(op.Verb,
                    $"Line count {lines} exceeds maximum of {FileHeadTailMaxLines}.");
        }

        return InMemory(op.Verb, op.Args);
    }

    /// <summary>
    /// Allowed characters in a <see cref="Mk8ShellVerb.SysDateFormat"/>
    /// format string. Only date/time specifiers, separators, and spaces.
    /// </summary>
    private static readonly HashSet<char> DateFormatAllowed =
    [
        'y', 'M', 'd', 'H', 'h', 'm', 's', 'f', 'F', 'z', 'Z', 'K',
        't', 'T', 'g', ':', '-', '/', '.', ' ', '\'', 'o', 'O', 'r', 'R',
        'u', 'U', 'D',
    ];

    private static Mk8CompiledCommand CompileSysDateFormat(Mk8ShellOperation op)
    {
        if (op.Args.Length > 1)
            throw new Mk8CompileException(Mk8ShellVerb.SysDateFormat,
                $"Expected 0–1 argument(s), got {op.Args.Length}.");

        if (op.Args.Length == 1)
        {
            var fmt = op.Args[0];
            if (fmt.Length > 32)
                throw new Mk8CompileException(Mk8ShellVerb.SysDateFormat,
                    $"Format string length {fmt.Length} exceeds maximum of 32.");

            foreach (var ch in fmt)
            {
                if (!DateFormatAllowed.Contains(ch) && !char.IsDigit(ch))
                    throw new Mk8CompileException(Mk8ShellVerb.SysDateFormat,
                        $"Invalid character '{ch}' in date format string.");
            }
        }

        return InMemory(Mk8ShellVerb.SysDateFormat, op.Args);
    }

    private static Mk8CompiledCommand CompileOpenUrl(Mk8ShellOperation op)
    {
        RequireArgs(op, 1, 1);
        // Reuse existing SSRF validation — same rules as HttpGet.
        Mk8UrlSanitizer.Validate(op.Args[0]);
        return InMemory(Mk8ShellVerb.OpenUrl, op.Args);
    }

    /// <summary>Max ping count.</summary>
    private const int NetPingMaxCount = 10;

    private static Mk8CompiledCommand CompileNetPing(Mk8ShellOperation op)
    {
        // args[0] = hostname, args[1] = count (optional, default 1).
        if (op.Args.Length < 1 || op.Args.Length > 2)
            throw new Mk8CompileException(Mk8ShellVerb.NetPing,
                $"Expected 1–2 argument(s), got {op.Args.Length}.");

        Mk8UrlSanitizer.ValidateHostname(op.Args[0]);

        if (op.Args.Length > 1)
        {
            if (!int.TryParse(op.Args[1], out var count) || count < 1 || count > NetPingMaxCount)
                throw new Mk8CompileException(Mk8ShellVerb.NetPing,
                    $"Count must be 1–{NetPingMaxCount}. Got '{op.Args[1]}'.");
        }

        return InMemory(Mk8ShellVerb.NetPing, op.Args);
    }

    private static Mk8CompiledCommand CompileNetDns(Mk8ShellOperation op)
    {
        RequireArgs(op, 1, 1);
        Mk8UrlSanitizer.ValidateHostname(op.Args[0]);
        return InMemory(Mk8ShellVerb.NetDns, op.Args);
    }

    private static Mk8CompiledCommand CompileNetTlsCert(Mk8ShellOperation op)
    {
        if (op.Args.Length < 1 || op.Args.Length > 2)
            throw new Mk8CompileException(Mk8ShellVerb.NetTlsCert,
                $"Expected 1–2 argument(s), got {op.Args.Length}.");

        Mk8UrlSanitizer.ValidateHostname(op.Args[0]);

        if (op.Args.Length > 1)
        {
            if (!int.TryParse(op.Args[1], out var port) || port < 1 || port > 65535)
                throw new Mk8CompileException(Mk8ShellVerb.NetTlsCert,
                    $"Port must be 1–65535, got '{op.Args[1]}'.");
        }

        return InMemory(Mk8ShellVerb.NetTlsCert, op.Args);
    }

    private static Mk8CompiledCommand CompileNetHttpStatus(Mk8ShellOperation op)
    {
        RequireArgs(op, 1, 1);
        Mk8UrlSanitizer.Validate(op.Args[0]);
        return InMemory(Mk8ShellVerb.NetHttpStatus, op.Args);
    }

    /// <summary>Max depth for <see cref="Mk8ShellVerb.FileGlob"/>.</summary>
    private const int FileGlobMaxDepth = 10;
    /// <summary>Max results for <see cref="Mk8ShellVerb.FileGlob"/>.</summary>
    private const int FileGlobMaxResults = 1000;

    private static Mk8CompiledCommand CompileFileGlob(Mk8ShellOperation op)
    {
        if (op.Args.Length < 2 || op.Args.Length > 3)
            throw new Mk8CompileException(Mk8ShellVerb.FileGlob,
                $"Expected 2–3 argument(s), got {op.Args.Length}.");

        if (op.Args.Length > 2)
        {
            if (!int.TryParse(op.Args[2], out var depth) || depth < 1)
                throw new Mk8CompileException(Mk8ShellVerb.FileGlob,
                    $"Depth must be a positive integer, got '{op.Args[2]}'.");

            if (depth > FileGlobMaxDepth)
                throw new Mk8CompileException(Mk8ShellVerb.FileGlob,
                    $"Depth {depth} exceeds maximum of {FileGlobMaxDepth}.");
        }

        return InMemory(Mk8ShellVerb.FileGlob, op.Args);
    }

    /// <summary>Allowed archive extensions for <see cref="Mk8ShellVerb.ArchiveExtract"/>.</summary>
    private static readonly HashSet<string> AllowedArchiveExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".zip", ".gz", ".tgz" };

    private static Mk8CompiledCommand CompileArchiveExtract(Mk8ShellOperation op)
    {
        // args[0] = archive path (read), args[1] = output directory (write).
        RequireArgs(op, 2, 2);

        var ext = Path.GetExtension(op.Args[0]);
        // Allow .tar.gz via double extension check.
        if (op.Args[0].EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            ext = ".tar.gz";

        if (!AllowedArchiveExtensions.Contains(ext) && ext != ".tar.gz")
            throw new Mk8CompileException(Mk8ShellVerb.ArchiveExtract,
                $"Unsupported archive extension '{ext}'. " +
                "Supported: .zip, .tar.gz, .tgz.");

        return InMemory(Mk8ShellVerb.ArchiveExtract, op.Args);
    }

    private static string[] ValidateArchiveExtractPaths(string[] args, string sandboxRoot)
    {
        if (args.Length > 0)
            args[0] = Mk8PathSanitizer.Resolve(args[0], sandboxRoot);
        if (args.Length > 1)
            args[1] = Mk8PathSanitizer.Resolve(args[1], sandboxRoot);
        return args;
    }

    // ═══════════════════════════════════════════════════════════════
    // Pre-compilation validation for templates and patches
    // ═══════════════════════════════════════════════════════════════

    private static void ValidateTemplatesAndPatches(
        IReadOnlyList<Mk8ShellOperation> operations)
    {
        foreach (var op in operations)
        {
            switch (op.Verb)
            {
                case Mk8ShellVerb.FileTemplate when op.Template is null:
                    throw new Mk8CompileException(Mk8ShellVerb.FileTemplate,
                        "FileTemplate verb requires a 'template' definition.");

                case Mk8ShellVerb.FilePatch when op.Patches is null or { Count: 0 }:
                    throw new Mk8CompileException(Mk8ShellVerb.FilePatch,
                        "FilePatch verb requires at least one patch entry.");
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private static Mk8CompiledCommand InMemory(Mk8ShellVerb verb, string[] args) =>
        new(Mk8CommandKind.InMemory, $"__mk8_inmemory_{verb}", args);

    private static string[] RequireArgs(Mk8ShellOperation op, int min, int max)
    {
        if (op.Args.Length < min || op.Args.Length > max)
            throw new Mk8CompileException(op.Verb,
                $"Expected {min}–{max} argument(s) for '{op.Verb}', got {op.Args.Length}.\n" +
                $"  ✗ You provided {op.Args.Length} arg(s): [{string.Join(", ", op.Args.Select(a => $"\"{a}\""))}]\n" +
                $"Run {{ \"verb\": \"Mk8Skills\", \"args\": [] }} to see the full verb reference " +
                $"with argument counts and types for every verb.");
        return op.Args;
    }

    // ═══════════════════════════════════════════════════════════════
    // JSON construction/mutation validation
    // ═══════════════════════════════════════════════════════════════

    private static Mk8CompiledCommand CompileJsonFromPairs(Mk8ShellOperation op)
    {
        if (op.Args.Length < 2 || op.Args.Length > 128 || op.Args.Length % 2 != 0)
            throw new Mk8CompileException(Mk8ShellVerb.JsonFromPairs,
                $"Expected 2–128 arguments in key-value pairs (even count), got {op.Args.Length}.");
        return InMemory(Mk8ShellVerb.JsonFromPairs, op.Args);
    }

    private static Mk8CompiledCommand CompileJsonArrayFrom(Mk8ShellOperation op)
    {
        if (op.Args.Length < 1 || op.Args.Length > 64)
            throw new Mk8CompileException(Mk8ShellVerb.JsonArrayFrom,
                $"Expected 1–64 argument(s), got {op.Args.Length}.");
        return InMemory(Mk8ShellVerb.JsonArrayFrom, op.Args);
    }

    // ═══════════════════════════════════════════════════════════════
    // File comparison validation
    // ═══════════════════════════════════════════════════════════════

    private static Mk8CompiledCommand CompileFileChecksum(Mk8ShellOperation op)
    {
        if (op.Args.Length < 2 || op.Args.Length > 3)
            throw new Mk8CompileException(Mk8ShellVerb.FileChecksum,
                $"Expected 2–3 argument(s), got {op.Args.Length}.");

        if (op.Args.Length > 2 && !AllowedHashAlgorithms.Contains(op.Args[2]))
            throw new Mk8CompileException(Mk8ShellVerb.FileChecksum,
                $"Unsupported hash algorithm '{op.Args[2]}'. Supported: sha256, sha512, md5.");

        return InMemory(Mk8ShellVerb.FileChecksum, op.Args);
    }

    // ═══════════════════════════════════════════════════════════════
    // Value generation validation
    // ═══════════════════════════════════════════════════════════════

    private static Mk8CompiledCommand CompileRandomInt(Mk8ShellOperation op)
    {
        RequireArgs(op, 2, 2);

        if (!int.TryParse(op.Args[0], out var min))
            throw new Mk8CompileException(Mk8ShellVerb.RandomInt,
                $"Min must be an integer, got '{op.Args[0]}'.");

        if (!int.TryParse(op.Args[1], out var max))
            throw new Mk8CompileException(Mk8ShellVerb.RandomInt,
                $"Max must be an integer, got '{op.Args[1]}'.");

        if (min < 0 || max > 1_000_000 || min > max)
            throw new Mk8CompileException(Mk8ShellVerb.RandomInt,
                $"Range must be 0–1000000 with min ≤ max. Got [{min}, {max}].");

        return InMemory(Mk8ShellVerb.RandomInt, op.Args);
    }

    // ═══════════════════════════════════════════════════════════════
    // Time arithmetic validation
    // ═══════════════════════════════════════════════════════════════

    private static Mk8CompiledCommand CompileTimeFormat(Mk8ShellOperation op)
    {
        if (op.Args.Length < 1 || op.Args.Length > 2)
            throw new Mk8CompileException(Mk8ShellVerb.TimeFormat,
                $"Expected 1–2 argument(s), got {op.Args.Length}.");

        if (!long.TryParse(op.Args[0], out _))
            throw new Mk8CompileException(Mk8ShellVerb.TimeFormat,
                $"First argument must be a Unix timestamp (integer), got '{op.Args[0]}'.");

        if (op.Args.Length > 1)
        {
            var fmt = op.Args[1];
            if (fmt.Length > 32)
                throw new Mk8CompileException(Mk8ShellVerb.TimeFormat,
                    $"Format string length {fmt.Length} exceeds maximum of 32.");

            foreach (var ch in fmt)
            {
                if (!DateFormatAllowed.Contains(ch) && !char.IsDigit(ch))
                    throw new Mk8CompileException(Mk8ShellVerb.TimeFormat,
                        $"Invalid character '{ch}' in date format string.");
            }
        }

        return InMemory(Mk8ShellVerb.TimeFormat, op.Args);
    }

    // ═══════════════════════════════════════════════════════════════
    // Sleep validation
    // ═══════════════════════════════════════════════════════════════

    private static Mk8CompiledCommand CompileSleep(Mk8ShellOperation op)
    {
        RequireArgs(op, 1, 1);

        if (!double.TryParse(op.Args[0], out var seconds))
            throw new Mk8CompileException(Mk8ShellVerb.Sleep,
                $"Seconds must be a number, got '{op.Args[0]}'.");

        if (seconds < 0.1 || seconds > 30)
            throw new Mk8CompileException(Mk8ShellVerb.Sleep,
                $"Sleep duration must be 0.1–30 seconds, got {seconds}.");

        return InMemory(Mk8ShellVerb.Sleep, op.Args);
    }

    // ═══════════════════════════════════════════════════════════════
    // Sysadmin verb validation
    // ═══════════════════════════════════════════════════════════════

    private static Mk8CompiledCommand CompileNetTcpConnect(Mk8ShellOperation op)
    {
        if (op.Args.Length < 2 || op.Args.Length > 3)
            throw new Mk8CompileException(Mk8ShellVerb.NetTcpConnect,
                $"Expected 2–3 argument(s), got {op.Args.Length}.");

        Mk8UrlSanitizer.ValidateHostname(op.Args[0]);

        if (!int.TryParse(op.Args[1], out var port) || port < 1 || port > 65535)
            throw new Mk8CompileException(Mk8ShellVerb.NetTcpConnect,
                $"Port must be 1–65535, got '{op.Args[1]}'.");

        if (op.Args.Length > 2)
        {
            if (!int.TryParse(op.Args[2], out var timeout) || timeout < 1 || timeout > 30)
                throw new Mk8CompileException(Mk8ShellVerb.NetTcpConnect,
                    $"Timeout must be 1–30 seconds, got '{op.Args[2]}'.");
        }

        return InMemory(Mk8ShellVerb.NetTcpConnect, op.Args);
    }

    private static Mk8CompiledCommand CompileHttpLatency(Mk8ShellOperation op)
    {
        if (op.Args.Length < 1 || op.Args.Length > 2)
            throw new Mk8CompileException(Mk8ShellVerb.HttpLatency,
                $"Expected 1–2 argument(s), got {op.Args.Length}.");

        Mk8UrlSanitizer.Validate(op.Args[0]);

        if (op.Args.Length > 1)
        {
            if (!int.TryParse(op.Args[1], out var count) || count < 1 || count > 10)
                throw new Mk8CompileException(Mk8ShellVerb.HttpLatency,
                    $"Count must be 1–10, got '{op.Args[1]}'.");
        }

        return InMemory(Mk8ShellVerb.HttpLatency, op.Args);
    }

    private static Mk8CompiledCommand CompileFileNewerThan(Mk8ShellOperation op)
    {
        RequireArgs(op, 2, 2);

        if (!long.TryParse(op.Args[1], out var seconds) || seconds < 0)
            throw new Mk8CompileException(Mk8ShellVerb.FileNewerThan,
                $"Seconds must be a non-negative integer, got '{op.Args[1]}'.");

        return InMemory(Mk8ShellVerb.FileNewerThan, op.Args);
    }

    private static Mk8CompiledCommand CompileTextColumn(Mk8ShellOperation op)
    {
        if (op.Args.Length < 2 || op.Args.Length > 3)
            throw new Mk8CompileException(Mk8ShellVerb.TextColumn,
                $"Expected 2–3 argument(s), got {op.Args.Length}.");

        if (!int.TryParse(op.Args[1], out var index) || index < 0 || index > 100)
            throw new Mk8CompileException(Mk8ShellVerb.TextColumn,
                $"Column index must be 0–100, got '{op.Args[1]}'.");

        return InMemory(Mk8ShellVerb.TextColumn, op.Args);
    }

    private static Mk8CompiledCommand CompileDirHash(Mk8ShellOperation op)
    {
        if (op.Args.Length < 1 || op.Args.Length > 3)
            throw new Mk8CompileException(Mk8ShellVerb.DirHash,
                $"Expected 1–3 argument(s), got {op.Args.Length}.");

        if (op.Args.Length > 1 && !AllowedHashAlgorithms.Contains(op.Args[1]))
            throw new Mk8CompileException(Mk8ShellVerb.DirHash,
                $"Unsupported hash algorithm '{op.Args[1]}'. Supported: sha256, sha512, md5.");

        return InMemory(Mk8ShellVerb.DirHash, op.Args);
    }

    // ═══════════════════════════════════════════════════════════════
    // System log viewing validation
    // ═══════════════════════════════════════════════════════════════

    private static readonly HashSet<string> AllowedLogSources = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows
        "application", "system", "security",
        // Linux
        "syslog", "auth", "kern", "daemon", "messages",
    };

    private static Mk8CompiledCommand CompileSysLogRead(Mk8ShellOperation op)
    {
        if (op.Args.Length < 1 || op.Args.Length > 3)
            throw new Mk8CompileException(Mk8ShellVerb.SysLogRead,
                $"Expected 1–3 argument(s), got {op.Args.Length}.");

        if (!AllowedLogSources.Contains(op.Args[0]))
            throw new Mk8CompileException(Mk8ShellVerb.SysLogRead,
                $"Unknown log source '{op.Args[0]}'. " +
                $"Allowed: {string.Join(", ", AllowedLogSources)}.");

        if (op.Args.Length > 1)
        {
            if (!int.TryParse(op.Args[1], out var lines) || lines < 1 || lines > 500)
                throw new Mk8CompileException(Mk8ShellVerb.SysLogRead,
                    $"Line count must be 1–500, got '{op.Args[1]}'.");
        }

        return InMemory(Mk8ShellVerb.SysLogRead, op.Args);
    }

    // ═══════════════════════════════════════════════════════════════
    // mk8.shell introspection — resolved at compile time
    // ═══════════════════════════════════════════════════════════════

    private Mk8CompiledCommand CompileMk8Blacklist(Mk8ShellOperation op)
    {
        RequireArgs(op, 0, 0);
        var patterns = _whitelist.GigaBlacklist.EffectivePatterns;
        var output = string.Join('\n', patterns);
        return InMemory(Mk8ShellVerb.Mk8Blacklist, [output]);
    }

    private Mk8CompiledCommand CompileMk8Vocab(Mk8ShellOperation op)
    {
        var args = RequireArgs(op, 1, 1);
        var listName = args[0];
        var words = _whitelist.GetWordList(listName);
        if (words.Count == 0)
        {
            var available = string.Join(", ", _whitelist.GetWordListNames());
            return InMemory(Mk8ShellVerb.Mk8Vocab,
                [$"Word list '{listName}' not found. Available: {available}"]);
        }
        var output = string.Join('\n', words.Order());
        return InMemory(Mk8ShellVerb.Mk8Vocab, [output]);
    }

    private Mk8CompiledCommand CompileMk8VocabList(Mk8ShellOperation op)
    {
        RequireArgs(op, 0, 0);
        var names = _whitelist.GetWordListNames();
        var sb = new System.Text.StringBuilder();
        foreach (var name in names)
        {
            var count = _whitelist.GetWordList(name).Count;
            sb.AppendLine($"{name} ({count} words)");
        }
        return InMemory(Mk8ShellVerb.Mk8VocabList, [sb.ToString().TrimEnd()]);
    }

    private Mk8CompiledCommand CompileMk8FreeText(Mk8ShellOperation op)
    {
        var args = RequireArgs(op, 0, 1);
        var config = _whitelist.FreeTextConfig;

        if (args.Length == 0)
        {
            // Global config summary
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Global Enabled: {config.Enabled}");
            sb.AppendLine($"Global MaxLength: {config.MaxLength}");
            sb.AppendLine($"Per-verb overrides: {config.PerVerb.Count}");
            foreach (var (key, policy) in config.PerVerb.OrderBy(kv => kv.Key))
                sb.AppendLine($"  {key}: Enabled={policy.Enabled}, MaxLength={policy.MaxLength}");
            return InMemory(Mk8ShellVerb.Mk8FreeText, [sb.ToString().TrimEnd()]);
        }

        // Per-command query
        var commandKey = args[0];
        var enabled = config.Enabled;
        var maxLen = config.MaxLength;

        if (config.PerVerb.TryGetValue(commandKey, out var verbPolicy))
        {
            enabled = verbPolicy.Enabled;
            if (verbPolicy.MaxLength > 0)
                maxLen = verbPolicy.MaxLength;
        }

        var result = $"Command: {commandKey}\n" +
                     $"FreeText Enabled: {enabled}\n" +
                     $"MaxLength: {maxLen}";
        return InMemory(Mk8ShellVerb.Mk8FreeText, [result]);
    }

    private Mk8CompiledCommand CompileMk8Env(Mk8ShellOperation op)
    {
        RequireArgs(op, 0, 0);

        // Use the workspace variables (merged global + sandbox).
        var vars = _workspace?.Variables ?? new Dictionary<string, string>();
        var sb = new System.Text.StringBuilder();

        // Secret-like patterns to redact
        string[] secretMarkers = ["KEY", "SECRET", "TOKEN", "PASSWORD",
            "PASSWD", "CREDENTIAL", "CONN", "PRIVATE", "ENCRYPT",
            "JWT", "BEARER", "CERTIFICATE", "APIKEY", "API_KEY"];

        foreach (var (key, value) in vars.OrderBy(kv => kv.Key))
        {
            var redact = secretMarkers.Any(m =>
                key.Contains(m, StringComparison.OrdinalIgnoreCase));
            sb.AppendLine(redact ? $"{key}=[REDACTED]" : $"{key}={value}");
        }

        return InMemory(Mk8ShellVerb.Mk8Env, [sb.ToString().TrimEnd()]);
    }

    private Mk8CompiledCommand CompileMk8Info(Mk8ShellOperation op)
    {
        RequireArgs(op, 0, 0);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SandboxId: {_workspace?.SandboxId ?? "(unknown)"}");
        sb.AppendLine($"WorkspaceRoot: {_workspace?.SandboxRoot ?? "(unknown)"}");
        sb.AppendLine($"WorkingDirectory: {_workspace?.WorkingDirectory ?? "(unknown)"}");
        sb.AppendLine($"User: {_workspace?.RunAsUser ?? Environment.UserName}");
        sb.AppendLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        sb.AppendLine($"Architecture: {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"ProcessArchitecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($".NET: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"ProcessorCount: {Environment.ProcessorCount}");
        sb.AppendLine($"MachineName: {Environment.MachineName}");

        return InMemory(Mk8ShellVerb.Mk8Info, [sb.ToString().TrimEnd()]);
    }

    private Mk8CompiledCommand CompileMk8Templates(Mk8ShellOperation op)
    {
        RequireArgs(op, 0, 0);
        var descriptions = _whitelist.GetTemplateDescriptions();
        var output = string.Join('\n', descriptions);
        return InMemory(Mk8ShellVerb.Mk8Templates, [output]);
    }

    private Mk8CompiledCommand CompileMk8Verbs(Mk8ShellOperation op)
    {
        RequireArgs(op, 0, 0);
        var verbs = Enum.GetNames<Mk8ShellVerb>()
            .Where(n => n != nameof(Mk8ShellVerb.ForEach)
                     && n != nameof(Mk8ShellVerb.If)
                     && n != nameof(Mk8ShellVerb.Include)
                     && n != nameof(Mk8ShellVerb.FileWriteMany)
                     && n != nameof(Mk8ShellVerb.FileCopyMany)
                     && n != nameof(Mk8ShellVerb.FileDeleteMany))
            .Order();
        return InMemory(Mk8ShellVerb.Mk8Verbs, [string.Join('\n', verbs)]);
    }

    private static Mk8CompiledCommand CompileMk8EmbeddedDoc(
        Mk8ShellOperation op, Mk8ShellVerb verb, string resourceName)
    {
        RequireArgs(op, 0, 0);

        var asm = typeof(Mk8ShellCompiler).Assembly;
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null)
            return InMemory(verb,
                [$"(embedded resource '{resourceName}' not found in assembly)"]);

        using var reader = new System.IO.StreamReader(stream);
        var content = reader.ReadToEnd();
        return InMemory(verb, [content]);
    }
}
