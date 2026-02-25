Execute commands inside a sandboxed mk8.shell environment.

mk8.shell is a restricted command language — no real shell, no eval,
no pipes, no chaining, no shell expansion. Arguments are structured
arrays. Only ProcRun spawns an external process; everything else runs
in-memory via .NET APIs.

Every command executes inside the named sandbox. The server resolves it
to a local directory, verifies its cryptographically signed environment,
compiles the script, and executes it in an isolated task container.

Script format: { "operations": [{ "verb": "...", "args": ["..."] }], "options": {...}, "cleanup": [...] }

Variables (compile-time): $WORKSPACE (sandbox root), $CWD, $USER,
$PREV (previous stdout when pipeStepOutput: true).
Named captures: { "captureAs": "NAME" } — max 16, blocked in ProcRun args.

Verbs:
  Files: FileRead, FileWrite, FileAppend, FileDelete, FileExists,
    FileList, FileCopy, FileMove, FileInfo
  File inspection: FileHash, FileLineCount, FileHead, FileTail,
    FileSearch, FileSearchRegex, FileGlob, FileDiff, FileMimeType,
    FileEncoding, FileEqual, FileChecksum, FileAge, FileNewerThan
  Structured edits: FileTemplate (requires "template" field),
    FilePatch (requires "patches" field — literal find/replace)
  Batch (max 64): FileWriteMany, FileCopyMany, FileDeleteMany
  Directories: DirCreate, DirDelete, DirList, DirExists,
    DirTree, DirFileCount, DirEmpty, DirCompare, DirHash
  Process: ProcRun — strict command-template whitelist only.
    Unsafe binaries always blocked. Protected git branches BANNED.
    Git: init, clone, push, pull, merge, commit, checkout, switch,
    add, stash, status, log, diff, branch, remote, ls-files, tag,
    describe. No rebase/reset/config/submodule.
  HTTP: HttpGet, HttpPost, HttpPut, HttpDelete
  Text: TextRegex, TextReplace, TextSplit, TextJoin, TextTrim,
    TextLength, TextSubstring, TextLines, TextToUpper, TextToLower,
    TextBase64Encode, TextBase64Decode, TextUrlEncode, TextUrlDecode,
    TextHtmlEncode, TextContains, TextStartsWith, TextEndsWith,
    TextMatch, TextHash, TextSort, TextUniq, TextCount,
    TextIndexOf, TextLastIndexOf, TextRemove, TextWordCount,
    TextReverse, TextPadLeft, TextPadRight, TextRepeat,
    TextRegexGroups, TextColumn, TextTable
  JSON: JsonParse, JsonQuery, JsonMerge, JsonKeys, JsonCount,
    JsonType, JsonFromPairs, JsonSet, JsonRemoveKey, JsonGet,
    JsonCompact, JsonStringify, JsonArrayFrom
  Env (read-only): EnvGet, EnvList
  System: SysWhoAmI, SysPwd, SysHostname, SysUptime, SysDate,
    SysDateFormat, SysTimestamp, SysOsInfo, SysCpuCount, SysTempDir,
    SysDiskUsage, SysDirSize, SysMemory, SysProcessList,
    SysDriveList, SysNetInfo, SysLogRead, SysLogSources,
    SysServiceList, SysServiceStatus
  Network: NetPing, NetDns, NetTlsCert, NetHttpStatus,
    NetTcpConnect, HttpLatency
  Path (no I/O): PathJoin, PathDir, PathFile, PathExt,
    PathStem, PathChangeExt
  Identity: GuidNew, GuidNewShort, RandomInt
  Time: TimeFormat, TimeParse, TimeAdd, TimeDiff
  Version: VersionCompare, VersionParse
  Encoding: HexEncode, HexDecode, BaseConvert
  Formatting: FormatBytes, FormatDuration
  Archive: ArchiveExtract
  Math: MathEval — basic arithmetic only
  Clipboard: ClipboardSet (write-only)
  URL: OpenUrl — validates HTTPS, returns URL
  Process search: ProcessFind
  Control: Echo, Sleep (max 30s), Assert, Fail
  Control flow: ForEach (max 256, no nesting), If
  Composition: Include [fragmentId]
  Introspection: Mk8Blacklist, Mk8Vocab, Mk8VocabList, Mk8FreeText,
    Mk8Env, Mk8Info, Mk8Templates, Mk8Verbs, Mk8Skills, Mk8Docs

Per-step: maxRetries, stepTimeout, label, onFailure ("goto:<label>"),
  captureAs, template, patches.
Options: maxRetries (0), retryDelay ("00:00:02"), stepTimeout ("00:00:30"),
  scriptTimeout ("00:05:00"), failureMode, maxOutputBytes, maxErrorBytes,
  pipeStepOutput (false).
Cleanup: runs after failure when failureMode = "StopAndCleanup".

Security: no interpreters, ProcRun whitelist only, workspace-relative
paths, HTTP ports 80/443 only, no private IPs, env allowlist,
gigablacklist on ALL args. Write-blocked: .exe .dll .so .js .sln .csproj
and build system files. Structurally impossible: sudo, pipes, chaining,
redirection, backgrounding.
