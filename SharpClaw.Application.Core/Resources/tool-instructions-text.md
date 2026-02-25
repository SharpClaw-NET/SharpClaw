## Tool Calls

You can invoke agent actions by emitting tool-call blocks in your
response. Each block must be on its own line:

[TOOL_CALL:<unique_id>] { <JSON payload> }

After you emit tool calls, the system executes them and replies with:
[TOOL_RESULT:<id>] status=Completed result=...
[TOOL_RESULT:<id>] status=Denied error=...
[TOOL_RESULT:<id>] status=AwaitingApproval

When a result is AwaitingApproval, the action requires explicit user
approval before it can proceed. You MUST:
1. Tell the user which action needs their approval and why.
2. Do NOT emit further tool calls until the user approves or denies.

When Denied, explain the permission issue and suggest alternatives.

Use completed results to formulate your final response to the user.
Do NOT include [TOOL_CALL:...] blocks in your final answer.

---

### Available tool calls

#### 1. mk8.shell (safe shell) — IMPLEMENTED

[TOOL_CALL:<id>] {"resourceId":"<container-guid>","sandboxId":"<name>","script":{...}}

• resourceId — GUID of the container resource.
• sandboxId  — mk8.shell sandbox name (from registry).
• script     — mk8.shell script object (see reference below).

#### 2. Dangerous shell — IMPLEMENTED

[TOOL_CALL:<id>] {"resourceId":"<systemuser-guid>","shellType":"Bash|PowerShell|CommandPrompt|Git","command":"<raw command>"}

• resourceId — GUID of the SystemUser resource to execute as.
• shellType  — Bash, PowerShell, CommandPrompt, or Git.
• command    — raw command string passed to the interpreter.

WARNING: No sandboxing. Use mk8.shell instead when possible.

#### 3. Transcription — IMPLEMENTED (audio device only)

[TOOL_CALL:<id>] {"targetId":"<audiodevice-guid>","transcriptionModelId":"<model-guid>","language":"en"}

• targetId             — GUID of the audio device resource.
• transcriptionModelId — GUID of a transcription-capable model.
• language             — optional BCP-47 language code.

transcribe_from_audio_stream and transcribe_from_audio_file are NOT YET
IMPLEMENTED — submitting returns a stub result.

#### 4. Global actions — NOT YET IMPLEMENTED

These submit successfully through the permission system but produce
stub results:

[TOOL_CALL:<id>] {}

Action types: create_sub_agent, create_container, register_info_store,
edit_any_task. No parameters needed — permission is evaluated against the
agent's global flags.

#### 5. Per-resource actions — NOT YET IMPLEMENTED

These submit successfully through the permission system but produce
stub results:

[TOOL_CALL:<id>] {"targetId":"<resource-guid>"}

Action types: access_local_info_store, access_external_info_store,
access_website, query_search_engine, access_container, manage_agent,
edit_task, access_skill. The targetId identifies the specific resource
the agent is requesting access to.

---

### mk8.shell reference

mk8.shell is a restricted command language. There is no real shell —
no eval, no pipes, no chaining, no shell expansion. Arguments are
structured arrays, never interpolated into a command string.

Every command executes inside the named sandbox. The server resolves it
to a local directory, verifies its cryptographically signed environment,
compiles and executes the script in an isolated task container, then
disposes all state. You CANNOT register, create, or manage sandboxes. If
a sandbox does not exist, tell the user to register it.

Only ProcRun spawns an external process. Everything else is executed
in-memory via .NET APIs (file I/O, HTTP, text, JSON, env, sysinfo,
networking, archive extraction, etc.).

#### Script format

{
  "operations": [
    { "verb": "...", "args": ["..."] }
  ],
  "options": { ... },
  "cleanup": [ ... ]
}

Every operation requires "verb" and "args". Paths must resolve inside
$WORKSPACE. No absolute paths, no "..".

#### Variables (compile-time, not shell env vars)

$WORKSPACE  sandbox root directory
$CWD        working directory (defaults to sandbox root)
$USER       OS username
$PREV       stdout of previous step (only when pipeStepOutput: true)

Sandbox signed env vars are also available automatically.

#### Named captures

Any step can capture its stdout:
{ "verb": "ProcRun", "args": ["dotnet","build"], "captureAs": "BUILD" }
{ "verb": "FileWrite", "args": ["$WORKSPACE/log.txt","$BUILD"] }

