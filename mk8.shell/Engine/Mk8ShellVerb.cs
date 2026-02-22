namespace Mk8.Shell.Engine;

/// <summary>
/// The closed set of mk8.shell verbs. Every agent command must map to
/// exactly one verb — there is no generic "execute" or "shell" verb.
/// </summary>
public enum Mk8ShellVerb
{
    // ── Filesystem ────────────────────────────────────────────────
    FileRead,
    FileWrite,
    FileAppend,
    FileDelete,
    FileExists,
    FileList,
    FileCopy,
    FileMove,

    /// <summary>
    /// Returns file metadata: size in bytes, created UTC, modified UTC,
    /// and attributes. Read-only, in-memory via <see cref="System.IO.FileInfo"/>.
    /// Path must be in sandbox.
    /// </summary>
    FileInfo,

    // ── Batch filesystem ──────────────────────────────────────────
    FileWriteMany,
    FileCopyMany,
    FileDeleteMany,

    // ── Directory ─────────────────────────────────────────────────
    DirCreate,
    DirDelete,
    DirList,
    DirExists,

    // ── Process (allowlisted binaries only) ───────────────────────
    ProcRun,

    // ── Git ───────────────────────────────────────────────────────
    //
    // REMOVED.  Git verbs (GitStatus, GitAdd, GitCommit, etc.) were
    // originally part of mk8.shell but have been pulled out.  Even
    // with flag validation and UseShellExecute=false, git's own flag
    // interpreter surface area is too large to guarantee that an
    // agent cannot destroy or corrupt a repository in an unexpected
    // way.
    //
    // Safe git functionality will be provided by a dedicated future
    // project (e.g. "mk8.safegit") with a much narrower read-heavy
    // API surface.  Until then, all git operations require
    // DangerousShellType.Git via the dangerous-shell execution path.
    //

    // ── HTTP ──────────────────────────────────────────────────────
    HttpGet,
    HttpPost,
    HttpPut,
    HttpDelete,

    // ── Text / data manipulation ─────────────────────────────────
    TextRegex,
    TextReplace,
    JsonParse,
    JsonQuery,

    // ── Text manipulation — extended (pure string ops, no I/O) ────

    /// <summary>Splits input on delimiter, returns parts separated by newlines.</summary>
    TextSplit,
    /// <summary>Joins args[1..] with delimiter args[0].</summary>
    TextJoin,
    /// <summary>Trims leading/trailing whitespace.</summary>
    TextTrim,
    /// <summary>Returns character count of input.</summary>
    TextLength,
    /// <summary>Returns substring. args: [input, start, length?].</summary>
    TextSubstring,
    /// <summary>Splits input into lines, returns count + lines.</summary>
    TextLines,
    /// <summary>Converts input to uppercase (invariant).</summary>
    TextToUpper,
    /// <summary>Converts input to lowercase (invariant).</summary>
    TextToLower,
    /// <summary>Base64-encodes a UTF-8 string.</summary>
    TextBase64Encode,
    /// <summary>Decodes a Base64 string to UTF-8.</summary>
    TextBase64Decode,
    /// <summary>URL-encodes a string via <c>Uri.EscapeDataString</c>.</summary>
    TextUrlEncode,
    /// <summary>URL-decodes a string via <c>Uri.UnescapeDataString</c>.</summary>
    TextUrlDecode,
    /// <summary>HTML-encodes a string via <c>WebUtility.HtmlEncode</c>.</summary>
    TextHtmlEncode,
    /// <summary>Returns <c>"true"</c>/<c>"false"</c> — literal substring check.</summary>
    TextContains,
    /// <summary>Returns <c>"true"</c>/<c>"false"</c> — literal prefix check.</summary>
    TextStartsWith,
    /// <summary>Returns <c>"true"</c>/<c>"false"</c> — literal suffix check.</summary>
    TextEndsWith,
    /// <summary>Returns <c>"true"</c>/<c>"false"</c> — regex match with 2s timeout.</summary>
    TextMatch,
    /// <summary>Hashes a UTF-8 string (sha256/sha512/md5). Like <see cref="FileHash"/> but for in-memory text.</summary>
    TextHash,
    /// <summary>Sorts lines alphabetically or numerically. Direction: asc (default), desc, numeric.</summary>
    TextSort,
    /// <summary>Removes consecutive duplicate lines (like <c>uniq</c>).</summary>
    TextUniq,
    /// <summary>Counts occurrences of a literal substring, or lines/words/chars if no pattern.</summary>
    TextCount,
    /// <summary>Returns first index of substring (-1 if not found).</summary>
    TextIndexOf,
    /// <summary>Returns last index of substring (-1 if not found).</summary>
    TextLastIndexOf,
    /// <summary>Removes all occurrences of a substring. Sugar for TextReplace(input, old, "").</summary>
    TextRemove,
    /// <summary>Word count via whitespace splitting.</summary>
    TextWordCount,
    /// <summary>Reverses a string.</summary>
    TextReverse,
    /// <summary>Left-pads a string to a total width. Optional pad char (default space, single printable).</summary>
    TextPadLeft,
    /// <summary>Right-pads a string to a total width. Optional pad char (default space, single printable).</summary>
    TextPadRight,
    /// <summary>Repeats a string N times. Count max 256, output capped at maxOutputBytes.</summary>
    TextRepeat,

