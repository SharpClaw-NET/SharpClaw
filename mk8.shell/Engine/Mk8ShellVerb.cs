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

    // ── JSON — construction/mutation ──────────────────────────────

    /// <summary>Builds a JSON object from key-value pairs: [k1, v1, k2, v2, ...]. Max 64 pairs.</summary>
    JsonFromPairs,
    /// <summary>Sets or overwrites a key in a JSON object: [json, key, value].</summary>
    JsonSet,
    /// <summary>Removes a key from a JSON object: [json, key].</summary>
    JsonRemoveKey,
    /// <summary>Gets a value from JSON by index (array) or key (object): [json, indexOrKey].</summary>
    JsonGet,
    /// <summary>Minifies JSON by removing whitespace: [json].</summary>
    JsonCompact,
    /// <summary>Wraps a raw string as a properly-escaped JSON string value: [value].</summary>
    JsonStringify,
    /// <summary>Builds a JSON array from arguments: [item0, item1, ...]. Max 64 items.</summary>
    JsonArrayFrom,

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

    // ── File comparison (read-only) ──────────────────────────────

    /// <summary>Byte-for-byte file comparison. Returns <c>"True"</c>/<c>"False"</c>.</summary>
    FileEqual,
    /// <summary>Computes file hash and compares to expected. Returns <c>"True"</c>/<c>"False"</c>.</summary>
    FileChecksum,

    // ── Path manipulation (pure string ops, no I/O) ──────────────

    /// <summary>Joins path segments platform-aware via <c>Path.Combine</c>. No disk access.</summary>
    PathJoin,
    /// <summary>Returns directory portion via <c>Path.GetDirectoryName</c>.</summary>
    PathDir,
    /// <summary>Returns filename portion via <c>Path.GetFileName</c>.</summary>
    PathFile,
    /// <summary>Returns file extension via <c>Path.GetExtension</c>.</summary>
    PathExt,
    /// <summary>Returns filename without extension via <c>Path.GetFileNameWithoutExtension</c>.</summary>
    PathStem,
    /// <summary>Changes file extension via <c>Path.ChangeExtension</c>.</summary>
    PathChangeExt,

    // ── Identity/value generation ────────────────────────────────

    /// <summary>Generates a new GUID: <c>Guid.NewGuid().ToString()</c>.</summary>
    GuidNew,
    /// <summary>Generates a short 8-char hex GUID: <c>Guid.NewGuid().ToString("N")[..8]</c>.</summary>
    GuidNewShort,
    /// <summary>Generates a random integer in [min, max]. Max range 0–1000000.</summary>
    RandomInt,

    // ── Time arithmetic (pure DateTimeOffset math) ───────────────

    /// <summary>Formats a Unix timestamp as a human-readable string: [unixSeconds, format?].</summary>
    TimeFormat,
    /// <summary>Parses a date string to Unix seconds: [dateString, format?].</summary>
    TimeParse,
    /// <summary>Adds seconds to a Unix timestamp: [unixSeconds, secondsToAdd].</summary>
    TimeAdd,
    /// <summary>Returns absolute difference in seconds between two Unix timestamps.</summary>
    TimeDiff,

    // ── Version comparison ───────────────────────────────────────

    /// <summary>Compares two semver strings. Returns <c>-1</c>, <c>0</c>, or <c>1</c>.</summary>
    VersionCompare,
    /// <summary>Extracts the first semver-like version from a string.</summary>
    VersionParse,

    // ── Encoding/conversion ──────────────────────────────────────

    /// <summary>Encodes a UTF-8 string as a hex string.</summary>
    HexEncode,
    /// <summary>Decodes a hex string to a UTF-8 string.</summary>
    HexDecode,
    /// <summary>Converts between numeric bases 2/8/10/16: [value, fromBase, toBase].</summary>
    BaseConvert,

    // ── Regex capture groups ─────────────────────────────────────

    /// <summary>Returns named/numbered capture groups as JSON. Same 2s timeout as TextRegex.</summary>
    TextRegexGroups,

    // ── Script control/debugging ─────────────────────────────────

    /// <summary>Returns the message as-is. Identity function for debugging/markers.</summary>
    Echo,
    /// <summary>Pauses execution for N seconds. Capped at 30s, min 0.1s.</summary>
    Sleep,
    /// <summary>Fails the step if actual != expected. Optional message: [actual, expected, msg?].</summary>
    Assert,
    /// <summary>Always fails the step with the given message: [message].</summary>
    Fail,

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

    /// <summary>
    /// TCP connect test — checks if a remote port is open. Returns
    /// <c>"Open (Xms)"</c> or <c>"Closed (Xms)"</c>. Same SSRF
    /// hostname validation as <see cref="NetPing"/>. In-memory via
    /// <see cref="System.Net.Sockets.TcpClient"/>. No data sent.
    /// </summary>
    NetTcpConnect,

    /// <summary>
    /// Measures HTTP round-trip latency via timed HEAD requests.
    /// Returns min/avg/max ms. Same SSRF validation as <see cref="HttpGet"/>.
    /// </summary>
    HttpLatency,

    // ── Sysadmin — file age/staleness ──────────────────────────────

    /// <summary>
    /// Returns seconds since last modification of a file. Read-only,
    /// in-memory via <see cref="System.IO.FileInfo.LastWriteTimeUtc"/>.
    /// </summary>
    FileAge,

    /// <summary>
    /// Returns <c>"True"</c>/<c>"False"</c> — whether a file was modified
    /// within the last N seconds. Convenience for staleness checks.
    /// </summary>
    FileNewerThan,

    // ── Sysadmin — process search ──────────────────────────────────

    /// <summary>
    /// Finds processes by name substring. Returns matching PID + name + working set.
    /// Max 50 results. Read-only via <see cref="System.Diagnostics.Process.GetProcesses"/>.
    /// </summary>
    ProcessFind,

    // ── Sysadmin — system discovery ────────────────────────────────

    /// <summary>
    /// Lists all drives/mount points with name, type, total/free space, and format.
    /// Read-only via <see cref="System.IO.DriveInfo.GetDrives"/>.
    /// </summary>
    SysDriveList,

    /// <summary>
    /// Lists network interfaces with name, status, type, and IP addresses.
    /// Loopback filtered. Read-only via <see cref="System.Net.NetworkInformation.NetworkInterface"/>.
    /// </summary>
    SysNetInfo,

    /// <summary>
    /// Lists all allowed environment variable names with their current values.
    /// Read-only, returns only variables from the <see cref="Mk8EnvAllowlist"/>.
    /// </summary>
    EnvList,

    // ── Sysadmin — regex file search ───────────────────────────────

    /// <summary>
    /// Regex search in file — returns matching lines with line numbers.
    /// Same 2-second regex timeout as <see cref="TextRegex"/>. Max 500 matches.
    /// </summary>
    FileSearchRegex,

    // ── Sysadmin — tabular text ────────────────────────────────────

    /// <summary>
    /// Extracts a column (0-based) from each line of delimited text.
    /// Default delimiter is whitespace. Like <c>awk '{print $N}'</c>.
    /// </summary>
    TextColumn,

    /// <summary>
    /// Aligns columns of delimited text for display. Pads each column
    /// to its maximum width. Default delimiter is tab.
    /// </summary>
    TextTable,

    // ── Sysadmin — directory comparison ────────────────────────────

    /// <summary>
    /// Compares two directory trees by filename (not content). Returns
    /// only-in-first, only-in-second, and common files. Read-only.
    /// </summary>
    DirCompare,

    /// <summary>
    /// Hashes all files in a directory, returns a manifest in <c>sha256sum</c>
    /// format (<c>hash  relativePath</c>). Max 500 files. Read-only.
    /// </summary>
    DirHash,

    // ── Sysadmin — human-readable formatting ───────────────────────

    /// <summary>
    /// Formats a byte count as human-readable: <c>1048576</c> → <c>"1.00 MB"</c>.
    /// Pure arithmetic, no I/O.
    /// </summary>
    FormatBytes,

    /// <summary>
    /// Formats seconds as human-readable duration: <c>3661</c> → <c>"1h 1m 1s"</c>.
    /// Pure arithmetic, no I/O.
    /// </summary>
    FormatDuration,

    // ── Sysadmin — system log viewing (read-only, redacted) ────────

    /// <summary>
    /// Reads system/application logs with secret redaction. On Windows,
    /// reads from <see cref="System.Diagnostics.EventLog"/>. On Linux,
    /// reads from <c>/var/log/</c> files. All output is scrubbed for
    /// secret-like patterns before returning.
    /// </summary>
    SysLogRead,

    /// <summary>
    /// Lists available log sources on the current OS. Read-only.
    /// </summary>
    SysLogSources,

    // ── Sysadmin — service status (read-only) ──────────────────────

    /// <summary>
    /// Lists OS services with status. On Windows, via
    /// <see cref="System.ServiceProcess.ServiceController"/>. On Linux,
    /// parses <c>/etc/init.d/</c> or systemd unit files. Read-only.
    /// </summary>
    SysServiceList,

    /// <summary>
    /// Returns detailed status of a specific OS service by name. Read-only.
    /// </summary>
    SysServiceStatus,

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

    // ── mk8.shell introspection (read-only, resolved at compile time) ──

    /// <summary>
    /// Returns all effective gigablacklist patterns (compile-time +
    /// env-sourced) as a newline-separated list. Read-only, no args.
    /// </summary>
    Mk8Blacklist,

    /// <summary>
    /// Returns the words in a named vocabulary/word list as a
    /// newline-separated sorted list. Args: <c>[listName]</c>.
    /// </summary>
    Mk8Vocab,

    /// <summary>
    /// Returns all vocabulary/word list names as a newline-separated
    /// sorted list. Read-only, no args.
    /// </summary>
    Mk8VocabList,

    /// <summary>
    /// Returns FreeText status for a command: whether it's enabled,
    /// the max length, and the fallback word list. Args: <c>[commandKey]</c>
    /// (e.g., <c>"git commit"</c>, <c>"git tag create"</c>,
    /// <c>"dotnet ef migrations add"</c>). No args returns the global
    /// FreeText config.
    /// </summary>
    Mk8FreeText,

    /// <summary>
    /// Returns all merged environment variables (global base.env +
    /// sandbox signed env) as <c>KEY=VALUE</c> lines. Values containing
    /// blocked env patterns (<c>KEY=</c>, <c>SECRET=</c>, etc.) are
    /// redacted. Read-only, no args.
    /// </summary>
    Mk8Env,

    /// <summary>
    /// Returns mk8.shell runtime information: sandbox ID, workspace
    /// root, OS, .NET runtime, architecture, processor count, and
    /// the current user. Read-only, no args.
    /// </summary>
    Mk8Info,

    /// <summary>
    /// Returns all registered ProcRun command template descriptions
    /// as a newline-separated list. Read-only, no args.
    /// </summary>
    Mk8Templates,

    /// <summary>
    /// Returns all available <see cref="Mk8ShellVerb"/> names as a
    /// newline-separated sorted list. Read-only, no args.
    /// </summary>
    Mk8Verbs,

    /// <summary>
    /// Returns the mk8.shell skill reference (agent-facing quick
    /// reference with verb tables, slot types, examples, and constraints).
    /// Read-only, no args. Loaded from embedded resource at compile time.
    /// </summary>
    Mk8Skills,

    /// <summary>
    /// Returns the full mk8.shell documentation (detailed spec with
    /// security model, design rationale, and all verb reference tables).
    /// Read-only, no args. Loaded from embedded resource at compile time.
    /// </summary>
    Mk8Docs,
}
