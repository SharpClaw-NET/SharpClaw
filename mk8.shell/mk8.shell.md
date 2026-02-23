# mk8.shell — Agent Command Language

## Overview

mk8.shell is a restricted pseudocode shell language that agents use to execute
system operations. By design, **there is no generic shell execution verb** — agents
can only use the closed set of verbs defined in the `Mk8ShellVerb` enum.

Scripts are compiled server-side into either in-memory .NET API calls or
tightly-validated process invocations. Agents never see or interact with a real
shell. Arguments are always passed as structured arrays — never concatenated
into a command string — eliminating the injection surface entirely.

**Only ProcRun spawns an external process.** Everything else (file I/O,
HTTP, text, env, sysinfo) is executed in-memory via .NET APIs. The compiler
never emits bash, cmd, or powershell as an executable.

## Execution Model

mk8.shell runs entirely within .NET — **no terminal or shell process is
ever involved**.

The compiler (`Mk8ShellCompiler`) produces `Mk8CompiledCommand` records.
Each record carries an `Mk8CommandKind` that tells the executor how to
dispatch it:

| `Mk8CommandKind` | Verb categories | Runtime mechanism |
|---|---|---|
| `InMemory` | File*, Dir*, HTTP, Text*, Json*, Env, Sys*, FileTemplate, FilePatch, FileHash, DirTree, Clipboard, Math, OpenUrl, NetPing, NetDns, NetTlsCert, NetHttpStatus, ArchiveExtract | .NET APIs directly (`File.ReadAllTextAsync`, `HttpClient`, `System.Security.Cryptography`, `System.Data.DataTable`, `System.Net.NetworkInformation.Ping`, `System.Net.Security.SslStream`, `System.IO.Compression`, `System.Text.Json.Nodes`, etc.) — no process spawned |
| `Process` | ProcRun | `System.Diagnostics.Process` with `UseShellExecute = false`, args via `ArgumentList` |

