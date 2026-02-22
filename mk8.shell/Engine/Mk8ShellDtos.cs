using Mk8.Shell.Safety;

namespace Mk8.Shell.Engine;

// ── Script model ──────────────────────────────────────────────────

/// <summary>
/// A single typed operation in an mk8.shell script. The <see cref="Verb"/>
/// determines which handler processes the <see cref="Args"/> array.
/// Arguments are never concatenated into a shell string — they stay as
/// a structured array all the way through compilation and execution.
/// </summary>
public sealed record Mk8ShellOperation(
    Mk8ShellVerb Verb,
    string[] Args,
    int? MaxRetries = null,
    /// <summary>
    /// When <see cref="Verb"/> is <see cref="Mk8ShellVerb.ForEach"/>,
    /// this defines the loop body and item list.
    /// </summary>
    Mk8ForEach? ForEach = null,
    /// <summary>
    /// When <see cref="Verb"/> is <see cref="Mk8ShellVerb.If"/>,
    /// this defines the predicate and guarded operation.
    /// </summary>
    Mk8Conditional? If = null,
    /// <summary>
    /// Optional label for this step, used as a jump target by
    /// <see cref="OnFailure"/>. Labels are metadata — resolved and
    /// validated at compile time. Must be unique within a script.
    /// </summary>
    string? Label = null,
    /// <summary>
    /// Forward jump on failure, e.g. <c>"goto:cleanup"</c>. The target
    /// label must exist and must be AFTER this step (no backward jumps).
    /// Validated at compile time to form a DAG — no cycles possible.
    /// </summary>
    string? OnFailure = null,
    /// <summary>
    /// Captures this step's stdout into a named variable accessible
    /// via <c>$NAME</c> in subsequent steps. Same blocking rules as
    /// <c>$PREV</c> apply — captured variables are blocked in ProcRun args
    /// if they originated from a process step.
    /// </summary>
    string? CaptureAs = null,
    /// <summary>
    /// Per-step timeout override.
    /// </summary>
    TimeSpan? StepTimeout = null,
    /// <summary>
    /// When <see cref="Verb"/> is <see cref="Mk8ShellVerb.FileTemplate"/>,
    /// this defines the template source and replacement values.
    /// </summary>
    Mk8FileTemplate? Template = null,
    /// <summary>
    /// When <see cref="Verb"/> is <see cref="Mk8ShellVerb.FilePatch"/>,
    /// this defines the ordered find/replace patches to apply.
    /// </summary>
    IReadOnlyList<Mk8PatchEntry>? Patches = null);

/// <summary>
/// An ordered list of <see cref="Mk8ShellOperation"/>s submitted as
/// the payload of an <c>ExecuteAsSystemUser</c> agent job.
/// </summary>
public sealed record Mk8ShellScript(
    IReadOnlyList<Mk8ShellOperation> Operations,
    Mk8ExecutionOptions? Options = null,
    /// <summary>
    /// Operations that run after failure when <see cref="Mk8ExecutionOptions.FailureMode"/>
    /// is <see cref="Mk8FailureMode.StopAndCleanup"/>. Cleanup steps run with
    /// <c>ContinueOnError</c> semantics (best-effort). They go through the same
    /// compilation pipeline — same path validation, same verb restrictions.
    /// </summary>
    IReadOnlyList<Mk8ShellOperation>? Cleanup = null);

// ── ForEach (compile-time unroll) ─────────────────────────────────

/// <summary>
/// Unrolled at compile time into N concrete operations. The agent
/// provides <see cref="Items"/> and a <see cref="Body"/> template
/// whose args may contain <c>$ITEM</c> (replaced per iteration)
/// and <c>$INDEX</c> (0-based).
/// <para>
/// This is NOT a runtime loop — it cannot observe results between
/// iterations. It's purely syntactic sugar that reduces script size.
/// </para>
/// </summary>
/// <example>
/// <code>
/// {
///   "verb": "ForEach",
///   "forEach": {
///     "items": ["src/a.txt", "src/b.txt", "src/c.txt"],
///     "body": { "verb": "FileCopy", "args": ["$ITEM", "$WORKSPACE/backup/$ITEM"] }
///   }
/// }
/// // Compiles to 3 FileCopy operations.
/// </code>
/// </example>
public sealed record Mk8ForEach(
    /// <summary>Literal string items to iterate over.</summary>
    IReadOnlyList<string> Items,
    /// <summary>
    /// Template operation. <c>$ITEM</c> and <c>$INDEX</c> in
    /// <see cref="Mk8ShellOperation.Args"/> are replaced per iteration.
    /// </summary>
    Mk8ShellOperation Body)
{
    /// <summary>
    /// Hard ceiling on expansion count. Prevents agents from
    /// submitting a 100,000-item list.
    /// </summary>
    public const int MaxExpansion = 256;
}