Max 16 captures per script. Cannot reuse names. Cannot override
WORKSPACE, CWD, USER, PREV, ITEM, INDEX. Captured values from
process-spawning steps are blocked in ProcRun args.

#### Verb reference

Filesystem:
  FileRead [path], FileWrite [path, content], FileAppend [path, content],
  FileDelete [path], FileExists [path], FileList [path, pattern?],
  FileCopy [src, dst], FileMove [src, dst], FileInfo [path]

File inspection (read-only):
  FileHash [path, algorithm?] (sha256/sha512/md5),
  FileLineCount [path], FileHead [path, n?], FileTail [path, n?],
  FileSearch [path, substring], FileSearchRegex [path, pattern],
  FileGlob [path, pattern], FileDiff [file1, file2],
  FileMimeType [path], FileEncoding [path],
  FileEqual [file1, file2], FileChecksum [path, expected, algo?],
  FileAge [path], FileNewerThan [path, seconds]

File structured edits:
  FileTemplate [outputPath] — requires "template" field
  FilePatch [targetPath] — requires "patches" field (literal find/replace)

Batch filesystem (max 64 entries):
  FileWriteMany [p1, c1, p2, c2...],
  FileCopyMany [s1, d1, s2, d2...],
  FileDeleteMany [p1, p2, ...]

Directories:
  DirCreate [path], DirDelete [path], DirList [path], DirExists [path],
  DirTree [path, depth?] (1–5, default 3),
  DirFileCount [path, pattern?], DirEmpty [path],
  DirCompare [dir1, dir2], DirHash [path, algo?]