    // ── JSON — extended ───────────────────────────────────────────

    /// <summary>Shallow-merges two JSON objects. Second wins on key conflict.</summary>
    JsonMerge,
    /// <summary>Returns top-level keys from a JSON object as newline-separated output.</summary>
    JsonKeys,
    /// <summary>Returns element count from a JSON array.</summary>
    JsonCount,
    /// <summary>Returns the root JSON token type (object, array, string, number, boolean, null).</summary>
    JsonType,

    // ── File inspection (read-only, in-memory) ────────────────────

    /// <summary>Returns line count of a file. In-memory via <c>File.ReadLines</c>.</summary>
    FileLineCount,
    /// <summary>Returns first N lines of a file (default 10, max 1000).</summary>
    FileHead,
    /// <summary>Returns last N lines of a file (default 10, max 1000).</summary>
    FileTail,
    /// <summary>Literal substring search in file — returns matching lines with line numbers.</summary>
    FileSearch,
    /// <summary>Line-by-line diff of two files.</summary>
    FileDiff,
    /// <summary>
    /// Recursive file search by glob pattern within sandbox. Read-only,
    /// depth max 10, results max 1000. Safe replacement for blocked <c>find</c>.
    /// </summary>
    FileGlob,

    // ── Directory inspection (read-only) ──────────────────────────

    /// <summary>Returns count of files in a directory, optional glob pattern.</summary>
    DirFileCount,
    /// <summary>Returns <c>"True"</c>/<c>"False"</c> — whether a directory contains any entries.</summary>
    DirEmpty,

    // ── File inspection — type detection (read-only, in-memory) ──

    /// <summary>
    /// Detects file type via magic-byte header matching. Read-only, in-memory.
    /// Returns a MIME type string (e.g. <c>"application/pdf"</c>).
    /// </summary>
    FileMimeType,
    /// <summary>
    /// Detects file encoding via BOM detection + heuristics. Read-only, in-memory.
    /// Returns encoding name (e.g. <c>"utf-8"</c>, <c>"utf-16-le"</c>, <c>"ascii"</c>).
    /// </summary>
    FileEncoding,

    // ── System info — extended (read-only, no args unless noted) ──

    /// <summary>Formatted date. args: [format?] — restricted charset, max 32 chars.</summary>
    SysDateFormat,
    /// <summary>Unix epoch seconds (UTC).</summary>
    SysTimestamp,
    /// <summary>OS description, architecture, .NET runtime version.</summary>
    SysOsInfo,
    /// <summary>Returns <c>Environment.ProcessorCount</c>.</summary>
    SysCpuCount,
    /// <summary>Returns <c>Path.GetTempPath()</c>. Read-only, does not create files.</summary>
    SysTempDir,

    // ── Environment (read-only allowlist) ─────────────────────────
    EnvGet,

    // ── System info (read-only) ──────────────────────────────────
    SysWhoAmI,
    SysPwd,
    SysHostname,
    SysUptime,
    SysDate,

    /// <summary>
    /// Reports disk space for the drive containing the given path
    /// (or sandbox root if omitted). Read-only, in-memory via
    /// <see cref="System.IO.DriveInfo"/>.
    /// </summary>
    SysDiskUsage,

    /// <summary>
    /// Computes the total size in bytes of all files under a directory,
    /// recursively. Read-only, in-memory. Path must be in sandbox.
    /// </summary>
    SysDirSize,

    /// <summary>
    /// Reports process memory usage (working set, GC heap).
    /// Read-only, in-memory via <see cref="System.Diagnostics.Process"/>
    /// and <see cref="System.GC"/>.
    /// </summary>
    SysMemory,

    /// <summary>
    /// Lists running processes (name + PID). Read-only, in-memory via
    /// <see cref="System.Diagnostics.Process.GetProcesses"/>. Output
    /// is truncated to the first 200 processes.
    /// </summary>
    SysProcessList,