// ── Conditional (compile-time guard) ──────────────────────────────

/// <summary>
/// Predicate evaluated at compile time or at step execution time.
/// If the predicate is false the <see cref="Then"/> operation is
/// skipped (not compiled / not executed). No else branch.
/// </summary>
public sealed record Mk8Conditional(
    Mk8Predicate Predicate,
    /// <summary>The guarded operation — only runs if predicate is true.</summary>
    Mk8ShellOperation Then);

/// <summary>
/// A simple predicate with a closed set of test types. No arbitrary
/// expressions, no nesting, no boolean operators.
/// </summary>
public sealed record Mk8Predicate(
    Mk8PredicateKind Kind,
    /// <summary>
    /// Arguments interpreted by <see cref="Kind"/>:
    /// <list type="bullet">
    ///   <item><c>FileExists</c> — <c>[path]</c></item>
    ///   <item><c>DirExists</c> — <c>[path]</c></item>
    ///   <item><c>PrevContains</c> — <c>[substring]</c></item>
    ///   <item><c>PrevEmpty</c> — <c>[]</c></item>
    ///   <item><c>EnvEquals</c> — <c>[name, expectedValue]</c></item>
    ///   <item><c>PrevStartsWith</c> — <c>[value]</c></item>
    ///   <item><c>PrevEndsWith</c> — <c>[value]</c></item>
    ///   <item><c>PrevEquals</c> — <c>[value]</c></item>
    ///   <item><c>PrevMatch</c> — <c>[pattern]</c></item>
    ///   <item><c>PrevLineCount</c> — <c>[operator, count]</c></item>
    ///   <item><c>CaptureEmpty</c> — <c>[name]</c></item>
    ///   <item><c>CaptureContains</c> — <c>[name, substring]</c></item>
    /// </list>
    /// </summary>
    string[] Args);

public enum Mk8PredicateKind
{
    FileExists,
    DirExists,
    PrevContains,
    PrevEmpty,
    EnvEquals,

    /// <summary>True if <c>$PREV</c> starts with the given value (case-insensitive).</summary>
    PrevStartsWith,
    /// <summary>True if <c>$PREV</c> ends with the given value (case-insensitive).</summary>
    PrevEndsWith,
    /// <summary>True if <c>$PREV</c> equals the given value exactly (case-insensitive).</summary>
    PrevEquals,
    /// <summary>True if <c>$PREV</c> matches the given regex pattern (2s timeout).</summary>
    PrevMatch,
    /// <summary>
    /// True if <c>$PREV</c> line count satisfies operator + count.
    /// Args: <c>[operator, count]</c> where operator ∈ {eq, gt, lt, gte, lte}.
    /// </summary>
    PrevLineCount,
    /// <summary>True if the named capture variable is empty/whitespace. Args: <c>[name]</c>.</summary>
    CaptureEmpty,
    /// <summary>True if the named capture contains substring. Args: <c>[name, substring]</c>.</summary>
    CaptureContains,
}

// ── Batch file operations ─────────────────────────────────────────

/// <summary>
/// Used as <c>Args</c> payload for <see cref="Mk8ShellVerb.FileWriteMany"/>.
/// Each entry is <c>[path, content]</c> — same as FileWrite but batched.
/// The compiler expands to N in-memory FileWrite commands.
/// </summary>
/// <remarks>
/// Why: agents frequently need to scaffold multiple files. Without
/// batch, they'd submit a 20-step script for 20 files. This keeps
/// the script readable and the expansion safe (same path + extension
/// validation applies per file).
/// </remarks>
public sealed record Mk8BatchFileWrite(
    IReadOnlyList<Mk8FileEntry> Files)
{
    public const int MaxFiles = 64;
}

public sealed record Mk8FileEntry(string Path, string Content);

// ── FileTemplate (structured templating) ──────────────────────