> **Git via command-template whitelist.**  Git operations are available
> through the strict command-template whitelist (see ProcRun
> section) — only the exact templates registered in `Mk8GitCommands`
> can execute, and no unregistered flag can reach git.  Write
> operations (commit, checkout, switch, push, pull, merge) are
> constrained to compile-time word lists.  Protected branches (main,
> master, develop, staging, production, live, release/*, trunk) are
> intentionally excluded — agents must work in feature/bugfix/hotfix
> branches only.

**Why no shell?** `ProcessStartInfo.UseShellExecute = false` combined with
`ArgumentList.Add(arg)` passes each argument individually to the OS
process-creation syscall (`execve` on Linux, `CreateProcessW` on Windows).
Shell metacharacters in argument values (`; rm -rf /`, `$(cmd)`, backticks)
are treated as literal text — they are never interpreted because no shell
parses them.

## Design Principles

1. **Closed verb set** — if it's not in `Mk8ShellVerb`, it cannot be executed
2. **Structured arguments** — `string[]`, never interpolated into a shell string
3. **No shell involved** — compilation targets `ProcessStartInfo.ArgumentList`
4. **Per-verb sanitization** — each verb handler validates its own argument types
5. **Platform-agnostic input** — one script runs on Linux, Windows, or macOS
6. **Scoped paths** — paths can only descend into workspace subdirectories
7. **Compile-time expansion** — ForEach/If are unrolled before execution

## Script Format

A script is an ordered list of operations. The caller provides a **sandbox
ID** and a **script** as separate parameters. mk8.shell handles everything
else: it resolves the sandbox from its own `%APPDATA%/mk8.shell` registry,
verifies the signed environment, builds the workspace context, compiles
the script, executes it, and returns results. The sandbox ID is not part
of the script JSON.

```json
{
  "operations": [
    { "verb": "DirList",   "args": ["$WORKSPACE/app"] },
    { "verb": "FileRead",  "args": ["$WORKSPACE/app/config.json"] },
    {
      "verb": "ProcRun", "args": ["dotnet", "build"],
      "maxRetries": 2,
      "stepTimeout": "00:02:00",
      "label": "build",
      "onFailure": "goto:cleanup",
      "captureAs": "BUILD_OUTPUT"
    },
    { "verb": "FileWrite", "args": ["$WORKSPACE/out.txt", "$BUILD_OUTPUT"] },
    { "verb": "DirDelete", "args": ["$WORKSPACE/tmp"], "label": "cleanup" }
  ],
  "cleanup": [
    { "verb": "DirDelete",  "args": ["$WORKSPACE/publish"] },
    { "verb": "FileDelete", "args": ["$WORKSPACE/build.lock"] }
  ],
  "options": {
    "maxRetries": 1,
    "retryDelay": "00:00:02",
    "stepTimeout": "00:00:30",
    "scriptTimeout": "00:05:00",
    "failureMode": "StopAndCleanup",
    "pipeStepOutput": true
  }
}
```

### Per-Step Fields

| Field        | Type        | Default | Description                                                     |
|--------------|-------------|--------|-----------------------------------------------------------------|
| verb         | Mk8ShellVerb| —      | Required. The operation to perform.                              |
| args         | string[]    | —      | Required. Structured arguments (never shell-concatenated).       |
| maxRetries   | int?        | null   | Per-step retry count. Overrides script-level default.            |
| stepTimeout  | TimeSpan?   | null   | Per-step timeout. Overrides script-level default.              |
| label        | string?     | null   | Step label for jump targets. Unique within script.              |
| onFailure    | string?     | null   | Forward jump on failure, e.g. `"goto:cleanup"`.                 |
| captureAs    | string?     | null   | Capture stdout into a named variable (e.g. `"BUILD_OUTPUT"`).   |
| forEach      | Mk8ForEach? | null   | Loop body for `ForEach` verb.                                    |
| if           | Mk8Conditional? | null | Predicate + guarded op for `If` verb.                           |
| template     | Mk8FileTemplate? | null | Template definition for `FileTemplate` verb.                   |
| patches      | Mk8PatchEntry[]? | null | Patch list for `FilePatch` verb.                               |

## Execution Options

| Option          | Type           | Default         | Description                                                  |
|-----------------|----------------|-----------------|--------------------------------------------------------------|
| maxRetries      | int            | 0               | Default per-step retry count. Per-step `maxRetries` overrides. |
| retryDelay      | TimeSpan       | 2s              | Base delay between retries (exponential back-off).           |
| stepTimeout     | TimeSpan       | 30s             | Max wall-clock time per step before kill.                    |
| scriptTimeout   | TimeSpan       | 5min            | Max wall-clock time for the entire script.                   |
| failureMode     | enum           | StopOnFirstError| `StopOnFirstError`, `ContinueOnError`, `StopAndCleanup`.    |
| maxOutputBytes  | int            | 1 MB            | Max stdout captured per step (truncated).                    |
| maxErrorBytes   | int            | 256 KB          | Max stderr captured per step (truncated).                    |
| pipeStepOutput  | bool           | false           | Makes `$PREV` available — stdout of last step.              |

### Retry Behaviour

- Steps retry on non-zero exit code only (not on compile errors).
- Per-step `maxRetries` overrides the script-level default.
- Delay doubles each attempt: `retryDelay * 2^(attempt-1)`.

### Failure Modes

| Mode              | Behaviour                                              |
|-------------------|--------------------------------------------------------|
| StopOnFirstError  | Abort immediately. Remaining steps are skipped.        |
| ContinueOnError   | Log failure, continue to next step.                    |
| StopAndCleanup    | Abort, then run the `cleanup` operation list.          |

#### StopAndCleanup

When `failureMode` is `StopAndCleanup`, the script accepts a separate
`cleanup` operation list that runs after any failure. Cleanup steps execute
with `ContinueOnError` semantics (best-effort) and go through the same
compilation pipeline — same path validation, same verb restrictions, no
elevated privileges.

```json
{
  "operations": [
    { "verb": "ProcRun", "args": ["dotnet", "build"] },
    { "verb": "ProcRun", "args": ["dotnet", "publish", "-o", "$WORKSPACE/publish"] }
  ],
  "cleanup": [
    { "verb": "DirDelete",  "args": ["$WORKSPACE/publish"] },
    { "verb": "FileDelete", "args": ["$WORKSPACE/build.lock"] }
  ],
  "options": { "failureMode": "StopAndCleanup" }
}
```

Cleanup operations are compiled identically to main operations and included
in `Mk8CompiledScript.CleanupCommands`. If no `cleanup` list is provided
and a step fails, `StopAndCleanup` behaves like `StopOnFirstError`.

#### Step Labels + Goto-on-Failure

For finer-grained failure handling, individual steps can declare a `label`
and an `onFailure` forward jump:

```json
{ "verb": "ProcRun", "args": ["dotnet", "build"], "label": "build", "onFailure": "goto:deploy" },
{ "verb": "ProcRun", "args": ["dotnet", "test"] },
{ "verb": "ProcRun", "args": ["dotnet", "publish"], "label": "deploy" }
```

**Rules** (enforced by `Mk8LabelValidator` at compile time):
- Labels must be unique within a script.
- Jump targets must reference existing labels.
- Only **forward jumps** allowed — no backward jumps, preventing loops.
- The jump graph is validated as a DAG — no cycles possible.
- Label names: alphanumeric, hyphens, underscores only (max 64 chars).
- Labels are metadata — not executable. No new attack surface.

The compiled script includes a `LabelIndex` (label → step index mapping)
that the executor uses to skip ahead on failure.

### Per-Step Timeout Overrides

Per-step `stepTimeout` overrides the script-level `options.stepTimeout`.

```json
{ "verb": "ProcRun",  "args": ["dotnet", "build"],       "stepTimeout": "00:02:00" },
{ "verb": "FileRead", "args": ["$WORKSPACE/output.json"], "stepTimeout": "00:00:05" }
```

This allows slow steps (builds) to have generous timeouts while fast steps
(file reads) that hang are killed promptly.

## Workspace Context

mk8.shell is a **self-contained terminal system**. The caller provides a
sandbox ID and a script — mk8.shell resolves, verifies, compiles, executes,
and returns results entirely on its own. It does not rely on any external
system for sandbox resolution, path validation, or environment loading.

### Sandbox Lifecycle

Every mk8.shell invocation targets a named **sandbox** — an isolated
directory registered via `mk8.shell.startup`. mk8.shell performs the full
lifecycle internally via `Mk8TaskContainer.Create(sandboxId)`:

1. **Load global env** from `mk8.shell.base.env` (ships with the assembly).
2. **Resolve sandbox ID** → local path from mk8.shell's own
   `%APPDATA%/mk8.shell/sandboxes.json` registry.
3. **Verify signature** — read `mk8.shell.signed.env` from sandbox root,
   verify HMAC-SHA256 with the machine-local key.
4. **Extract env vars** from the verified signed content (never from the
   unsigned `.env` — that file exists only for user convenience).
5. **Build `Mk8WorkspaceContext`** with merged environment.
6. **Compile and execute** the script inside an isolated task container.
7. **Dispose** — all state is discarded. Nothing transfers between invocations.

### Sandbox Registration

Sandboxes are created exclusively via `mk8.shell.startup` (the
`Mk8SandboxRegistrar.Register` method). mk8.shell itself has no ability to
create, modify, or manage sandboxes.

Registration creates:
- `mk8.shell.env` — user-editable KEY=VALUE file (never read by mk8.shell at runtime)
- `mk8.shell.signed.env` — cryptographically signed copy (HMAC-SHA256, machine-local key)

Both files are **GIGABLACKLISTED** — mk8.shell commands cannot read, write,
copy, move, delete, hash, or access them in any way. Only the user or
mk8.shell.startup may touch them.

Sandbox names must contain only English letters (A-Z, a-z) and digits (0-9).

### Local Registry (`%APPDATA%/mk8.shell/`)

```
%APPDATA%/mk8.shell/
├── sandboxes.json            ← sandbox ID → { rootPath, registeredAtUtc }
├── mk8.shell.key             ← 256-bit HMAC signing key (machine-local)
└── history/
    ├── Banana_20250101_120000.signed.env
    └── ...                   ← timestamped backup of every signing
```

The signing key ensures that signed env files cannot be copied between
machines without re-signing.

### Where Variables Load From

```
Sandbox Registry (local)
├── RootPath               → $WORKSPACE, Mk8PathSanitizer boundary
└── mk8.shell.signed.env   → additional variables (verified first)

Global (mk8.shell.base.env)
├── ProjectBases            → Mk8RuntimeConfig for command whitelist
├── GitRemoteUrls           → Mk8RuntimeConfig for command whitelist
└── GitCloneUrls            → Mk8RuntimeConfig for command whitelist

OS
└── Username                → $USER
```

`$CWD` defaults to the sandbox root. `$WORKSPACE` is always the sandbox root.

### Variable Shortcuts

Variables are resolved at **compile time** — before any process is spawned.
They are not shell environment variables.

| Variable     | Source                           | Example value                    |
|--------------|----------------------------------|----------------------------------|
| `$WORKSPACE` | Sandbox root path (from registry)| `/home/deploy/sandbox`           |
| `$CWD`       | Working directory (= sandbox root)| `/home/deploy/sandbox`          |
| `$USER`      | OS username                      | `deploy`                         |
| `$PREV`      | stdout of previous step          | `Build succeeded. 0 warnings.`   |

#### How `$PREV` Works

When `pipeStepOutput: true`:

```json
{ "verb": "ProcRun",  "args": ["dotnet", "build"] }
  → stdout: "Build succeeded.\n    0 Warning(s)\n    0 Error(s)"
  → $PREV = "Build succeeded.\n    0 Warning(s)\n    0 Error(s)"

{ "verb": "FileWrite", "args": ["$WORKSPACE/build.log", "$PREV"] }
  → writes the build output to a file
```

When `pipeStepOutput: false` (default), `$PREV` is always empty.

#### Custom Variables

Additional variables are loaded from the sandbox's signed environment file
(`mk8.shell.signed.env`). These are verified on every command startup and
made available as `$NAME` in scripts. Custom variables cannot override
built-in variables (`WORKSPACE`, `CWD`, `USER`, `PREV`).

#### Named Captures (`captureAs`)

Any step can capture its stdout into a named variable via `captureAs`.
The captured value is stored in the variable dictionary and accessible
via `$NAME` in subsequent steps:

```json
{ "verb": "ProcRun", "args": ["dotnet", "build"], "captureAs": "BUILD_OUTPUT" },
{ "verb": "ProcRun", "args": ["dotnet", "test"],  "captureAs": "TEST_OUTPUT" },
{ "verb": "FileWrite", "args": ["$WORKSPACE/report.txt", "$BUILD_OUTPUT\n$TEST_OUTPUT"] }
```

**Capture rules** (enforced by `Mk8VariableResolver.ValidateCaptures`):
- Max 16 captured variables per script (`Mk8CaptureRules.MaxCaptures`).
- Names may only contain letters, digits, and underscores.
- Cannot override built-ins: `WORKSPACE`, `CWD`, `USER`, `PREV`, `ITEM`, `INDEX`.
- Capture names must be unique within a script.
- Captured variables from process-spawning steps (ProcRun) are
  **blocked in ProcRun arguments** — same injection prevention as `$PREV`.
- Capture size is enforced by existing `maxOutputBytes`.

## Verb Reference

### Filesystem

| Verb       | Args                      | Description                          | Linux equivalent      |
|------------|---------------------------|--------------------------------------|-----------------------|
| FileRead   | `[path]`                  | Read entire file contents            | `cat <path>`          |
| FileWrite  | `[path, content]`         | Overwrite file with content          | `echo "content" > f`  |
| FileAppend | `[path, content]`         | Append content to file               | `echo "content" >> f` |
| FileDelete | `[path]`                  | Delete a file                        | `rm <path>`           |
| FileExists | `[path]`                  | Check if file exists (returns bool)  | `test -f <path>`      |
| FileList   | `[path, pattern?]`        | List files matching optional glob    | `ls <path>`           |
| FileCopy   | `[source, destination]`   | Copy a file                          | `cp <src> <dst>`      |
| FileMove   | `[source, destination]`   | Move or rename a file                | `mv <src> <dst>`      |
| FileHash   | `[path, algorithm?]`      | Compute file hash (sha256/sha512/md5)| `sha256sum <path>`    |
| FileInfo   | `[path]`                  | File metadata (size, dates, attrs)   | `stat <path>`         |

`FileHash` returns the hex digest. The algorithm argument defaults to
`sha256`. Executed in-memory via `System.Security.Cryptography` — no
external process, read-only, path must be in sandbox.

`FileInfo` returns size in bytes, creation date (UTC), last modified date
(UTC), and file attributes. Executed in-memory via `System.IO.FileInfo` —
no external process, read-only, path must be in sandbox.

### Filesystem — Structured Edits

| Verb         | Args              | Description                               |
|--------------|-------------------|-------------------------------------------|
| FileTemplate | `[outputPath]`    | Write file from template + replacement values |
| FilePatch    | `[targetPath]`    | Apply ordered find/replace patches to a file  |

#### FileTemplate

Reads a template file, replaces `{{KEY}}` placeholders with literal values,
and writes the result. No eval, no expression language.

```json
{
  "verb": "FileTemplate",
  "args": ["$WORKSPACE/app/appsettings.json"],
  "template": {
    "source": "$WORKSPACE/templates/appsettings.template.json",
    "values": {
      "DB_HOST": "postgres.internal",
      "APP_PORT": "8080"
    }
  }
}
```

**Constraints:**
- Template source must be in sandbox (validated by `Mk8PathSanitizer.Resolve`).
- Output path goes through `ResolveForWrite` (extension blocking applies).
- Values are literal strings only — `$` characters in values are **blocked**
  to prevent variable injection (`{{$PREV}}` attack).
- Max 64 replacement keys (`Mk8FileTemplate.MaxKeys`).

#### FilePatch

Applies ordered find/replace patches to an existing file. Each patch is a
literal string match — no regex (use `TextRegex` separately if needed).
The verb reads the file, applies patches sequentially, writes the result.

```json
{
  "verb": "FilePatch",
  "args": ["$WORKSPACE/app/config.yaml"],
  "patches": [
    { "find": "port: 3000", "replace": "port: 8080" },
    { "find": "debug: true", "replace": "debug: false" }
  ]
}
```

**Constraints:**
- File path goes through `ResolveForWrite` (extension blocking applies).
- Patch values are literal strings — `$` characters are **blocked**.
- Max 32 patches per operation (`Mk8PatchEntry.MaxPatches`).
- `find` cannot be empty.

### Directory

| Verb      | Args              | Description                          | Linux equivalent      |
|-----------|-------------------|--------------------------------------|-----------------------|
| DirCreate | `[path]`          | Create directory (recursive)         | `mkdir -p <path>`     |
| DirDelete | `[path]`          | Delete directory recursively         | `rm -rf <path>`       |
| DirList   | `[path]`          | List immediate children              | `ls <path>`           |
| DirExists | `[path]`          | Check if directory exists            | `test -d <path>`      |
| DirTree   | `[path, depth?]`  | Recursive listing up to depth N      | `tree -L <N> <path>`  |

`DirTree` returns a structured recursive listing (one path per line,
indented). Depth defaults to 3, max 5. Read-only, in-memory, no glob.
Path must be in sandbox.

### Process (Strict Command-Template Whitelist)

| Verb    | Args                         | Description                            |
|---------|------------------------------|----------------------------------------|
| ProcRun | `[binary, arg0, arg1, ...]`  | Run a whitelisted command template     |

ProcRun uses a **strict command-template whitelist** (`Mk8CommandWhitelist`).
There is no "allowed binary + blocked flags" model — every invocation must
match a registered `Mk8AllowedCommand` template EXACTLY.  Each template
defines fixed prefix args, optional flags with typed values, and positional
parameter slots.  The agent can NEVER inject free text into any argument
position.

Templates and word lists are compile-time constants defined in the
`Commands/` directory (one file per tool category).  They cannot be modified
at runtime — to change the allowed surface, a developer edits the source file
and recompiles.

#### Slot types

| Kind | Description | Example |
|---|---|---|
| `Choice` | Value must exactly match one of a fixed set | `Release` or `Debug` |
| `SandboxPath` | Value must resolve inside the sandbox via `Mk8PathSanitizer` | `$WORKSPACE/src/app.cs` |
| `AdminWord` | Value must be a single entry from a named word list | `Initial` from MigrationNames |
| `IntRange` | Value must be an integer in [min, max] | `1`–`100` |
| `ComposedWords` | Value is split on spaces; each word must be in a named word list (max 12 words) | `"Fix build errors"` → `["Fix","build","errors"]` all in CommitWords |
| `CompoundName` | Runtime base name + optional compile-time suffix (the ONLY runtime exception) | Base `"Banana"` → `"Banana"`, `"BananaApi"`, `"Banana.Api"` |

#### How ComposedWords works

Spaces are safe — `ProcessStartInfo.ArgumentList.Add("Fix build errors")`
passes the entire string as one OS-level argument.  No shell tokenizes it.

Each word is validated independently against the word list:
- `"Fix auth errors"` → `["Fix", "auth", "errors"]` → all in CommitWords → **allowed**
- `"Api 1 2 3"` → `["Api", "1", "2", "3"]` → all in CommitWords → **allowed**
- `"Api123"` → tries to match `"Api123"` as one word → NOT in list → **rejected**
- `"I hacked the mainframe"` → `"hacked"` NOT in list → **rejected**

ComposedWords is NOT concatenation.  `"Api 1 2 3"` is allowed (4 separate
words) but `"Api123"` is rejected (one word not in the list).  With single
letters A-Z in the word list, concatenation would accept ANY string,
defeating the whitelist.  This is why AdminWord (single exact match) is used
for migration names and branch names.

#### How CompoundName works (project names)

This is the **ONLY runtime exception** in the whitelist.  Project names are
environment-specific — pre-enumerating all possible base names at compile
time is impractical.  Creating a project is a one-off setup operation with
limited abuse surface.

The administrator provides base names at startup via `Mk8RuntimeConfig`:

```csharp
var config = new Mk8RuntimeConfig { ProjectBases = ["Banana", "SharpClaw"] };
var whitelist = Mk8CommandWhitelist.CreateDefault(config);
```

The whitelist pre-computes all valid combinations at construction time:

- `"Banana"` — base alone
- `"BananaApi"` — base + suffix (direct concatenation)
- `"Banana.Api"` — base + "." + suffix (dot-separated)
- `"Banana.Application.API"` — base + "." + compound suffix

Compile-time suffixes (`Mk8DotnetCommands.ProjectSuffixes`): `App`, `Api`,
`Core`, `Infrastructure`, `Contracts`, `Tests`, `Utils`, `Client`, `Server`,
`Worker`, `Service`, `Web`, `Grpc`, `Shared`, `Common`, `Domain`, `Data`,
`Models`, `Handlers`, `Extensions`, plus compound suffixes like
`Application.API`, `Application.Core`, `PublicAPI`, `UITests`, etc.

If no base names are configured, `dotnet new -n` is unavailable (but
`dotnet new <template>` without `-n` still works — uses directory name).

#### FreeText — Sanitized Free-Form Text

A new slot kind that allows agents to write arbitrary text (e.g., commit
messages) without being constrained to a fixed vocabulary. **When disabled**
(the default), slots typed as `FreeText` fall back to `ComposedWords`
validation using the same word list.

**Configuration** is loaded from `mk8.shell.base.env` (global) and the
sandbox's signed env (local). Local overrides global for scalar values;
per-verb entries merge additively.

```json
{
  "FreeText": {
    "enabled": false,
    "maxLength": 200,
    "perVerb": {
      "git commit": { "enabled": true, "maxLength": 200 },
      "git tag create": { "enabled": true, "maxLength": 128 },
      "git tag annotated": { "enabled": true, "maxLength": 200 },
      "git tag delete": { "enabled": true, "maxLength": 128 },
      "git merge": { "enabled": true, "maxLength": 200 },
      "dotnet ef migrations add": { "enabled": true, "maxLength": 128 }
    }
  }
}
```

**Unsafe binaries** — the following can NEVER have FreeText-typed slots,
regardless of configuration. This is a compile-time constant:

> `bash`, `sh`, `zsh`, `cmd`, `powershell`, `pwsh`, `python`, `python3`,
> `ruby`, `perl`, `lua`, `php`, `node`, `npx`, `deno`, `bun`, `sudo`,
> `su`, `chmod`, `chown`, `curl`, `wget`, `ssh`, `scp`

**Sanitization** (when FreeText IS enabled):

1. **Max length**: per-slot override → per-verb config → global config.
2. **Control characters**: all except space are blocked (null, newlines, tabs).
3. **Secret patterns**: `KEY=`, `TOKEN=`, `PASSWORD=`, `APIKEY=`, etc.
4. **Gigablacklist**: the full gigablacklist is enforced on every value.

**Command-specific extra validation** (applied after generic sanitization):

- **`dotnet ef migrations add`**: value must be a valid C# identifier —
  starts with a letter or underscore, contains only letters/digits/underscores,
  no spaces. EF generates a class from the name.
- **`git tag create`/`git tag annotated`/`git tag delete`** (name slot):
  value must be a valid git ref name — no spaces, no `..`, no `~^:?*[\`,
  cannot start/end with `.` or `/`, cannot end with `.lock`, no consecutive
  slashes.

**FreeText commands** currently registered:

| Command | Slot | Fallback | Max Length | Extra Validation |
|---|---|---|---|---|
| `git commit -m` | message | CommitWords | 200 | — |
| `git tag <name>` | name | TagNames | 128 | git-ref safe |
| `git tag -a <name> -m <msg>` | name | TagNames | 128 | git-ref safe |
| `git tag -a <name> -m <msg>` | message | CommitWords | 200 | — |
| `git tag -d <name>` | name | TagNames | 128 | git-ref safe |
| `git merge <branch> -m <msg>` | message | CommitWords | 200 | — |
| `dotnet ef migrations add` | name | MigrationNames | 128 | C# identifier |

#### Vocabularies — Env-Sourced Word Lists

Vocabularies for `ComposedWords` / `FreeText`-fallback are no longer
purely compile-time. They are now loaded from env files and merged
additively with the hardcoded constants from `Commands/` source files.

**On first startup**, if `mk8.shell.base.env` is missing or empty, all
compile-time vocabularies are serialized into it as defaults.

**Per-sandbox vocabularies** use env keys prefixed with `MK8_VOCAB_`:

```
MK8_VOCAB_CommitWords=Sprint,Backlog,Standup,Retrospective
MK8_VOCAB_BranchNames=feature/sprint-1,feature/sprint-2
```

Global and sandbox vocabularies **add together** — neither overrides
the other. A word in either source is valid.

#### Gigablacklist Enforcement

The gigablacklist is an **unconditional, non-bypassable** safety layer
that runs on ALL arguments of ALL commands (both ProcRun and in-memory
verbs) after variable resolution and before any other validation.

If ANY pattern in the list appears ANYWHERE in ANY argument, the entire
operation fails before compilation with `Mk8GigaBlacklistException`:

```
Gigablacklisted term not allowed: [mk8.shell.env] — Argument '...' contains gigablacklisted term.
```

The gigablacklist includes two compile-time groups plus env-sourced
custom patterns:

1. **mk8.shell env patterns** — `mk8.shell.env`, `mk8.shell.signed.env`,
   `mk8.shell.base.env`, `mk8.shell.key`. These protect the sandbox's
   own configuration and signing keys from agent access.
2. **Hardcoded patterns** — shell injection markers, destructive
   filesystem commands (rm -rf, format, mkfs, dd, diskpart, shred),
   raw block-device paths (/dev/sda, \\.\PhysicalDrive), system control
   commands (shutdown, reboot, halt, poweroff), process kill-all patterns,
   sensitive system files (/etc/shadow, /etc/sudoers), fork bombs, SQL
   destruction (DROP DATABASE, DROP TABLE, xp_cmdshell), Windows
   registry/service manipulation, and privilege escalation commands.
3. **Custom patterns** — loaded from `CustomBlacklist` in base.env
   (global, cached at startup) and `MK8_BLACKLIST` in sandbox signed env
   (loaded fresh per execution). Both sources merge additively. Patterns
   shorter than 2 characters are silently ignored.

**Disable flags** (base.env-only, ignored in sandbox env):

| Flag | Default | Effect |
|---|---|---|
| `DisableHardcodedGigablacklist` | `false` | Disables group 2 (hardcoded patterns). Group 1 (env patterns) stays active. |
| `DisableMk8shellEnvsGigablacklist` | `false` | Only takes effect when `DisableHardcodedGigablacklist` is also `true`. Disables group 1 (env patterns). |

> **⚠ WARNING:** `DisableHardcodedGigablacklist` should essentially
> never be set to `true` except in a dedicated test environment. The
> hardcoded patterns exist for a reason — they prevent agents from
> producing arguments referencing catastrophically destructive commands.
> Even when hardcoded patterns are disabled, the mk8.shell env/key
> patterns remain active by default. Setting *both* flags to `true`
> removes ALL compile-time protection — only custom patterns remain.
> This is strongly discouraged even in test environments.

#### Allowed command templates

**dotnet** (`Mk8DotnetCommands`):

| Template | Flags | Params |
|---|---|---|
| `dotnet --version` | — | — |
| `dotnet --info` | — | — |
| `dotnet --list-sdks` | — | — |
| `dotnet --list-runtimes` | — | — |
| `dotnet tool list` | `-g`, `--global` | — |
| `dotnet sln list` | — | — |
| `dotnet list reference` | — | — |
| `dotnet list package` | `--outdated`, `--deprecated`, `--vulnerable`, `--include-transitive`, `--format json` | — |
| `dotnet nuget list source` | `--format json\|Detailed\|Short` | — |
| `dotnet workload list` | — | — |
| `dotnet sdk check` | — | — |
| `dotnet build` | `--configuration Release\|Debug`, `--no-restore`, `-o SandboxPath`, `--verbosity quiet\|minimal\|normal\|detailed\|diagnostic` | — |
| `dotnet publish` | same as build | — |
| `dotnet test` | `--configuration`, `--no-restore`, `--no-build`, `--verbosity` | — |
| `dotnet clean` | `--configuration`, `--verbosity` | — |
| `dotnet restore` | `--no-cache`, `--verbosity` | — |
| `dotnet format` | `--verify-no-changes`, `--verbosity` | — |
| `dotnet new <template> -n <name>` | `-n`/`--name` CompoundName (runtime base + suffix), `-o` SandboxPath | template from DotnetTemplates |
| `dotnet ef migrations add <name>` | — | name: FreeText (C# identifier) with MigrationNames fallback |
| `dotnet ef migrations list` | — | — |
| `dotnet ef migrations script` | `--idempotent`, `-o` SandboxPath | — |
| `dotnet ef dbcontext info` | — | — |
| `dotnet ef dbcontext list` | — | — |

Word lists:
- **MigrationNames**: `Initial`, `Baseline`, `Seed`, `AddUsers`, `AddRoles`, ... `V1`–`V5`, `A`–`Z`, `0`–`9`
- **ProjectSuffixes**: `App`, `Api`, `Core`, `Infrastructure`, ... `Application.API`, `PublicAPI`, `UITests` (combined with runtime base names via `CompoundName`)
- **DotnetTemplates**: `console`, `classlib`, `webapi`, `web`, `mvc`, `razor`, `worker`, `mstest`, `nunit`, `xunit`, `blazorserver`, `blazorwasm`, `grpc`, `gitignore`, `editorconfig`, `globaljson`, `tool-manifest`

**git** (`Mk8GitCommands`):

Read-only:

| Template | Flags | Params |
|---|---|---|
| `git --version` | — | — |
| `git init` | — | — |
| `git status` | `--short`, `-s`, `--porcelain`, `--branch` | — |
| `git log --oneline` | `-n 1-100`, `--all`, `--no-decorate`, `--graph`, `--reverse` | — |
| `git log --oneline -- <path>` | `-n 1-100`, `--all`, `--no-decorate` | SandboxPath |
| `git diff` | `--staged`, `--cached`, `--stat`, `--name-only`, `--name-status`, `--word-diff` | — |
| `git diff <path>` | `--staged`, `--cached` | SandboxPath |
| `git branch` | `--list`, `-a`, `--all`, `-r` | — |
| `git remote` | `-v` | — |
| `git remote add <name> <url>` | — | name from RemoteNames, url from GitRemoteUrls (runtime) |
| `git remote remove <name>` | — | name from RemoteNames |
| `git rev-parse HEAD` | — | — |
| `git rev-parse --short HEAD` | — | — |
| `git ls-files` | `--modified`, `--deleted`, `--others`, `--ignored` | — |
| `git tag --list` / `git tag -l` | — | — |
| `git describe` | `--tags`, `--always`, `--long`, `--abbrev 1-40` | — |
| `git stash show` | `--stat`, `--patch`, `-p` | — |
| `git blame <path>` | `-L` from BlameLineRanges | SandboxPath |
| `git clean -n` / `--dry-run` | `-d` | — |
| `git count-objects` | `-v`, `-H` | — |
| `git cherry` | `-v` | — |
| `git shortlog -sn` | `--all`, `--no-merges` | — |
| `git rev-list --count HEAD` | — | — |

Write (constrained):

| Template | Flags | Params |
|---|---|---|
| `git add <paths>` | — | variadic SandboxPath |
| `git add .` | — | — |
| `git add -A` | — | — |
| `git commit` | `-m` FreeText with CommitWords fallback | — |
| `git stash` / `pop` / `drop` | — | — |
| `git stash list` | `--oneline` | — |
| `git tag <name>` | — | FreeText (git-ref safe) with TagNames fallback |
| `git tag -a <name> -m <msg>` | `-m` FreeText with CommitWords fallback | FreeText (git-ref safe) with TagNames fallback |
| `git tag -d <name>` | — | FreeText (git-ref safe) with TagNames fallback |
| `git checkout <branch>` | — | AdminWord from BranchNames |
| `git checkout -b <branch>` | — | AdminWord from BranchNames |
| `git switch <branch>` | — | AdminWord from BranchNames |
| `git switch -c <branch>` | — | AdminWord from BranchNames |
| `git clone <url>` | — | url from GitCloneUrls (runtime) |
| `git clone <url> <path>` | — | url from GitCloneUrls (runtime), SandboxPath |
| `git push <remote> <branch>` | — | remote from RemoteNames, branch from BranchNames |
| `git push -u <remote> <branch>` | `-u`, `--set-upstream` | remote from RemoteNames, branch from BranchNames |
| `git push --tags <remote>` | — | remote from RemoteNames |
| `git pull <remote> <branch>` | `--rebase`, `--no-rebase`, `--ff-only` | remote from RemoteNames, branch from BranchNames |
| `git merge <branch>` | `--no-ff`, `--ff-only`, `--squash`, `-m` FreeText with CommitWords fallback | AdminWord from BranchNames |
| `git merge --abort` | — | — |

Word lists:
- **CommitWords**: vocabulary of ~200 verbs, nouns, adjectives, connectors, letters, digits — agent composes messages by combining words with spaces (max 12 words)
- **BranchNames**: `feature/*`, `bugfix/*`, `hotfix/*`, plus single letters/digits
- **RemoteNames**: `origin`, `upstream`, `fork`, `backup`, `mirror`
- **GitRemoteUrls**: runtime-configured via `Mk8RuntimeConfig.GitRemoteUrls`
- **GitCloneUrls**: runtime-configured via `Mk8RuntimeConfig.GitCloneUrls`
- **BlameLineRanges**: pre-approved `-L` ranges (`1,10`, `1,20`, `1,50`, `1,100`, `1,200`, `1,500`, `1,1000`)
- **TagNames**: `v0.1.0`–`v3.1.0`, pre-release variants (`-alpha`, `-beta`, `-rc1`, `-rc2`), milestones (`baseline`, `checkpoint`, `snapshot`, `draft`, `initial`, `stable`, `latest`)

**Protected branches — BANNED:** `main`, `master`, `develop`, `staging`,
`production`, `live`, `release`, `release/*`, `trunk`.  These are
intentionally excluded from BranchNames.  Agents must NEVER operate on
branches used for live or master development.  All agent work must happen
in feature/bugfix/hotfix branches.  Push, pull, and merge are restricted
to the same branch word list — agents cannot push to or merge into
protected branches.

Not whitelisted (require dangerous-shell path): `rebase`, `reset`,
`clean -f`, `config`, `submodule`, `am`, `apply`, `filter-branch`,
`cherry-pick`, `bisect`, `gc`, `fsck`, `reflog`.

#### Runtime configuration (`Mk8RuntimeConfig`)

The **ONLY runtime exception** in the whitelist.  The administrator provides
environment-specific values at startup:

```csharp
var config = new Mk8RuntimeConfig
{
    ProjectBases = ["Banana", "SharpClaw"],
    GitRemoteUrls = ["https://github.com/org/repo.git"],
    GitCloneUrls = ["https://github.com/org/repo.git"],
};
var whitelist = Mk8CommandWhitelist.CreateDefault(config);
```

These are baked into the immutable whitelist at construction — they cannot be
changed after creation.

If no runtime config is provided, `dotnet new -n`, `git remote add`,
and `git clone` are unavailable (the agent gets a clear error message).

**node / npm** (`Mk8NodeNpmCommands`): `node --version`, `npm --version`,
`npm ls` (with `--depth 0-10`, `--all`, `--json`, `--prod`, `--dev`, `--long`),
`npm outdated` (with `--json`), `npm audit` (with `--json`, `--production`,
`--omit dev` — **no `--fix`**), `npm cache verify`, `npm doctor`, `npm fund`,
`npm prefix`.

**cargo** (`Mk8CargoCommands`): `cargo --version` only.

**Archive tools** (`Mk8ArchiveCommands`): create and list via ProcRun.
`tar -tf`, `tar -tvf` (verbose listing with sizes/dates), `tar -cf`,
`tar -czf`, `gzip`, `gunzip`, `zip`, `unzip -l`.
Safe extraction is available via the `ArchiveExtract` in-memory verb (see
Archive Extraction section above).

**Read-only tools** (`Mk8ReadOnlyToolCommands`): `cat`, `head -n`, `tail -n`,
`wc -l/-w/-c`, `sort`, `uniq`, `diff`, `sha256sum`, `md5sum`,
`base64`/`base64 -d`.  All accept ONLY SandboxPath arguments.

**Version checks** (`Mk8VersionCheckCommands`): `python3 --version`,
`ruby --version`, `perl --version`, `php --version`, `java --version`,
`javac --version`, `go version`, `rustc --version`, `swift --version`,
`cmake --version`, `gcc --version`, `g++ --version`, `clang --version`,
`docker --version`, `kubectl version --client`, `deno --version`,
`bun --version`, `terraform --version`.  No arguments, no file access.
`python3`, `ruby`, `perl`, `php`, and `cmake` use a version-check
exception to bypass the permanent binary block — only the exact
`--version` invocation is allowed.

**OpenSSL certificate inspection** (`Mk8OpensslCommands`): read-only
`x509 -in <SandboxPath> -noout` with `-text`, `-enddate`, `-subject`,
`-issuer`, `-serial`, or `-fingerprint -sha256`. Parses certificate files
already inside the sandbox — no network connection, no key generation, no
encryption/decryption. The `-noout` flag prevents binary output. No other
OpenSSL subcommand (`s_client`, `enc`, `genrsa`, `req`) is whitelisted.

**Tool existence checks** (`Mk8ToolCheckCommands`): `which <binary>` on
Linux/macOS, `where <binary>` on Windows. The binary argument is a
`Choice` slot restricted to binaries already in the whitelist (`dotnet`,
`git`, `node`, `npm`, `cargo`, `tar`, `openssl`, `cat`, `head`, `tail`,
etc.). Reveals nothing the agent couldn't discover by running the binary
itself — just avoids the cryptic "command not found" error on failure.

#### Defence-in-depth layers

1. **Permanently blocked binaries** (`Mk8BinaryAllowlist`): bash, sh, cmd,
   powershell, python, perl, ruby, curl, wget, find, sudo, chmod, etc.
   Cannot be overridden even with a template — **except** for exact
   version-check invocations (`python3 --version`, `ruby --version`,
   `perl --version`, `php --version`, `cmake --version`) which
   are carved out via `IsVersionCheckException`.
2. **Command-template whitelist** (`Mk8CommandWhitelist`): only registered
   templates can execute.  Unregistered flags, unknown binaries → rejected.
3. **Typed parameter slots**: every argument position has a slot type.
   No free text reaches any process argument.
4. **`.git/` write protection** (`Mk8PathSanitizer.ResolveForWrite`): blocks
   all writes to paths containing `.git/`, preventing hook injection and
   config tampering.
5. **`.gitattributes` / `.gitmodules` write block**: these filenames are in
   `BlockedWriteFilenames` to prevent filter driver redirection and
   submodule URL manipulation.
6. **Sandbox env GIGABLACKLIST** (`Mk8PathSanitizer.Resolve`): `mk8.shell.env`
   and `mk8.shell.signed.env` are blocked on ALL operations — read, write,
   copy, move, delete, hash, list. Enforced in `Resolve()` itself, not just
   `ResolveForWrite()`. Only the user or mk8.shell.startup may access them.

### Git

Git is available through the strict command-template whitelist:

- **Only registered templates execute.** `git config`, `git -c`, and all
  other unregistered subcommands/flags are rejected.
- **Commit messages are composed** from a vocabulary word list — no free text.
- **Branch names are pre-approved** — protected branches are excluded.
- **`.git/` internals are write-protected** — agents cannot create hooks,
  modify config, or inject objects.
- **Push/pull/merge are branch-restricted** — the same pre-approved branch
  word list applies, so agents cannot push to or merge into protected branches.
- **Clone URLs are env-whitelisted** — `git clone` only accepts URLs from
  `Mk8RuntimeConfig.GitCloneUrls`.

### HTTP

| Verb       | Args                          | Description        |
|------------|-------------------------------|--------------------|
| HttpGet    | `[url]`                       | GET request        |
| HttpPost   | `[url, body?]`                | POST request       |
| HttpPut    | `[url, body?]`                | PUT request        |
| HttpDelete | `[url]`                       | DELETE request      |

URL validation: scheme must be `https` (or `http` if opt-in). Host must not
resolve to a private/link-local IP. Port must be 80 or 443.

### Text / Data

| Verb        | Args                       | Description                   |
|-------------|----------------------------|-------------------------------|
| TextRegex   | `[input, pattern]`         | Match regex (2s timeout)      |
| TextReplace | `[input, old, new]`        | Simple string replacement     |
| JsonParse   | `[input]`                  | Validate and pretty-print     |
| JsonQuery   | `[input, jsonpath]`        | Extract value by JSONPath     |

### Extended Text Manipulation (Pure String Ops)

| Verb            | Args                     | Description                       |
|-----------------|--------------------------|-----------------------------------|
| TextSplit       | `[input, delimiter]`     | Split on delimiter, newline-separated output |
| TextJoin        | `[delimiter, parts...]`  | Join 1–32 parts with delimiter    |
| TextTrim        | `[input]`                | Trim leading/trailing whitespace  |
| TextLength      | `[input]`                | Character count                   |
| TextSubstring   | `[input, start, length?]`| Extract substring (0-based)       |
| TextLines       | `[input]`                | Line count + content              |
| TextToUpper     | `[input]`                | Uppercase (invariant)             |
| TextToLower     | `[input]`                | Lowercase (invariant)             |
| TextBase64Encode| `[input]`                | Base64-encode UTF-8 string        |
| TextBase64Decode| `[input]`                | Decode Base64 to UTF-8            |
| TextUrlEncode   | `[input]`                | URL-encode (`Uri.EscapeDataString`)  |
| TextUrlDecode   | `[input]`                | URL-decode (`Uri.UnescapeDataString`)|
| TextHtmlEncode  | `[input]`                | HTML-encode (`WebUtility.HtmlEncode`)|
| TextContains    | `[input, substring]`     | Returns `"True"`/`"False"`        |
| TextStartsWith  | `[input, value]`         | Returns `"True"`/`"False"`        |
| TextEndsWith    | `[input, value]`         | Returns `"True"`/`"False"`        |
| TextMatch       | `[input, pattern]`       | Regex match, returns `"True"`/`"False"` (2s timeout) |
| TextHash        | `[input, algorithm?]`    | Hash UTF-8 string (sha256/sha512/md5) |
| TextSort        | `[input, direction?]`  | Sort lines: asc (default), desc, numeric |
| TextUniq        | `[input]`              | Remove consecutive duplicate lines    |
| TextCount       | `[input, substring?]`  | Count occurrences, or lines/words/chars if no pattern |
| TextIndexOf     | `[input, substring]`   | First index of substring (-1 if not found) |
| TextLastIndexOf | `[input, substring]`   | Last index of substring (-1 if not found) |
| TextRemove      | `[input, old]`         | Remove all occurrences (sugar for `TextReplace [input, old, ""]`) |
| TextWordCount   | `[input]`              | Word count (whitespace-split)         |
| TextReverse     | `[input]`              | Reverse string                        |
| TextPadLeft     | `[input, width, char?]`| Left-pad to total width (default space, single printable) |
| TextPadRight    | `[input, width, char?]`| Right-pad to total width (default space, single printable) |
| TextRepeat      | `[input, count]`       | Repeat string N times (max 256, output capped) |
| JsonMerge       | `[json1, json2]`         | Shallow-merge JSON objects (second wins) |
| JsonKeys        | `[input]`                | Top-level keys from JSON object (newline-separated) |
| JsonCount       | `[input]`                | Element count from JSON array          |
| JsonType        | `[input]`                | Root JSON token type (object/array/string/number/boolean/null) |

All pure `.NET` string/encoding/crypto APIs — no file I/O, no process, no
network. These replace ProcRun equivalents (`wc`, `sort`, `base64`) by
operating on in-memory text from `$PREV`/`captureAs` variables.

### File Inspection (Read-Only, In-Memory)

| Verb          | Args                 | Description                              |
|---------------|----------------------|------------------------------------------|
| FileLineCount | `[path]`             | Line count via `File.ReadLines`          |
| FileHead      | `[path, lines?]`     | First N lines (default 10, max 1000)     |
| FileTail      | `[path, lines?]`     | Last N lines (default 10, max 1000)      |
| FileSearch    | `[path, literal]`    | Literal substring match — matching lines with numbers |
| FileDiff      | `[path1, path2]`     | Line-by-line diff of two files           |
| FileGlob      | `[path, pattern, depth?]` | Recursive file search by glob (max depth 10, max 1000 results) |

`FileSearch` is **literal** substring matching (not regex — `TextRegex`
exists for that). Returns matching lines with line numbers, capped at 500.
`FileDiff` returns added/removed/changed lines, capped at 500 differences.
All read-only, all paths validated against sandbox.

`FileGlob` recursively searches for files matching a glob pattern within
the sandbox. Depth defaults to 5, max 10. Results capped at 1000 files.
Safe replacement for the permanently-blocked `find` command — no `-exec`,
no predicates, just path matching.

### Directory Inspection (Read-Only)

| Verb         | Args              | Description                          |
|--------------|-------------------|--------------------------------------|
| DirFileCount | `[path, pattern?]`| Count files, optional glob pattern   |
| DirEmpty     | `[path]`          | Returns `"True"`/`"False"` — is directory empty? |

### File Type Detection (Read-Only, In-Memory)

| Verb          | Args     | Description                                      |
|---------------|----------|--------------------------------------------------|
| FileMimeType  | `[path]` | Detect file type via magic-byte header matching  |
| FileEncoding  | `[path]` | Detect file encoding via BOM + heuristics        |

`FileMimeType` reads the first bytes of a file and matches against known
magic-byte signatures. Returns a MIME type string (e.g. `"application/pdf"`,
`"image/png"`, `"text/plain"`). Read-only, in-memory, path must be in sandbox.

`FileEncoding` detects file encoding via BOM (Byte Order Mark) detection
and heuristic analysis. Returns encoding name (e.g. `"utf-8"`, `"utf-16-le"`,
`"ascii"`, `"utf-8-bom"`). Read-only, in-memory, path must be in sandbox.

### File Comparison (Read-Only)

| Verb         | Args                       | Description                               |
|--------------|----------------------------|-------------------------------------------|
| FileEqual    | `[path1, path2]`           | Byte-for-byte comparison → `"True"`/`"False"` |
| FileChecksum | `[path, expected, algo?]`  | Hash + compare → `"True"`/`"False"`       |

`FileEqual` performs streaming byte-for-byte comparison. Never loads both
files into memory. Both paths validated by sandbox. Returns `"False"` if
either file does not exist or sizes differ.

`FileChecksum` computes a hash of the file and compares it to the expected
hex string. Same algorithm support as `FileHash`: sha256, sha512, md5.
Case-insensitive comparison of hex strings.

### Path Manipulation (Pure String Ops — No I/O)

| Verb          | Args           | Description                               |
|---------------|----------------|-------------------------------------------|
| PathJoin      | `[parts...]`   | Join path segments (2–16 parts)           |
| PathDir       | `[path]`       | Directory portion                         |
| PathFile      | `[path]`       | Filename portion                          |
| PathExt       | `[path]`       | File extension (including dot)            |
| PathStem      | `[path]`       | Filename without extension                |
| PathChangeExt | `[path, ext]`  | Change file extension                     |

These use `System.IO.Path` methods that are **pure string operations** —
they never touch the filesystem. No `Exists()`, no disk access, no sandbox
escape possible. Results are validated by `Mk8PathSanitizer` if used in
subsequent file/directory verbs.

### Identity/Value Generation

| Verb         | Args         | Description                         |
|--------------|--------------|-------------------------------------|
| GuidNew      | `[]`         | New GUID via `Guid.NewGuid()`       |
| GuidNewShort | `[]`         | 8-char hex from GUID                |
| RandomInt    | `[min, max]` | Random integer in range (0–1000000) |

`GuidNew` returns a full GUID string. Useful for unique filenames,
correlation IDs, build tags. `GuidNewShort` returns an 8-character hex
string — shorter but still has ~4 billion possible values.

`RandomInt` generates a random integer in [min, max] (inclusive). Range
must be within 0–1000000. Validated at compile time. Uses
`Random.Shared.Next()`.

### Time Arithmetic (Pure DateTimeOffset Math)

| Verb       | Args                    | Description                        |
|------------|-------------------------|------------------------------------|
| TimeFormat | `[unixSec, format?]`    | Format Unix timestamp as string    |
| TimeParse  | `[dateString, format?]` | Parse date string to Unix seconds  |
| TimeAdd    | `[unixSec, seconds]`    | Add seconds to timestamp           |
| TimeDiff   | `[unixSec1, unixSec2]`  | Absolute difference in seconds     |

All pure arithmetic/string operations on `long` values. No I/O, no network.

`TimeFormat` uses the same format-string validation as `SysDateFormat`
(restricted charset, max 32 characters). Default format is ISO 8601 (`"o"`).

`TimeParse` accepts a date string and optional format. Returns Unix
seconds. Uses `DateTimeOffset.Parse` or `DateTimeOffset.ParseExact`.

### Version Comparison

| Verb           | Args         | Description                               |
|----------------|--------------|-------------------------------------------|
| VersionCompare | `[v1, v2]`   | Compare semver strings → `-1`/`0`/`1`    |
| VersionParse   | `[input]`    | Extract first version from string         |

`VersionCompare` extracts version numbers from both inputs and compares
via `System.Version.CompareTo`. Returns `-1` (v1 < v2), `0` (equal),
or `1` (v1 > v2). Handles 2, 3, or 4-part versions.

`VersionParse` extracts the first semver-like pattern (`\d+\.\d+(\.\d+)?`)
from arbitrary text. Useful for extracting versions from command output
like `"dotnet 9.0.100"` → `"9.0.100"`.

### Encoding/Conversion

| Verb        | Args                 | Description                     |
|-------------|----------------------|---------------------------------|
| HexEncode   | `[input]`            | UTF-8 string → hex string      |
| HexDecode   | `[hexString]`        | Hex string → UTF-8 string      |
| BaseConvert | `[value, from, to]`  | Convert between bases 2/8/10/16|

Pure encoding/conversion — no I/O. `BaseConvert` supported bases: 2
(binary), 8 (octal), 10 (decimal), 16 (hex).

### Regex Capture Groups

| Verb            | Args               | Description                        |
|-----------------|--------------------|------------------------------------|
| TextRegexGroups | `[input, pattern]` | Named/numbered groups as JSON      |

Returns a JSON object with group names/numbers as keys and matched values.
Same 2-second timeout as `TextRegex`/`TextMatch`. If no match, returns `{}`.

### Script Control/Debugging

| Verb   | Args                       | Description                           |
|--------|----------------------------|---------------------------------------|
| Echo   | `[message]`                | Returns message as-is (identity)      |
| Sleep  | `[seconds]`                | Pause 0.1–30 seconds                 |
| Assert | `[actual, expected, msg?]` | Fail step if values don't match       |
| Fail   | `[message]`                | Always fail with message              |

`Echo` is a pure identity function — returns its input as output. Useful
for debugging scripts, inserting markers in captured output, or composing
messages.

`Sleep` pauses execution. Capped at 30 seconds, minimum 0.1 seconds.
Validated at compile time. Useful between rate-limited HTTP calls.

`Assert` compares actual to expected (ordinal string comparison). If they
differ, the step fails with the provided message (or a default message).
Useful for verifying intermediate results before proceeding.

`Fail` always throws, causing the step to fail. Useful in conditional
branches (e.g. `If` + `Fail` for guard conditions).

### JSON Construction/Mutation (In-Memory)

| Verb          | Args                       | Description                        |
|---------------|----------------------------|------------------------------------|
| JsonFromPairs | `[k1, v1, k2, v2, ...]`   | Build JSON object from pairs       |
| JsonSet       | `[json, key, value]`       | Set/overwrite a key                |
| JsonRemoveKey | `[json, key]`              | Remove a key                       |
| JsonGet       | `[json, indexOrKey]`       | Get value by key or index          |
| JsonCompact   | `[json]`                   | Minify (remove whitespace)         |
| JsonStringify | `[value]`                  | Wrap as properly-escaped JSON string|
| JsonArrayFrom | `[items...]`               | Build JSON array from arguments    |

`JsonFromPairs` builds a JSON object from alternating key-value pairs.
Max 64 pairs (128 arguments). Values are always stored as JSON strings.

`JsonSet` sets or overwrites a key in a JSON object. Returns the modified
object. Useful for programmatically building configs without templates.

`JsonRemoveKey` removes a key from a JSON object. No error if key doesn't
exist.

`JsonGet` retrieves a value by key (object) or index (array). Simpler
than `JsonQuery` for single-level access. Returns the raw value for strings,
JSON text for objects/arrays.

`JsonCompact` minifies JSON by removing all whitespace/indentation. Useful
for embedding JSON in single-line contexts.

`JsonStringify` wraps a raw string value as a properly-escaped JSON string
(handles quotes, backslashes, control characters). Useful for embedding
captured output in JSON structures.

`JsonArrayFrom` builds a JSON array from arguments. Max 64 items. All
values stored as JSON strings.

### Environment (Read-Only)

| Verb   | Args     | Description                           |
|--------|----------|---------------------------------------|
| EnvGet | `[name]` | Read env var from allowlist only      |

Allowed: `HOME`, `USER`, `PATH`, `LANG`, `TZ`, `TERM`, `PWD`, `HOSTNAME`.
**Blocked:** anything containing `KEY`, `SECRET`, `TOKEN`, `PASSWORD`, `CONN`.

### System Info (Read-Only)

| Verb        | Args | Description              | Linux equivalent |
|-------------|------|--------------------------|------------------|
| SysWhoAmI   | `[]` | Current OS username      | `whoami`         |
| SysPwd      | `[]` | Current working directory| `pwd`            |
| SysHostname | `[]` | Machine hostname         | `hostname`       |
| SysUptime   | `[]` | System uptime            | `uptime`         |
| SysDate     | `[]` | Current UTC datetime     | `date -u`        |
| SysDiskUsage| `[path?]` | Disk space for drive containing path | `df -h <path>` |
| SysDirSize  | `[path]`  | Total size of directory (recursive)  | `du -sb <path>` |
| SysMemory   | `[]` | Process memory + GC heap | `free -m`        |
| SysProcessList | `[]` | List running processes (name + PID) | `ps aux`  |
| SysDateFormat  | `[format?]` | Formatted UTC date (restricted charset, max 32) | `date +"..."` |
| SysTimestamp   | `[]` | Unix epoch seconds (UTC)            | `date +%s`   |
| SysOsInfo      | `[]` | OS + arch + .NET runtime            | `uname -a`   |
| SysCpuCount    | `[]` | Processor count                     | `nproc`      |
| SysTempDir     | `[]` | Temp directory path (read-only)     | `echo $TMPDIR` |

`SysDiskUsage` reports total, available, and used space for the drive
containing the given path. If no path is provided, uses the sandbox root.
Executed in-memory via `System.IO.DriveInfo`.

`SysDirSize` recursively sums the size of all files under a directory.
Path must be in sandbox. Returns bytes and MB.

`SysMemory` reports the current process working set and GC heap size.
Executed in-memory via `System.Diagnostics.Process` and `System.GC`.

`SysProcessList` returns the first 200 running processes sorted by name,
each as `PID\tName`. Read-only, in-memory via
`System.Diagnostics.Process.GetProcesses()`.

`SysDateFormat` returns the current UTC date/time in a custom format.
The format string is validated at compile time: only date/time specifiers
(`y`, `M`, `d`, `H`, `m`, `s`, `f`, `z`, etc.), separators, and spaces
are allowed. Max 32 characters. Defaults to ISO 8601 (`"o"`) if omitted.

`SysTimestamp` returns Unix epoch seconds — useful for unique build IDs,
log filenames, cache keys.

`SysOsInfo` returns OS description, CPU architecture, and .NET runtime
version via `System.Runtime.InteropServices.RuntimeInformation`.

`SysCpuCount` returns `Environment.ProcessorCount`.

`SysTempDir` returns `Path.GetTempPath()` — the path string only, does
NOT create any files or directories.

### Clipboard (Write-Only)

| Verb         | Args        | Description              |
|--------------|-------------|--------------------------|
| ClipboardSet | `[content]` | Set OS clipboard text    |

`ClipboardSet` sets the OS clipboard text content. It does **not** read the
clipboard (prevents exfiltration of user-copied passwords or secrets).
Platform-dependent: uses `Set-Clipboard` on Windows, `pbcopy` on macOS,
`xclip`/`xsel` on Linux. May not be available on headless servers.

### Math (Safe Arithmetic)

| Verb     | Args           | Description                         |
|----------|----------------|-------------------------------------|
| MathEval | `[expression]` | Evaluate basic arithmetic expression|

`MathEval` evaluates a pure arithmetic expression: `+`, `-`, `*`, `/`, `%`,
`()`, decimal numbers, and spaces. **No variables, no functions, no string
eval.** The compiler rejects any character that is not a digit, decimal
point, operator, parenthesis, or space. Max 256 characters. Executed
in-memory via `System.Data.DataTable.Compute`.

### URL Validation

| Verb    | Args    | Description                                |
|---------|---------|---------------------------------------------|
| OpenUrl | `[url]` | Validate HTTPS URL, return as output string |

`OpenUrl` validates a URL using the same `Mk8UrlSanitizer` as HTTP verbs
(scheme must be `https` or `http`, no private IPs, no metadata endpoints,
port 80/443 only, no embedded credentials). mk8.shell does **NOT** launch
a browser — it returns the validated URL as output. The calling application
(CLI/API) decides whether to open it. Zero attack surface in mk8.shell.

### Network Diagnostics (In-Memory)

| Verb          | Args              | Description                  |
|---------------|-------------------|------------------------------|
| NetPing       | `[host, count?]`  | ICMP echo (1–10 pings)       |
| NetDns        | `[hostname]`      | DNS lookup (public IPs only) |
| NetTlsCert    | `[hostname, port?]`| TLS certificate inspection   |
| NetHttpStatus | `[url]`           | HTTP HEAD — status + headers |

`NetPing` sends ICMP echo requests via `System.Net.NetworkInformation.Ping`.
No process spawned. Host validated by `Mk8UrlSanitizer.ValidateHostname`:
IP literals blocked (must use hostname), private/metadata hosts blocked,
internal TLD suffixes (`.internal`, `.local`, `.corp`, `.lan`, `.intranet`,
`.private`) blocked. If the resolved address is private, the IP is hidden
from output. Count defaults to 1, max 10.

`NetDns` resolves a hostname via `System.Net.Dns.GetHostAddressesAsync`.
Same hostname validation as NetPing. **Private/reserved IPs are filtered
from output** — even if the DNS server returns `10.0.0.5`, the agent never
sees it. Prevents infrastructure probing.

`NetTlsCert` connects to a remote host via `System.Net.Security.SslStream`,
retrieves the TLS certificate, and returns subject, issuer, validity dates,
thumbprint, serial number, and Subject Alternative Names (SANs). Port
defaults to 443. Hostname validated by `Mk8UrlSanitizer.ValidateHostname`
(same SSRF protection as `NetPing`/`NetDns`). Read-only — connect, read
cert, disconnect. The certificate IS the server's public identity (designed
to be shared with every client), so no secrets are exposed. Reports days
until expiry with a warning if < 30 days.

`NetHttpStatus` sends an HTTP HEAD request and returns only the status code
and response headers — no body is downloaded. Lightweight health check.
Same SSRF URL validation as `HttpGet` (scheme, host, port restrictions).
Executed in-memory via `HttpClient`.

### TCP Port Check

| Verb          | Args                     | Description                     |
|---------------|--------------------------|---------------------------------|
| NetTcpConnect | `[host, port, timeout?]` | TCP connect test → Open/Closed  |

`NetTcpConnect` checks if a remote TCP port is reachable via
`System.Net.Sockets.TcpClient.ConnectAsync`. No data is sent — it only
tests whether the connection is established. Same SSRF hostname validation
as `NetPing` (no IP literals, no private/metadata hosts). Timeout defaults
to 5 seconds, max 30. Returns `"Open (Xms)"` or `"Closed (Xms)"`.

### HTTP Latency

| Verb        | Args              | Description                          |
|-------------|-------------------|--------------------------------------|
| HttpLatency | `[url, count?]`   | Timed HEAD requests → min/avg/max ms |

`HttpLatency` sends multiple timed HTTP HEAD requests and reports
min/avg/max round-trip times. Count defaults to 3, max 10. Same SSRF
URL validation as `HttpGet`. 200ms delay between requests. Returns a
detailed report with individual request times.

### File Age & Staleness (Read-Only)

| Verb          | Args               | Description                              |
|---------------|--------------------|------------------------------------------|
| FileAge       | `[path]`           | Seconds since last modification          |
| FileNewerThan | `[path, seconds]`  | `"True"`/`"False"` — modified within N s |

`FileAge` returns the number of seconds since the file was last written.
Pure `(UtcNow - LastWriteTimeUtc).TotalSeconds`. Throws if file does not
exist.

`FileNewerThan` checks if a file was modified within the last N seconds.
Returns `"False"` if the file does not exist (safe for use in `If`
predicates). Useful for staleness checks on health files, logs, or cache.

### Process Search (Read-Only)

| Verb        | Args     | Description                           |
|-------------|----------|---------------------------------------|
| ProcessFind | `[name]` | Find processes by name substring      |

`ProcessFind` filters `Process.GetProcesses()` by case-insensitive name
substring. Returns `PID\tName\tWorkingSetKB` per match, sorted by name.
Max 50 results. Read-only — no `Process.Kill()` or any mutation. If a
process's working set cannot be read (access denied), it shows
`"(access denied)"` instead.

### System Discovery (Read-Only)

| Verb         | Args | Description                              |
|--------------|------|------------------------------------------|
| SysDriveList | `[]` | All drives: name, type, total/free/used% |
| SysNetInfo   | `[]` | Network interfaces: name, status, IPs    |
| EnvList      | `[]` | All allowed env vars with values         |

`SysDriveList` enumerates all drives via `DriveInfo.GetDrives()`. Returns
name, drive type, filesystem format, total size, free space, and usage
percentage. Drives that are not ready show `"(not ready)"`.

`SysNetInfo` lists all non-loopback network interfaces via
`System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()`.
Returns interface name, operational status (Up/Down), type, and IPv4/IPv6
addresses. Link-local (`fe80:`) addresses are filtered. Private IPs are
**not** hidden here (unlike `NetDns`) because these are the agent's own
host interfaces — the agent already runs on this machine.

`EnvList` returns all environment variables from the allowlist with their
current values. Variables that are not set show `"(not set)"`. Same
allowlist as `EnvGet`.

### Regex File Search (Read-Only)

| Verb            | Args              | Description                        |
|-----------------|-------------------|------------------------------------|
| FileSearchRegex | `[path, pattern]` | Regex search — lines with numbers  |

Like `FileSearch` but with regex instead of literal matching. Same
2-second regex timeout as `TextRegex`/`TextMatch`. Returns
`lineNumber: matchedLine` format. Max 500 matches. Path must be in
sandbox.

### Tabular Text (Pure String Ops)

| Verb       | Args                      | Description                         |
|------------|---------------------------|-------------------------------------|
| TextColumn | `[input, index, delim?]`  | Extract column N from each line     |
| TextTable  | `[input, delimiter?]`     | Align columns for display           |

`TextColumn` splits each line by delimiter (default: whitespace), extracts
the column at the given 0-based index, and returns one value per line.
Column index validated at compile time (0–100).

`TextTable` aligns columns by padding each to its maximum width in the
dataset. Default delimiter is tab.

### Directory Comparison & Hashing (Read-Only)

| Verb       | Args                        | Description                        |
|------------|-----------------------------|------------------------------------|
| DirCompare | `[path1, path2]`           | Compare directory trees (names)    |
| DirHash    | `[path, algo?, pattern?]`  | Hash all files → manifest          |

`DirCompare` compares two directory trees **by filename only** (not
content). Returns three sections: only-in-first, only-in-second, and
common. Each section capped at 200 entries. Both paths validated by
sandbox.

`DirHash` walks a directory, hashes each file, and returns a manifest in
`sha256sum` format (`hash  relativePath`, one per line). Max 500 files.
Optional algorithm (sha256/sha512/md5) and glob pattern filter. Read-only.

### Human-Readable Formatting

| Verb           | Args        | Description                          |
|----------------|-------------|--------------------------------------|
| FormatBytes    | `[bytes]`   | `1048576` → `"1.00 MB"`             |
| FormatDuration | `[seconds]` | `3661` → `"1h 1m 1s"`               |

`FormatBytes` converts a byte count to the largest unit where the value
is ≥ 1. Units: B, KB, MB, GB, TB, PB. Two decimal places for KB+.

`FormatDuration` converts seconds to a human-readable duration. Format
adapts: `"2d 3h 15m 0s"`, `"1h 1m 1s"`, `"5.2s"`, `"150ms"`.

### System Log Viewing (Read-Only, Redacted)

| Verb           | Args                          | Description                            |
|----------------|-------------------------------|----------------------------------------|
| SysLogRead     | `[source, lines?, filter?]`   | Read system logs with secret redaction |
| SysLogSources  | `[]`                          | List available log sources             |

`SysLogRead` reads system/application logs from the host OS. On Windows,
reads from `System.Diagnostics.EventLog` (Application, System, Security).
On Linux, reads from `/var/log/syslog`, `/var/log/messages`, or
`/var/log/auth.log` (whichever exists). Args:

- `source`: `"application"`, `"system"`, `"security"` (Windows) or
  `"syslog"`, `"auth"`, `"kern"` (Linux). Case-insensitive.
- `lines`: max lines to return (default 50, max 500). Most recent first.
- `filter`: optional literal substring filter (same as `FileSearch`).

**All output is secret-redacted.** Lines matching the env blocklist
patterns (`KEY=`, `SECRET=`, `TOKEN=`, `PASSWORD=`, `CONN=`, `AUTH=`,
`PRIVATE=`, `ENCRYPT=`, `JWT=`, `CERTIFICATE=`, `APIKEY=`) have the
value portion replaced with `[REDACTED]`. This is defense-in-depth —
even though the agent runs on the same machine, redaction prevents
accidental persistence of secrets in captured output or sandbox files.

`SysLogSources` returns available log sources on the current OS.

### Service Status (Read-Only)

| Verb            | Args        | Description                            |
|-----------------|-------------|----------------------------------------|
| SysServiceList  | `[filter?]` | List OS services with status           |
| SysServiceStatus| `[name]`    | Detailed status of a specific service  |

On Windows, reads from `System.ServiceProcess.ServiceController`. On
Linux, reads from `/etc/init.d/` or parses `systemctl` output via
the .NET `Process` API (not ProcRun — internal only).

Returns service name, display name, status (Running/Stopped/etc.), and
start type (Automatic/Manual/Disabled). Filter is case-insensitive
name substring. Max 200 results.

### Archive Extraction (In-Memory, Pre-Validated)

| Verb           | Args                     | Description                    |
|----------------|--------------------------|--------------------------------|
| ArchiveExtract | `[archivePath, outputDir]` | Extract .zip or .tar.gz safely |

`ArchiveExtract` extracts archives using `System.IO.Compression` — no
external process. Supported formats: `.zip`, `.tar.gz`, `.tgz`.

**Pre-scan validation** (before ANY file is written to disk):

1. **Path traversal**: every entry's resolved path must be inside the output
   directory. Entries with `../` or absolute paths are rejected.
2. **Blocked extensions**: Tier 2 write-blocked extensions (`.dll`, `.exe`,
   `.js`, `.csproj`, etc.) are enforced per entry via `Mk8PathSanitizer.ResolveForWrite`.
3. **GIGABLACKLIST**: `mk8.shell.env` / `mk8.shell.signed.env` cannot appear
   in archives.
4. **Symlinks**: entries with Unix symlink attributes are rejected (no
   filesystem symlink is ever created).
5. **Zip bombs**: cumulative extracted size is capped at 256 MB.

If ANY entry fails validation, the entire extraction is aborted — nothing
is written to disk.

### mk8.shell Introspection (Read-Only, Compile-Time)

Diagnostic verbs that let agents inspect the mk8.shell runtime
configuration. All are resolved at **compile time** — the output is baked
into the compiled command, no runtime I/O or state access occurs. These
verbs exist so agents can discover their available tools, vocabularies, and
constraints without trial-and-error.

| Verb | Args | Description |
|---|---|---|
| `Mk8Blacklist` | `[]` | All effective gigablacklist patterns (compile-time + env-sourced), newline-separated |
| `Mk8Vocab` | `[listName]` | Words in a named vocabulary, sorted, newline-separated |
| `Mk8VocabList` | `[]` | All vocabulary names with word counts |
| `Mk8FreeText` | `[commandKey?]` | FreeText config: global summary (no args) or per-command status |
| `Mk8Env` | `[]` | All merged environment variables (global + sandbox). Secret-like values redacted |
| `Mk8Info` | `[]` | Runtime info: sandbox ID, workspace root, OS, .NET, architecture, CPU count, machine name |
| `Mk8Templates` | `[]` | All registered ProcRun command template descriptions |
| `Mk8Verbs` | `[]` | All available verb names (excluding control flow / batch verbs) |
| `Mk8Skills` | `[]` | Full skill reference (verb tables, slot types, examples, constraints) from embedded resource |
| `Mk8Docs` | `[]` | Full mk8.shell documentation (detailed spec, security model, design rationale) from embedded resource |

`Mk8Vocab` example: `{ "verb": "Mk8Vocab", "args": ["CommitWords"] }` returns
the full commit vocabulary, one word per line.

`Mk8FreeText` example: `{ "verb": "Mk8FreeText", "args": ["git commit"] }`
returns whether FreeText is enabled for `git commit -m`, the max length,
and the fallback word list.

`Mk8Env` redacts any variable whose key contains `KEY`, `SECRET`, `TOKEN`,
`PASSWORD`, `CREDENTIAL`, `CONN`, `PRIVATE`, `ENCRYPT`, `JWT`, `BEARER`,
`CERTIFICATE`, or `APIKEY`.

## Security Model

### What Cannot Be Expressed

The following are **structurally impossible** in mk8.shell:

- `sudo` / privilege escalation — no verb exists
- Arbitrary shell execution — no `bash -c` verb
- Pipe chains — scripts are sequential, no `|`
- Shell expansion — `$()`, backticks, glob in args are literal strings
- Background processes — no `&` or `nohup`
- Redirection — no `>`, `>>`, `2>&1` (file write is a dedicated verb)
- Chaining — no `&&`, `||`, `;` (operations are a typed array)

### Compilation Safety

The compiler **never** passes arguments through a shell. On all platforms:

```
ProcessStartInfo.UseShellExecute = false
ProcessStartInfo.ArgumentList.Add(arg)  // each arg individually
```

This means shell metacharacters in argument values (`; rm -rf /`) are treated
as literal text by the OS — they are never interpreted.

### Variable Safety

`$VARIABLE` shortcuts are resolved at **compile time** by simple string
replacement — they are not passed to the process environment. This means:

- `$WORKSPACE` in an argument becomes `/home/deploy/sandbox` before the
  process is ever spawned.
- Unknown variables are left as-is (e.g. `$UNKNOWN` stays literal) so the
  executor can report a clear error.
- Agents cannot define variables that override built-ins.
- Variable values are subject to the same per-verb sanitization as any other
  argument — a `$WORKSPACE` that resolves to a path still goes through
  `Mk8PathSanitizer`.
- **`$PREV` is blocked in ProcRun arguments.** The resolver throws a
  `Mk8CompileException` if `$PREV` appears in any ProcRun arg. This prevents
  the attack: `FileRead malicious.txt` → `$PREV` = file contents →
  `ProcRun dotnet $PREV`.
- **Named captures from process steps are also blocked in ProcRun arguments.**
  If `captureAs: "BUILD_OUT"` is set on a ProcRun step, `$BUILD_OUT`
  is blocked in all subsequent ProcRun args — same injection prevention.

### Write Protection (Two-Tier Model)

Write protection uses a **two-tier** model that distinguishes between files
the agent itself could execute vs. files only humans/external tools can run.

#### Tier 1: ALLOWED to write (interpreter-blocked scripts)

These script extensions are writable because **every interpreter that could
run them is permanently blocked** in the ProcRun allowlist:

`.sh`, `.bash`, `.zsh`, `.fish`, `.csh`, `.ksh` — shells blocked
`.bat`, `.cmd`, `.ps1`, `.psm1`, `.psd1` — cmd/powershell blocked
`.py`, `.rb`, `.pl`, `.lua`, `.php` — all interpreters blocked
`.service`, `.timer`, `.cron`, `.crontab` — systemctl/crontab blocked

Use case: agent writes a deployment script for a human operator, or an
admin-level `.sh` file that a CI pipeline picks up independently.

```
FileWrite  ["$WORKSPACE/deploy.sh", "#!/bin/bash\napt update && ..."]  → OK
FileWrite  ["$WORKSPACE/setup.py", "from setuptools import ..."]       → OK
```

The agent cannot execute these because `bash`, `sh`, `python`, `python3`,
`powershell`, and all other interpreters are permanently blocked.

#### Tier 2: BLOCKED from writing (executable by OS or allowed binaries)

| Category | Extensions / Names | Why |
|---|---|---|
| Native executables | `.exe`, `.com`, `.scr`, `.msi`, `.msp`, `.dll`, `.bin`, `.run`, `.elf`, `.so`, `.dylib`, `.appimage` | OS runs directly |
| Node.js code files | `.js`, `.mjs`, `.cjs` | `node` is on the allowlist |
| MSBuild project files | `.csproj`, `.fsproj`, `.vbproj`, `.proj`, `.targets`, `.props`, `.sln` | `dotnet build` executes `<Exec>` targets |
| Rust source files | `.rs` | `cargo build` executes `build.rs` scripts |
| Windows script host | `.jse`, `.wsf`, `.wsh`, `.msh`, `.vbs`, `.vbe` | OS can invoke via file association |
| Build config files (by name) | `Makefile`, `GNUmakefile`, `CMakeLists.txt`, `Dockerfile`, `.npmrc`, `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `nuget.config`, `package.json`, `build.rs`, `Cargo.toml`, `setup.py`, `setup.cfg`, `pyproject.toml`, `.gitattributes`, `.gitmodules`, `mk8.shell.env`, `mk8.shell.signed.env` | Build tools execute code from these implicitly / sandbox security |

##### Why these are blocked: the binary planting attack

The mk8.shell sandbox is a real directory on a real server. Other software
often shares that directory — CI runners, web servers, application runtimes,
container bind-mounts. If the agent could write native executables or
shared libraries, a **separate process** could load them without the agent
ever invoking them itself.

**Concrete scenario — DLL hijacking via shared build directory:**

```
/home/deploy/sandbox/           ← mk8.shell $WORKSPACE
├── app/
│   ├── MyApp.csproj
│   └── bin/Release/net10.0/
│       ├── MyApp.dll           ← legitimate app
│       └── Newtonsoft.Json.dll ← legitimate dependency
```

A CI pipeline (Jenkins, GitHub Actions self-hosted runner, cron job) runs
`dotnet MyApp.dll` in this directory on a schedule. If the agent could
write `.dll` files:

```
Step 1: FileWrite ["$WORKSPACE/app/bin/Release/net10.0/Newtonsoft.Json.dll",
                    "<trojanized assembly bytes>"]
        → Replaces the real dependency with a malicious one.

Step 2: (Agent does nothing — just waits.)

Step 3: CI runner executes: dotnet MyApp.dll
        → .NET runtime loads Newtonsoft.Json.dll from bin/
        → Trojanized code runs as the CI service account (often root or
          a deployment user with production credentials).
```

The agent never executed anything dangerous. It only wrote a file. The
damage happens when a completely separate process trusts the filesystem.

**Other variants of the same pattern:**

| Shared process | Agent writes | What happens |
|---|---|---|
| nginx with CGI directory in sandbox | `.exe` CGI binary | HTTP request triggers execution as `www-data` |
| Python app importing from sandbox | `.so` native extension | `import somelib` loads the `.so` at import time |
| Any Linux process with `LD_LIBRARY_PATH` including sandbox | `.so` named `libcurl.so.4` | Process loads it instead of the real library at startup |
| Docker host with bind-mount to sandbox | `.exe` in mounted volume | Another container or the host process runs it |
| Java app with classpath in sandbox | `.jar` file | JVM loads it, `static {}` initializer runs |
| Windows service monitoring a folder | `.dll` plugin | Service dynamically loads new DLLs from watched directory |

Tier 1 scripts (`.sh`, `.py`, `.ps1`) don't have this problem — no process
accidentally interprets a `.sh` file. Shell scripts require explicit
invocation (`bash script.sh`) or the execute bit (`chmod +x`, which is
permanently blocked). Native executables and shared libraries, on the
other hand, are loaded implicitly by runtimes, linkers, and OS loaders.

#### Tier 3: ProcRun code-file argument blocking

Even if a file extension is allowed for writing, ProcRun validates that
no argument points to a code file an allowed binary would interpret:

```
ProcRun ["node", "$WORKSPACE/script.js"]  → BLOCKED (code-file argument)
ProcRun ["node", "--version"]             → OK (no code file)
ProcRun ["make", "-f", "Makefile"]        → BLOCKED (dangerous config)
```

### Execution Limits

Execution options provide ceilings for retries and timeouts. Per-step
overrides can extend but not exceed script-level defaults.

## Control Flow

### ForEach (compile-time unroll)

ForEach is **not a runtime loop** — it's syntactic sugar that the expander
unrolls into N concrete operations before compilation. The agent cannot
observe intermediate results between iterations.

```json
{
  "verb": "ForEach",
  "forEach": {
    "items": ["src/a.txt", "src/b.txt", "src/c.txt"],
    "body": {
      "verb": "FileCopy",
      "args": ["$WORKSPACE/$ITEM", "$WORKSPACE/backup/$ITEM"]
    }
  }
}
```

Expands to 3 FileCopy operations at compile time. Available placeholders:
- `$ITEM` — the current item string
- `$INDEX` — 0-based iteration index

**Limits:**
- Max 256 items per ForEach (`Mk8ForEach.MaxExpansion`)
- Max 1024 total operations after expansion (`Mk8ScriptExpander.MaxExpandedOperations`)
- Max nesting depth: 3
- Nested ForEach is forbidden (no `ForEach` inside `ForEach.Body`)

### If (conditional guard)

If evaluates a predicate and either includes or skips the guarded operation.
No else branch, no boolean operators, no nesting beyond depth 3.

```json
{
  "verb": "If",
  "if": {
    "predicate": { "kind": "PrevContains", "args": ["Build succeeded"] },
    "then": { "verb": "FileWrite", "args": ["$WORKSPACE/status.txt", "build ok"] }
  }
}
```

**Available predicates:**

| Kind            | Args                  | Evaluated at | Description                                       |
|-----------------|-----------------------|--------------|---------------------------------------------------|
| PrevContains    | `[substring]`         | Compile time | True if `$PREV` contains substring                |
| PrevEmpty       | `[]`                  | Compile time | True if `$PREV` is empty/whitespace               |
| PrevStartsWith  | `[value]`             | Compile time | True if `$PREV` starts with value                 |
| PrevEndsWith    | `[value]`             | Compile time | True if `$PREV` ends with value                   |
| PrevEquals      | `[value]`             | Compile time | True if `$PREV` equals value exactly              |
| PrevMatch       | `[pattern]`           | Compile time | True if `$PREV` matches regex (2s timeout)        |
| PrevLineCount   | `[operator, count]`   | Compile time | True if `$PREV` line count satisfies op+count (eq/gt/lt/gte/lte) |
| CaptureEmpty    | `[name]`              | Compile time | True if named capture var is empty/whitespace     |
| CaptureContains | `[name, substring]`   | Compile time | True if named capture contains substring          |
| EnvEquals       | `[name, expected]`    | Compile time | True if env var equals value (allowlist)           |
| FileExists      | `[path]`              | Runtime      | Deferred — executor checks at step time           |
| DirExists       | `[path]`              | Runtime      | Deferred — executor checks at step time           |

All compile-time predicates are case-insensitive. `PrevMatch` uses a 2-second
regex timeout to prevent ReDoS. `PrevLineCount` operators: `eq`, `gt`, `lt`,
`gte`, `lte`. `CaptureEmpty`/`CaptureContains` check named capture variables
from `captureAs` — same injection-safe variable dictionary as `$PREV`.

## Batch Operations

Batch verbs reduce script verbosity when operating on multiple files.
They expand to individual operations at compile time — same validation
applies per file.

### FileWriteMany

```json
{
  "verb": "FileWriteMany",
  "args": [
    "$WORKSPACE/src/index.ts", "export {}",
    "$WORKSPACE/src/config.ts", "export const config = {}",
    "$WORKSPACE/README.md", "# My Project"
  ]
}
```

Args are pairs: `[path1, content1, path2, content2, ...]`. Max 64 files.
Each pair expands to a `FileWrite` — executable extension blocking applies.

### FileCopyMany

```json
{
  "verb": "FileCopyMany",
  "args": [
    "$WORKSPACE/template/a.txt", "$WORKSPACE/out/a.txt",
    "$WORKSPACE/template/b.txt", "$WORKSPACE/out/b.txt"
  ]
}
```

Args are pairs: `[src1, dst1, src2, dst2, ...]`. Max 64 pairs.

### FileDeleteMany

```json
{ "verb": "FileDeleteMany", "args": ["$WORKSPACE/tmp/a.log", "$WORKSPACE/tmp/b.log"] }
```

Args are paths. Max 64 paths.

## Script Composition (Include)

The `Include` verb references admin-approved script fragments by ID.
Fragments are resolved at expand time from a server-side registry
(`IMk8FragmentRegistry`) — agents cannot define fragments, only reference
them. This is compile-time inlining, not runtime function calls.

```json
{
  "operations": [
    { "verb": "Include", "args": ["setup-workspace"] },
    { "verb": "ProcRun", "args": ["dotnet", "build"] },
    { "verb": "Include", "args": ["deploy-to-staging"] }
  ]
}
```

**Rules:**
- Agents reference fragments by ID only (e.g. `"setup-workspace"`).
- Admins register fragments via `Mk8InMemoryFragmentRegistry.Register`.
- Fragment IDs: letters, digits, hyphens, underscores, periods (max 128 chars).
- Fragments cannot contain `Include` (no nested composition).
- Expansion depth counts toward the nesting limit (max 3).
- Total expanded operation cap (1024) still applies.
- Fragments go through the same compilation pipeline — same path
  validation, same verb restrictions, same security model.

## Scoped Path Builder

`Mk8ScopedPath` constructs workspace-relative paths that can **only descend**
into subdirectories. The agent never needs to know the absolute sandbox path.

```
Mk8ScopedPath.Join("src", "components", "App.tsx")
→ "$WORKSPACE/src/components/App.tsx"

Mk8ScopedPath.JoinCwd("output", "build.log")
→ "$CWD/output/build.log"

Mk8ScopedPath.Join("..", "..", "etc", "passwd")
→ THROWS Mk8CompileException
```

**Validation rules:**
- No `..` segments (throws immediately)
- No absolute path segments
- No null bytes, control characters, or Windows reserved names
- No trailing dots (Windows device name tricks)
- Forward slashes in a segment are split into sub-segments
- The resulting `$WORKSPACE/...` path is STILL validated by
  `Mk8PathSanitizer` after variable resolution — double defence

## Compilation Pipeline

```
Agent submits Mk8ShellScript
       │
       ▼
Mk8ScriptExpander.Expand()           ← unrolls ForEach/If/Batch/Include
       │                                (Include inlines from IMk8FragmentRegistry)
       │                                (cleanup operations expanded separately)
       ▼
Mk8VariableResolver.ValidateCaptures()← validates captureAs names, detects process captures
       │
       ▼
Mk8LabelValidator.Validate()         ← checks label uniqueness, forward-only jumps, DAG
       │
       ▼
Mk8VariableResolver.ResolveArgs()    ← resolves $WORKSPACE, $CWD, $USER, $CAPTURES
       │                                ($PREV + process captures blocked for ProcRun)
       ▼
Mk8PathSanitizer.Resolve()           ← validates paths in sandbox
Mk8PathSanitizer.ResolveForWrite()   ← + blocks executable extensions + project files
       │
       ▼
Mk8ShellCompiler.CompileOperation()  ← verb dispatch
       │
       ├─ File/Dir/HTTP/Text/Env/Sys  → InMemory marker (.NET APIs)
       ├─ Text*(extended)/Json*      → InMemory (TextHash: algorithm, SysDateFormat: charset)
       ├─ File inspection verbs      → InMemory (FileHead/Tail: line count bounds)
       ├─ FileTemplate/FilePatch     → InMemory (template/patch validation)
       ├─ FileHash/DirTree           → InMemory (algorithm/depth validation)
       ├─ ClipboardSet/MathEval      → InMemory (MathEval: character validation)
       ├─ OpenUrl                    → InMemory (SSRF URL validation)
       ├─ NetPing/NetDns             → InMemory (hostname + SSRF validation)
       ├─ NetTlsCert/NetHttpStatus   → InMemory (hostname/URL + SSRF validation)
       ├─ ArchiveExtract             → InMemory (extension + path validation)
       └─ ProcRun                    → Mk8BinaryAllowlist + ValidateArgs
       │
       ▼
Mk8CompiledScript
├── Commands          (flat list of Mk8CompiledCommand)
├── CleanupCommands   (compiled cleanup ops, if StopAndCleanup)
└── LabelIndex        (label → step index, if labels used)
```

## Audit Log

Every compiled script can produce a complete audit trail via
`Mk8AuditLog.CreateEntries()`. Each step records what the agent
requested, what was compiled, and what actually ran:

```
Mk8ShellAuditEntry
├── JobId                              unique job identifier
├── StepIndex                          0-based step position
├── RequestedVerb + RequestedArgs      pre-compilation (agent's input)
├── CompiledExecutable + CompiledArgs  post-compilation (resolved paths, validated args)
├── ExitCode                           process exit code or 0 for in-memory ops
├── Output / Error                     captured stdout/stderr (truncated to limits)
├── StartedAt / CompletedAt            UTC timestamps
├── Attempts                           retry count (1 = no retries)
└── SandboxRoot                        workspace boundary at execution time
```

This enables:
- **Post-incident analysis:** "the agent wrote `deploy.sh` with these
  contents at 14:23, then committed it at 14:24."
- **Rate limiting:** detecting agents that issue suspiciously many write
  operations.
- **Compliance:** full traceability from agent request → compiled command →
  execution result.

## Future Considerations

The following features have been evaluated but carry residual risk that
cannot be fully mitigated within mk8.shell's security model.

| Feature | Risk | Why it stays out |
|---|---|---|
| `traceroute` | Reveals internal network hops, router IPs, topology | The useful output IS the internal hops — any implementation that strips them isn't useful. `NetPing` covers the "is it reachable / how fast" use case. |
| `ClipboardGet` (read clipboard) | Exfiltration of user-copied passwords/secrets | Would need user consent prompt or clipboard content filtering — not feasible in headless agent context. |
