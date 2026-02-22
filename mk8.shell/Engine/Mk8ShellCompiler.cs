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
            Mk8ShellVerb.JsonMerge        => InMemory(op.Verb, RequireArgs(op, 2, 2)),

            // ── File inspection (read-only, in-memory) ────────────
            Mk8ShellVerb.FileLineCount => InMemory(op.Verb, RequireArgs(op, 1, 1)),
            Mk8ShellVerb.FileHead      => CompileFileHeadTail(op),
            Mk8ShellVerb.FileTail      => CompileFileHeadTail(op),
            Mk8ShellVerb.FileSearch    => InMemory(op.Verb, RequireArgs(op, 2, 2)),
            Mk8ShellVerb.FileDiff      => InMemory(op.Verb, RequireArgs(op, 2, 2)),
            Mk8ShellVerb.FileGlob      => CompileFileGlob(op),

            // ── Directory inspection ──────────────────────────────
            Mk8ShellVerb.DirFileCount  => InMemory(op.Verb, RequireArgs(op, 1, 2)),

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

            // ── Archive extraction (in-memory, pre-validated) ─────
            Mk8ShellVerb.ArchiveExtract => CompileArchiveExtract(op),

            // ── Batch/Control/Include verbs should never reach the compiler ─
            Mk8ShellVerb.ForEach or Mk8ShellVerb.If
            or Mk8ShellVerb.FileWriteMany or Mk8ShellVerb.FileCopyMany
            or Mk8ShellVerb.FileDeleteMany or Mk8ShellVerb.Include
                => throw new Mk8CompileException(op.Verb,
                    $"'{op.Verb}' must be expanded before compilation. " +
                    "This is an internal error — the expander should have " +
                    "removed all control flow, batch, and include verbs."),

            _ => throw new Mk8CompileException(op.Verb, "Unknown verb.")
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
                $"Expected 1–64 argument(s), got {args.Length}.");

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
                => ValidateReadPaths(args, sandboxRoot),

            // FileSearch: args[0] = path (read), args[1] = search literal (not a path).
            Mk8ShellVerb.FileSearch
                => ValidateReadPaths(args, sandboxRoot),

            // FileDiff: both args are read paths.
            Mk8ShellVerb.FileDiff
                => ValidateDualReadPaths(args, sandboxRoot),

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
                "FileTemplate verb requires a 'template' definition.");

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
                    $"Template value for key '{key}' contains a '$' character. " +
                    "Variable expansion inside template values is not allowed " +
                    "to prevent injection.");
        }

        return InMemory(Mk8ShellVerb.FileTemplate, op.Args);
    }

    private static Mk8CompiledCommand CompileFilePatch(Mk8ShellOperation op)
    {
        // args[0] = target file path (already validated by ValidatePathArgs).
        RequireArgs(op, 1, 1);

        if (op.Patches is null || op.Patches.Count == 0)
            throw new Mk8CompileException(Mk8ShellVerb.FilePatch,
                "FilePatch verb requires at least one patch entry.");

        if (op.Patches.Count > Mk8PatchEntry.MaxPatches)
            throw new Mk8CompileException(Mk8ShellVerb.FilePatch,
                $"FilePatch has {op.Patches.Count} patches, " +
                $"exceeding the limit of {Mk8PatchEntry.MaxPatches}.");

        foreach (var patch in op.Patches)
        {
            if (string.IsNullOrEmpty(patch.Find))
                throw new Mk8CompileException(Mk8ShellVerb.FilePatch,
                    "Patch 'find' value cannot be empty.");

            // No variable expansion inside patch values.
            if (patch.Find.Contains('$') || patch.Replace.Contains('$'))
                throw new Mk8CompileException(Mk8ShellVerb.FilePatch,
                    "Patch find/replace values contain a '$' character. " +
                    "Variable expansion inside patches is not allowed.");
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
                $"Expression length {expr.Length} exceeds maximum of {MathEvalMaxLength}.");

        if (string.IsNullOrWhiteSpace(expr))
            throw new Mk8CompileException(Mk8ShellVerb.MathEval,
                "Expression cannot be empty.");

        foreach (var ch in expr)
        {
            if (!MathEvalAllowed.Contains(ch))
                throw new Mk8CompileException(Mk8ShellVerb.MathEval,
                    $"Invalid character '{ch}' in expression. " +
                    "Only digits, decimal point, +, -, *, /, %, (), and spaces are allowed.");
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
                $"Expected {min}–{max} argument(s), got {op.Args.Length}.");
        return op.Args;
    }
}