/// <summary>
/// Defines a template source and replacement values for
/// <see cref="Mk8ShellVerb.FileTemplate"/>. The executor reads the
/// source template, replaces <c>{{key}}</c> placeholders with the
/// provided values, and writes the result to the target path.
/// <para>
/// No eval, no expression language — just string replacement with
/// a closed set of keys. Template source must be in sandbox.
/// Replacement values are literal strings only — no variable
/// resolution inside values (prevents <c>{{$PREV}}</c> injection).
/// </para>
/// </summary>
public sealed record Mk8FileTemplate(
    /// <summary>
    /// Path to the template file. Must be in sandbox.
    /// The file should contain <c>{{KEY}}</c> placeholders.
    /// </summary>
    string Source,
    /// <summary>
    /// Key-value pairs. Each <c>{{KEY}}</c> in the template is
    /// replaced with its corresponding value. Keys are
    /// case-sensitive. Values are literal — no variable expansion.
    /// </summary>
    IReadOnlyDictionary<string, string> Values)
{
    /// <summary>Max number of replacement keys.</summary>
    public const int MaxKeys = 64;
}

// ── FilePatch (surgical file edits) ───────────────────────────

/// <summary>
/// A single find/replace patch entry for
/// <see cref="Mk8ShellVerb.FilePatch"/>. Both <c>Find</c> and
/// <c>Replace</c> are literal strings — no regex, no variable
/// expansion inside patch values.
/// </summary>
public sealed record Mk8PatchEntry(
    /// <summary>Exact string to find in the file.</summary>
    string Find,
    /// <summary>Replacement string.</summary>
    string Replace)
{
    /// <summary>Max patches per FilePatch operation.</summary>
    public const int MaxPatches = 32;
}

// ── Named capture rules ───────────────────────────────────────

/// <summary>
/// Constants for step output capture to named variables.
/// </summary>
public static class Mk8CaptureRules
{
    /// <summary>Max number of captured variables per script.</summary>
    public const int MaxCaptures = 16;

    /// <summary>
    /// Built-in variable names that cannot be used as capture names.
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "WORKSPACE", "CWD", "USER", "PREV", "ITEM", "INDEX"
        };
}

// ── Execution options ─────────────────────────────────────────────

public sealed record Mk8ExecutionOptions(
    int MaxRetries = 0,
    TimeSpan RetryDelay = default,
    TimeSpan StepTimeout = default,
    TimeSpan ScriptTimeout = default,
    Mk8FailureMode FailureMode = Mk8FailureMode.StopOnFirstError,
    int MaxOutputBytes = 1_048_576,
    int MaxErrorBytes = 262_144,
    bool PipeStepOutput = false)
{
    public static Mk8ExecutionOptions Default { get; } = new()
    {
        RetryDelay = TimeSpan.FromSeconds(2),
        StepTimeout = TimeSpan.FromSeconds(30),
        ScriptTimeout = TimeSpan.FromMinutes(5),
    };
}

public enum Mk8FailureMode
{
    StopOnFirstError,
    ContinueOnError,
    StopAndCleanup,
}

// ── Workspace context ─────────────────────────────────────────────

/// <summary>
/// Built server-side at job startup. The agent never supplies this.
/// </summary>
public sealed record Mk8WorkspaceContext(
    /// <summary>
    /// The sandbox identifier (e.g. "Banana"). Used for audit logging
    /// and tracing — not for path resolution (use <see cref="SandboxRoot"/>).
    /// </summary>
    string SandboxId,
    string SandboxRoot,
    string WorkingDirectory,
    string RunAsUser,
    IReadOnlyDictionary<string, string> Variables);

// ── Compilation output ────────────────────────────────────────────

/// <summary>
/// Describes how the executor should dispatch a compiled command.
/// mk8.shell never invokes a shell — these categories determine
/// whether the command runs in-memory via .NET APIs or spawns an
/// OS process via <c>ProcessStartInfo</c>.
/// </summary>
public enum Mk8CommandKind
{
    /// <summary>
    /// Executed in-memory via .NET APIs (File.ReadAllTextAsync,
    /// HttpClient, System.Security.Cryptography, etc.).
    /// No external process is spawned. The <c>Executable</c> field
    /// is a marker string like <c>__mk8_inmemory_FileRead</c>.
    /// </summary>
    InMemory,

    /// <summary>
    /// Spawns an OS process via <c>ProcessStartInfo</c> with
    /// <c>UseShellExecute = false</c> and args passed individually
    /// via <c>ArgumentList</c>. The binary must be on the
    /// <see cref="Mk8BinaryAllowlist"/>.
    /// </summary>
    Process,

    // GitProcess was removed.  All git operations now require the
    // dangerous-shell execution path.  A future dedicated project
    // (e.g. "mk8.safegit") may reintroduce a safe git surface.
}

public sealed record Mk8CompiledCommand(
    Mk8CommandKind Kind,
    string Executable,
    string[] Arguments);