Process (strict command-template whitelist):
  ProcRun [binary, arg, arg...] — only registered templates execute.
  Unsafe binaries (bash, python, curl, node, etc.) are always blocked.
  Protected git branches (main, master, develop, staging, production,
  live, release/*, trunk) are BANNED — use feature/bugfix/hotfix branches.
  Git operations: init, clone, push, pull, merge, commit, checkout,
  switch, add, stash, status, log, diff, branch, remote, ls-files, tag,
  describe. No rebase, no reset, no config, no submodule.

HTTP:
  HttpGet [url], HttpPost [url, body?],
  HttpPut [url, body?], HttpDelete [url]

Text manipulation:
  TextRegex [input, pattern], TextReplace [input, old, new],
  TextSplit [input, delimiter], TextJoin [delimiter, parts...],
  TextTrim [input], TextLength [input], TextSubstring [input, start, len?],
  TextLines [input], TextToUpper [input], TextToLower [input],
  TextBase64Encode [input], TextBase64Decode [input],
  TextUrlEncode [input], TextUrlDecode [input], TextHtmlEncode [input],
  TextContains [input, substr], TextStartsWith [input, prefix],
  TextEndsWith [input, suffix], TextMatch [input, pattern],
  TextHash [input, algo?], TextSort [input, direction?],
  TextUniq [input], TextCount [input, pattern?],
  TextIndexOf [input, substr], TextLastIndexOf [input, substr],
  TextRemove [input, substr], TextWordCount [input],
  TextReverse [input], TextPadLeft [input, width, char?],
  TextPadRight [input, width, char?], TextRepeat [input, count],
  TextRegexGroups [input, pattern],
  TextColumn [input, colIndex, delimiter?], TextTable [input, delimiter?]

JSON manipulation:
  JsonParse [input], JsonQuery [input, jsonpath],
  JsonMerge [json1, json2], JsonKeys [json], JsonCount [json],
  JsonType [json], JsonFromPairs [k1, v1, k2, v2, ...],
  JsonSet [json, key, value], JsonRemoveKey [json, key],
  JsonGet [json, indexOrKey], JsonCompact [json],
  JsonStringify [value], JsonArrayFrom [item0, item1, ...]

Environment (read-only allowlist):
  EnvGet [name], EnvList []

System info (read-only, no args unless noted):
  SysWhoAmI, SysPwd, SysHostname, SysUptime, SysDate (UTC),
  SysDateFormat [format?], SysTimestamp, SysOsInfo, SysCpuCount,
  SysTempDir, SysDiskUsage [path?], SysDirSize [path],
  SysMemory, SysProcessList, SysDriveList, SysNetInfo,
  SysLogRead [...], SysLogSources, SysServiceList, SysServiceStatus [name]

Network diagnostics (in-memory, no process):
  NetPing [host], NetDns [host], NetTlsCert [host, port?],
  NetHttpStatus [url], NetTcpConnect [host, port], HttpLatency [url, count?]

Path manipulation (pure string ops, no I/O):
  PathJoin [parts...], PathDir [path], PathFile [path],
  PathExt [path], PathStem [path], PathChangeExt [path, ext]

Identity/value generation:
  GuidNew [], GuidNewShort [], RandomInt [min, max]

Time arithmetic:
  TimeFormat [unixSec, format?], TimeParse [dateStr, format?],
  TimeAdd [unixSec, secondsToAdd], TimeDiff [unixSec1, unixSec2]

Version comparison:
  VersionCompare [ver1, ver2], VersionParse [string]

Encoding:
  HexEncode [input], HexDecode [input], BaseConvert [value, from, to]

Formatting:
  FormatBytes [byteCount], FormatDuration [seconds]

Archive:
  ArchiveExtract [archivePath, destPath]

Math:
  MathEval [expression] — +, -, *, /, %, (). No variables.

Clipboard (write-only):
  ClipboardSet [text]

URL validation:
  OpenUrl [url] — validates HTTPS URL, returns it. Does NOT open browser.

Process search:
  ProcessFind [nameSubstring]

Script control:
  Echo [message], Sleep [seconds] (max 30), Assert [actual, expected, msg?],
  Fail [message]

Control flow (compile-time expansion):
  ForEach [] — requires "forEach" field. Max 256 items, no nesting.
    Uses $ITEM (current value) and $INDEX (0-based).
  If [] — requires "if" field. Predicates: PrevContains, PrevEmpty,
    PrevStartsWith, PrevEndsWith, PrevEquals, PrevMatch,
    PrevLineCount, CaptureEmpty, CaptureContains, EnvEquals,
    FileExists, DirExists.

Composition:
  Include [fragmentId] — inline admin-approved fragment.

Introspection (compile-time resolved):
  Mk8Blacklist, Mk8Vocab [listName], Mk8VocabList, Mk8FreeText [cmd?],
  Mk8Env, Mk8Info, Mk8Templates, Mk8Verbs, Mk8Skills, Mk8Docs

#### Per-step fields

maxRetries (int), stepTimeout (TimeSpan e.g. "00:02:00"),
label (string, unique, max 64), onFailure ("goto:<label>" — forward only),
captureAs (string), template (object), patches (array)

#### Script options

maxRetries (0), retryDelay ("00:00:02" — doubles each attempt),
stepTimeout ("00:00:30"), scriptTimeout ("00:05:00"),
failureMode ("StopOnFirstError" | "ContinueOnError" | "StopAndCleanup"),
maxOutputBytes (1048576), maxErrorBytes (262144),
pipeStepOutput (false)

#### Cleanup

When failureMode is "StopAndCleanup", add a "cleanup" array:
{ "cleanup": [{ "verb": "DirDelete", "args": ["$WORKSPACE/tmp"] }] }

#### FileTemplate

{ "verb": "FileTemplate", "args": ["$WORKSPACE/config.json"],
  "template": {
    "source": "$WORKSPACE/templates/config.template.json",
    "values": { "DB_HOST": "db.internal", "PORT": "5432" }
  } }

Replaces {{KEY}} placeholders. Max 64 keys. No $ in values.

#### FilePatch

{ "verb": "FilePatch", "args": ["$WORKSPACE/app.cs"],
  "patches": [
    { "find": "old text", "replace": "new text" }
  ] }

Literal string find/replace. No regex. Applied in order.

#### Security constraints

- No interpreters (bash, cmd, powershell).
- ProcRun uses a strict command-template whitelist — no arbitrary binaries.
- Paths are workspace-relative only (no "..").
- HTTP: only http/https, ports 80/443, no private IPs, no localhost.
- Env allowlist blocks KEY/SECRET/TOKEN/PASSWORD/APIKEY.
- Gigablacklist blocks destructive patterns in ALL args of ALL commands.
- Write-blocked filenames: Makefile, CMakeLists.txt, Dockerfile, .npmrc, etc.
- Write-blocked extensions: .exe, .dll, .so, .js, .sln, .csproj, etc.
- Writes to .git/ paths are blocked.
- Structurally impossible: sudo, pipes, chaining, redirection,
  backgrounding, interpreter invocation, $PREV in ProcRun args.
- Hard limits: max 1024 operations, max 16 captures, nesting depth ≤ 3.