    // ── Filesystem — advanced ─────────────────────────────────────

    /// <summary>
    /// Reads a template file, replaces <c>{{key}}</c> placeholders with
    /// provided values, and writes the result. No eval, no expression
    /// language — just literal string replacement with a closed key set.
    /// </summary>
    FileTemplate,

    /// <summary>
    /// Applies ordered find/replace patches to a file. Each patch is a
    /// literal string match — no regex. Reads the file, applies patches
    /// in order, writes the result atomically.
    /// </summary>
    FilePatch,

    /// <summary>
    /// Recursive directory listing up to a bounded depth (max 5).
    /// Read-only, in-memory. No glob — just a bounded tree walk.
    /// </summary>
    DirTree,

    /// <summary>
    /// Computes a cryptographic hash of a file. In-memory via
    /// <c>System.Security.Cryptography</c>. Read-only, no external process.
    /// Supported algorithms: sha256, sha512, md5.
    /// </summary>
    FileHash,

    // ── Clipboard (write-only) ─────────────────────────────────────

    /// <summary>
    /// Sets the OS clipboard text content. In-memory, write-only.
    /// Does NOT read clipboard (prevents exfiltration of user-copied
    /// passwords or secrets). Platform-dependent — may no-op on
    /// headless Linux.
    /// </summary>
    ClipboardSet,

    // ── Math (safe arithmetic) ─────────────────────────────────────

    /// <summary>
    /// Evaluates a basic arithmetic expression: <c>+</c>, <c>-</c>,
    /// <c>*</c>, <c>/</c>, <c>%</c>, <c>()</c>, decimal numbers.
    /// No variables, no functions, no string eval. In-memory via
    /// <see cref="System.Data.DataTable.Compute"/>. Max 256 chars.
    /// </summary>
    MathEval,

    // ── URL validation (in-memory, no browser launch) ──────────────

    /// <summary>
    /// Validates an HTTPS URL and returns it as output. mk8.shell does
    /// NOT open a browser — the caller decides what to do with the
    /// validated URL. Same SSRF validation as HTTP verbs.
    /// </summary>
    OpenUrl,

    // ── Network diagnostics (in-memory, no process) ────────────────

    /// <summary>
    /// Sends ICMP echo requests via <see cref="System.Net.NetworkInformation.Ping"/>.
    /// Host validated against SSRF blocklist. In-memory, no process spawned.
    /// </summary>
    NetPing,

    /// <summary>
    /// DNS lookup via <see cref="System.Net.Dns"/>. Host validated at
    /// compile time, private IPs filtered from output. In-memory.
    /// </summary>
    NetDns,

    /// <summary>
    /// Inspects a remote TLS certificate via <see cref="System.Net.Security.SslStream"/>.
    /// Returns subject, issuer, expiry, SANs, thumbprint. Hostname SSRF-validated.
    /// Port defaults to 443. Read-only — connect, read cert, disconnect.
    /// </summary>
    NetTlsCert,

    /// <summary>
    /// Sends an HTTP HEAD request and returns status code + response headers
    /// (no body downloaded). Lightweight health check. Same SSRF validation
    /// as <see cref="HttpGet"/>.
    /// </summary>
    NetHttpStatus,

    // ── Archive extraction (in-memory, pre-scan validated) ─────────

    /// <summary>
    /// Extracts a .zip or .tar.gz archive into a sandbox directory.
    /// In-memory via <see cref="System.IO.Compression"/>. Pre-scans
    /// all entries for path traversal, symlinks, blocked extensions,
    /// and zip bombs before extracting anything to disk.
    /// </summary>
    ArchiveExtract,

    // ── Script composition (compile-time inlining) ────────────────

    /// <summary>
    /// References an admin-approved script fragment by ID. Resolved at
    /// expand time from a server-side registry — agents cannot define
    /// fragments, only reference them. Expansion depth is counted and
    /// total operation cap still applies.
    /// </summary>
    Include,

    // ── Control flow (limited, compile-time expansion) ────────────
    /// <summary>
    /// Expands a template operation over an item list at compile time.
    /// Not a real "loop" — the compiler unrolls it into N concrete
    /// operations before execution. The agent cannot create unbounded
    /// iteration; <see cref="Mk8ForEach.MaxExpansion"/> caps it.
    /// </summary>
    ForEach,

    /// <summary>
    /// Conditional guard — only compiles the inner operation if a
    /// compile-time predicate is true. No branching at runtime.
    /// </summary>
    If,
}