public sealed record Mk8CompiledScript(
IReadOnlyList<Mk8CompiledCommand> Commands,
Mk8ExecutionOptions EffectiveOptions,
Mk8WorkspaceContext Workspace,
/// <summary>
/// Compiled cleanup commands. Run with <c>ContinueOnError</c>
/// semantics after failure when <see cref="Mk8ExecutionOptions.FailureMode"/>
/// is <see cref="Mk8FailureMode.StopAndCleanup"/>. Empty if no cleanup defined.
/// </summary>
IReadOnlyList<Mk8CompiledCommand>? CleanupCommands = null,
/// <summary>
/// Label-to-index mapping. Only populated when operations use labels.
/// Used by the executor for forward jumps on failure.
/// </summary>
IReadOnlyDictionary<string, int>? LabelIndex = null);

// ── Execution result ──────────────────────────────────────────────

public sealed record Mk8ShellStepResult(
    int StepIndex,
    Mk8ShellVerb Verb,
    bool Success,
    string? Output,
    string? Error,
    int Attempts,
    TimeSpan Duration);

public sealed record Mk8ShellScriptResult(
    bool AllSucceeded,
    IReadOnlyList<Mk8ShellStepResult> Steps,
    TimeSpan TotalDuration);

// ── Audit log ─────────────────────────────────────────────────

/// <summary>
/// Complete audit trail entry for a single step. Records what the
/// agent requested → what was compiled → what actually ran → what
/// it returned. Enables post-incident analysis and rate limiting.
/// </summary>
public sealed record Mk8ShellAuditEntry(
    /// <summary>Unique identifier for the job that executed this step.</summary>
    string JobId,
    /// <summary>0-based index of this step within the script.</summary>
    int StepIndex,
    /// <summary>Verb as submitted by the agent (pre-compilation).</summary>
    Mk8ShellVerb RequestedVerb,
    /// <summary>Args as submitted by the agent (pre-compilation).</summary>
    string[] RequestedArgs,
    /// <summary>Executable after compilation (post-variable-resolution).</summary>
    string CompiledExecutable,
    /// <summary>Args after compilation (post-variable-resolution, post-sanitization).</summary>
    string[] CompiledArgs,
    /// <summary>Process exit code or 0 for in-memory operations.</summary>
    int ExitCode,
    /// <summary>Captured stdout (truncated to maxOutputBytes).</summary>
    string? Output,
    /// <summary>Captured stderr (truncated to maxErrorBytes).</summary>
    string? Error,
    /// <summary>UTC timestamp when step execution started.</summary>
    DateTimeOffset StartedAt,
    /// <summary>UTC timestamp when step execution completed.</summary>
    DateTimeOffset CompletedAt,
    /// <summary>Number of attempts (1 = no retries).</summary>
    int Attempts,
    /// <summary>Sandbox root at time of execution.</summary>
    string SandboxRoot);

/// <summary>
/// Generates <see cref="Mk8ShellAuditEntry"/> records from a compiled
/// script and its execution results.
/// </summary>
public static class Mk8AuditLog
{
    /// <summary>
    /// Creates audit entries by correlating the original operations,
    /// compiled commands, and execution results.
    /// </summary>
    public static IReadOnlyList<Mk8ShellAuditEntry> CreateEntries(
        string jobId,
        Mk8ShellScript originalScript,
        Mk8CompiledScript compiledScript,
        Mk8ShellScriptResult result)
    {
        ArgumentNullException.ThrowIfNull(originalScript);
        ArgumentNullException.ThrowIfNull(compiledScript);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var entries = new List<Mk8ShellAuditEntry>(result.Steps.Count);

        for (var i = 0; i < result.Steps.Count; i++)
        {
            var step = result.Steps[i];
            var compiled = i < compiledScript.Commands.Count
                ? compiledScript.Commands[i]
                : null;

            entries.Add(new Mk8ShellAuditEntry(
                JobId: jobId,
                StepIndex: step.StepIndex,
                RequestedVerb: step.Verb,
                RequestedArgs: i < originalScript.Operations.Count
                    ? originalScript.Operations[i].Args
                    : [],
                CompiledExecutable: compiled?.Executable ?? "unknown",
                CompiledArgs: compiled?.Arguments ?? [],
                ExitCode: step.Success ? 0 : -1,
                Output: step.Output,
                Error: step.Error,
                StartedAt: DateTimeOffset.UtcNow - step.Duration,
                CompletedAt: DateTimeOffset.UtcNow,
                Attempts: step.Attempts,
                SandboxRoot: compiledScript.Workspace.SandboxRoot));
        }

        return entries;
    }
}
