# SharpClaw Core CLI Reference

The SharpClaw CLI is an interactive REPL that runs alongside the API server
inside the same process. You interact with it on the terminal where you
launched the backend. It dispatches directly to the same handler methods
that back the REST API — there is no HTTP round-trip.

---

## Table of Contents

- [Starting the CLI](#starting-the-cli)
- [Session model](#session-model)
- [ID system](#id-system)
- [Argument parsing](#argument-parsing)
- [Auth](#auth)
- [Provider](#provider)
- [Model](#model)
- [Agent](#agent)
- [Context](#context)
- [Channel](#channel)
- [Thread](#thread)
- [Chat](#chat)
- [Job](#job)
- [Task](#task)
- [Role](#role)
- [User](#user)
- [Bio](#bio)
- [Tool awareness sets](#tool-awareness-sets)
- [Resource](#resource)
- [Module](#module)
- [Env](#env)
- [DB](#db)
- [Health](#health)
- [Module-provided CLI commands](#module-provided-cli-commands)

---

## Starting the CLI

When you run the SharpClaw backend directly (not in headless mode), the REPL
starts automatically after the API server is ready:

```
Type 'help' for available commands, 'exit' to quit.
Log in with: login <username> <password>
```

If stdin is redirected — for example when the backend is launched detached
by a client or run as a service — the REPL is skipped automatically. The
server runs normally and waits for Ctrl+C or host shutdown.

Type `help` at any time for a compact command reference. Type `exit` or
`quit` to shut down the backend.

---

## Session model

Four pieces of state persist across commands within a single run:

| State | Set by | Cleared by | Used implicitly by |
|---|---|---|---|
| Current user / user ID | `login`, `register` | `logout` | All non-public commands |
| Current channel | `channel add`, `channel select` | `channel delete` (if active), `logout` | `chat`, `thread list/add`, `job list`, `task start` |
| Current thread | `thread select` | `thread deselect`, `thread delete` (if active), `channel select` | `chat` |
| Chat mode | `chat toggle` | `!exit`, `!chat toggle` | All input |

Every command runs in a fresh DI scope. The session user ID is injected into
`SessionService` at the start of each command so the same authorisation rules
that apply to API requests apply here.

---

## ID system

Entities are referenced by either a **short ID** or a **full GUID**.

Short IDs (`#1`, `#2`, …) are assigned the first time a GUID appears in
any output during a session. They are ephemeral — they reset when the
process restarts. The `#` prefix is optional:

```
provider get #3
provider get 3
provider get 550e8400-e29b-41d4-a716-446655440000
```

All three forms are accepted wherever an entity ID is expected.

If you reference a short ID that has not been assigned yet (because you
have not listed or displayed the entity), you will get:

```
Unknown short ID #7. Use 'list' to see available IDs.
```

Every piece of JSON output printed to the terminal includes a `"#"` field
before each `"Id"` GUID, so you can always read off the short ID immediately
after a `list` or `get`.

---

## Argument parsing

Arguments are space-separated. Use double quotes to include spaces inside a
single argument:

```
agent add "My Agent" #3 "You are a helpful assistant" --max-tokens 1024
```

Quoted strings are stripped of their outer quotes and treated as one token.

---

## Auth

```
register <username> <password>
```
Registers a new user and immediately logs in as that user.

```
login <username> <password> [--remember]
```
Logs in. `--remember` issues a refresh token. On success the prompt changes
to `sharpclaw (<username>)>`.

```
logout
```
Clears the current session. Returns the prompt to `sharpclaw>`.

```
me
```
Displays the current user's profile and role.

All commands other than `register`, `login`, `help`, and their aliases
require a logged-in session.

---

## Provider

```
provider add <name> <type> [endpoint]
```
Creates a provider. `type` is case-insensitive. `endpoint` is required for
`Custom` only.

Valid types: `OpenAI`, `Anthropic`, `OpenRouter`, `GoogleVertexAI`,
`GoogleVertexAIOpenAi`, `GoogleGemini`, `GoogleGeminiOpenAi`, `ZAI`,
`VercelAIGateway`, `XAI`, `Groq`, `Cerebras`, `Mistral`, `GitHubCopilot`,
`Minimax`, `Custom`.

```
provider get <id>
provider list
provider update <id> <name> [endpoint]
provider delete <id>
```

```
provider set-key <id> <apiKey>
```
Encrypts and stores the API key for this provider.

```
provider login <id>
```
Starts an OAuth device-code flow. The CLI prints a URL and user code, then
waits for authorisation in your browser.

```
provider sync-models <id>
```
Fetches the provider's model list and imports any that are not yet
registered. Also refreshes model capability flags.

```
provider refresh-caps <id>
```
Re-infers capability flags for all models belonging to this provider without
fetching the model list again.

```
provider cost <id> [--days <n>]
```
Shows token usage and cost for this provider. Default window: 30 days.

```
provider cost-total [--days <n>] [--simple] [--all]
```
Shows a total cost summary across all providers. `--simple` prints a one-line
summary. `--all` includes providers with zero cost.

---

## Model

```
model add <name> <providerId> [--cap <capabilities>]
```
Registers a model. `<name>` must be the **exact provider model ID** (e.g.
`gpt-4o`, `claude-sonnet-4-20250514`, `gemini-2.5-flash`). Use
`provider sync-models` to auto-import instead of adding manually.

Capability flags (comma-separated, case-insensitive):
`Chat`, `Transcription`, `ImageGeneration`, `Embedding`, `TextToSpeech`.
Default: `Chat`.

```
model get <id>
model list [--provider <id>]
model update <id> <name> [--cap <capabilities>]
model delete <id>
```

### Local models

```
model download <url> [--name <alias>] [--quant <Q4_K_M>]
               [--gpu-layers <n>] [--provider <LlamaSharp|Whisper>]
```
Downloads a GGUF model file and registers it. Omitting `--provider` registers
with **both** LlamaSharp and Whisper. `--gpu-layers` has no effect for Whisper.

```
model download list <url>
```
Lists available GGUF files at the given URL without downloading.

```
model load <id> [--gpu-layers <n>] [--ctx <size>] [--mmproj <path>]
```
Pins a model in memory so it stays loaded between requests. Models
auto-load on first use and auto-unload when idle; use `load` to keep a
frequently-used model resident.

```
model unload <id>
```
Unpins the model. Stops immediately if there are no active requests.

```
model mmproj <id> <path|none>
```
Sets or clears the CLIP / mmproj file path for a LlamaSharp vision model.
Pass `none` to clear.

```
model local list
```
Lists all downloaded local model files with their status.

---

## Agent

```
agent add <name> <modelId> [system prompt] [flags...]
```
Creates an agent. The system prompt is any positional text after the model
ID that is not a recognised flag. If the model ID resolves to a local model
file rather than a model, the CLI auto-resolves it to the parent model.

```
agent get <id>
agent list
agent update <id> <name> [modelId] [system prompt] [flags...]
agent delete <id>
```

For `update`, an optional positional after the name is tested as a model ID;
if it resolves, it is used as the new model. Any remaining positional text
becomes the new system prompt.

```
agent role <id> <roleId>
agent role <id> none
```
Assigns or removes the agent's role.

```
agent sync-with-models
```
Creates a `default-<model>` agent for every chat-capable model that does not
already have a default agent.

### Inference flags (add / update)

| Flag | Type | Effect |
|---|---|---|
| `--max-tokens <n>` | integer | Cap on tokens generated per response |
| `--temperature <f>` / `--temp <f>` | float | Sampling temperature |
| `--top-p <f>` | float | Nucleus sampling |
| `--top-k <n>` | integer | Top-k sampling |
| `--frequency-penalty <f>` | float | Frequency penalty |
| `--presence-penalty <f>` | float | Presence penalty |
| `--stop <s1,s2>` | comma-separated | Stop sequences |
| `--seed <n>` | integer | Deterministic seed |
| `--response-format <json>` | JSON string | Structured output format |
| `--reasoning-effort <s>` | string | Reasoning effort hint (e.g. `low`, `high`) |
| `--params <json>` | JSON object | Raw provider-specific parameter overrides |
| `--header <template>` | string | Custom chat header template |
| `--tools <setId>` | ID | Tool awareness set to assign |
| `--no-tools` | flag | Disable all tool schemas for this agent |

---

## Context

Contexts group channels under a shared default agent and permission policy.
`context` and `ctx` are interchangeable.

```
context add <agentId> [name]
context get <id>
context list [agentId]
context update <id> <name>
context delete <id>
```

```
context agents <id>
context agents <id> add <agentId>
context agents <id> remove <agentId>
```
Lists, adds, or removes an allowed agent for this context.

```
context defaults <id>
context defaults <id> set <key> <resourceId>
context defaults <id> clear <key>
```
Views or modifies default resources. Use `all` as the resource ID to set a
wildcard grant. Valid keys:

`safeshell`, `dangshell` / `dangerousshell`, `container`, `website`,
`search` / `searchengine`, `internaldb`, `externaldb`,
`inputaudio` / `audio`, `displaydevice` / `display`, `agent`, `task`,
`skill`, `transcriptionmodel` / `model`, `editorsession` / `editor`.

---

## Channel

Channels are conversations. They inherit their default agent, permission
policy, allowed agents, and default resources from their context when not
set directly. `channel` and `chan` are interchangeable.

```
channel add [--agent <id>] [--context <id>] [--header <template>]
            [--tools <setId>] [--no-tools] [title]
```
Either `--agent` or `--context` is required. The newly created channel is
automatically selected as the active channel.

```
channel get <id>
channel list [agentId]
channel update <id> ...
channel delete <id>
```

```
channel select <id>
```
Sets the active channel for `chat`, `thread`, and `job` commands.
Deselects any active thread.

```
channel cost <id>
```
Shows token usage and cost broken down by agent for this channel.

```
channel attach <id> <contextId>
channel detach <id>
```
Attaches or detaches the channel from a context.

```
channel agents <id>
channel agents <id> add <agentId>
channel agents <id> remove <agentId>
```

```
channel defaults <id>
channel defaults <id> set <key> <resourceId>
channel defaults <id> clear <key>
```
Same keys as `context defaults`. Channel defaults override context defaults
for this channel only. Other channels in the same context are unaffected.

---

## Thread

Threads provide persistent conversation history within a channel. Messages
sent inside a thread include prior history up to the thread's limits. Messages
sent to a channel without a thread are one-shot (no history).

```
thread add [channelId] [name] [--max-messages <n>] [--max-chars <n>]
```
Uses the active channel if `channelId` is omitted. System defaults:
50 messages, 100 000 characters. Oldest messages are trimmed first when
limits are reached.

```
thread get <id>
thread list [channelId]
thread cost <id>
thread update <id> [--name <name>] [--max-messages <n>] [--max-chars <n>]
thread delete <id>
```
Set `--max-messages` or `--max-chars` to `0` to reset to the system default.

```
thread select <id>
```
Sets the active thread. All subsequent `chat` commands will include this
thread's history automatically.

```
thread deselect
```
Removes the active thread selection. Chat reverts to one-shot mode.

---

## Chat

```
chat [--agent <id>] [--thread <id>] <message>
```
Sends a message in the active channel. `--agent` overrides the channel's
default agent for this message only. `--thread` routes the message through a
thread with history.

If no channel is selected, the most recently active channel is auto-selected
with a notice.

**Thread inference:** If the first positional argument looks like a short ID
or GUID, the CLI checks whether it resolves to a thread (in which case the
channel is inferred from it) or to a channel (in which case that channel
becomes active). This lets you write:

```
chat #5 What is the server status?
```

without `--thread`.

### Streaming output

Responses stream to the terminal as they arrive:

```
[tool] #N actionKey → Executing
[result] #N → Completed
[approval] Job #N (actionKey) requires approval. Approve? (y/n):
[approval] → Approved
[error] description
```

Text deltas are written character by character. A newline is emitted after
the final delta.

### Chat mode

```
chat toggle
```
Toggles **chat mode**. In chat mode, every line of input is sent as a chat
message — you do not type `chat` before each one. The prompt changes to
`sharpclaw (<user>) 💬>`.

Escape from chat mode:
```
!exit
!chat toggle
```

---

## Job

Jobs are single tool-call executions submitted to a channel's agent.

```
job submit <channelId> <actionKey> [resourceId]
           [--agent <id>] [--model <id>] [--lang <code>]
           [--mode <sliding|step|window>]
           [--window <seconds>] [--step <seconds>]
```
`actionKey` is a module tool name (e.g. `execute_as_safe_shell`,
`manage_agent`, `cu_click_desktop`, `tr_transcribe_audio_device`). Valid
action keys are dynamic — see `module list` or `GET /modules`.

When `resourceId` is omitted, default resources are resolved from the
channel's `DefaultResourceSet` → context's → permission-set defaults.

The `--mode`, `--window`, and `--step` flags are specific to transcription
jobs. `--model` overrides the transcription model for transcription actions.

```
job list [channelId]
```
Uses the active channel if `channelId` is omitted.

```
job status <jobId>
job approve <jobId>
job cancel <jobId>
job pause <jobId>
job resume <jobId>
job stop <jobId>
```
`stop` gracefully completes a running job (also accepts `Paused` jobs).
`cancel` aborts immediately.

```
job listen <jobId>
```
Streams live transcription segments to the terminal. Press Ctrl+C to stop
listening without cancelling the job.

---

## Task

Tasks are user-defined C# scripts compiled and run as managed background
processes. There are two layers: **definitions** (the compiled source) and
**instances** (running executions).

### Definitions

```
task create <sourceFilePath>
```
Reads and validates the `.cs` file. Diagnostics (line, column, severity,
message) are printed before upload if validation fails.

```
task list
task get <id>
task update <id> <sourceFilePath>
task activate <id>
task deactivate <id>
task delete <id>
```

### Instances

```
task start <taskId> [channelId] [--param key=value ...]
task run <taskId> [channelId] [--param key=value ...]
```
`run` is an alias for `start`. Uses the active channel if `channelId` is
omitted. Multiple `--param` flags may be supplied.

```
task create-queued <taskId> [channelId] [--param key=value ...]
```
Creates a queued instance without starting it immediately.

```
task start-instance <instanceId>
```
Starts an existing queued instance.

```
task instances <taskId>
task instance <instanceId>
task outputs <instanceId> [--since <timestamp>]
task cancel <instanceId>
task stop <instanceId>
task pause <instanceId>
task resume <instanceId>
```

```
task listen <instanceId>
```
Streams live task output events to the terminal. Event types: `[output]`,
`[log]`, `[status]`, `[done]`. Press Ctrl+C to stop listening without
stopping the instance.

---

## Role

```
role list
role get <id>
role permissions <id>
role permissions <id> set [flags...]
```

The `set` command does a **full replacement** of the role's permissions.
All flags are additive within a single `set` invocation.

### Global capability flags

| Flag | Grants |
|---|---|
| `--create-sub-agents` | CanCreateSubAgents |
| `--create-containers` | CanCreateContainers |
| `--register-databases` | CanRegisterDatabases |
| `--localhost-browser` | CanAccessLocalhostInBrowser |
| `--localhost-cli` | CanAccessLocalhostCli |
| `--click-desktop` | CanClickDesktop |
| `--type-on-desktop` | CanTypeOnDesktop |
| `--read-cross-thread-history` | CanReadCrossThreadHistory |
| `--edit-agent-header` | CanEditAgentHeader |
| `--edit-channel-header` | CanEditChannelHeader |
| `--create-document-sessions` | CanCreateDocumentSessions |
| `--enumerate-windows` | CanEnumerateWindows |
| `--focus-window` | CanFocusWindow |
| `--close-window` | CanCloseWindow |
| `--resize-window` | CanResizeWindow |
| `--send-hotkey` | CanSendHotkey |
| `--read-clipboard` | CanReadClipboard |
| `--write-clipboard` | CanWriteClipboard |

By default all global flags default to `Independent` clearance. Append
`:<clearance>` to specify a different level.

For arbitrary flag names use:
```
--flag <FlagKey>[:<clearance>]
```

### Resource grant flags

Each flag accepts `<id>[:<clearance>]`. Use `all` as the ID for a wildcard
grant (all resources of that type).

`--dangerous-shell`, `--safe-shell`, `--container`, `--website`,
`--search-engine`, `--internal-db`, `--external-db`, `--input-audio`,
`--agent`, `--task`, `--skill`

Clearance values (ascending): `Unset`, `ApprovedBySameLevelUser`,
`ApprovedByWhitelistedUser`, `ApprovedByPermittedAgent`,
`ApprovedByWhitelistedAgent`, `Independent`.

**Example:**
```
role permissions #2 set --create-sub-agents --safe-shell all:Independent
```

---

## User

These commands require admin privileges.

```
user list
```
Lists all registered users.

```
user role <userId> <roleId>
user role <userId> none
```
Assigns or removes a user's role.

---

## Bio

```
bio get
bio set <text>
bio clear
```
Reads, sets, or clears the current user's bio. The bio is included in the
chat header so agents know something about the person they are talking to.

---

## Tool awareness sets

Tool awareness sets control which tool-call schemas are sent to the model in
each API request. Excluding unused tools reduces prompt-token overhead.

```
tools add <name> [json]
```
Creates a set. The optional `json` argument is a JSON object mapping tool
names to `true` or `false`. Omitted tools default to enabled.

```
tools list
tools get <id>
tools update <id> [--name <name>] [json]
tools delete <id>
```

Assign a set to an agent or channel via `--tools <setId>` on `add` or
`update`. Override chain: channel → agent → null (all tools enabled).

Tool names are dynamic — they depend on which modules are enabled. Use
`module list` to see all registered tool names.

---

## Resource

```
resource <type> <command> [args...]
```
All resource types are module-provided. The set of valid types depends on
which modules are currently enabled. See `module list` or `help` for the
full list at runtime.

Commands supported by all types: `add`, `get`, `list`, `update`, `delete`,
`sync` (where supported).

The `sync` sub-command imports from the system or an external registry (for
example, `resource inputaudio sync` imports WASAPI devices from Windows).

---

## Module

```
module list
module get <id>
module enable <id>
module disable <id>
module scan
module reload <id>
module unload <id>
```

Module IDs are string identifiers (e.g. `sharpclaw_transcription`), not
GUIDs. Short IDs do not apply here.

`scan` discovers and loads external module assemblies from the modules
directory without restarting. `reload` unloads and re-loads a single
external module. `disable` is rejected (409) if another enabled module
depends on a contract this module exports.

---

## Env

Manages the core `.env` file (`Infrastructure/Environment/.env`). This
file controls encryption keys, JWT secrets, database connection strings,
and other server-side configuration. Changes require a backend restart.

```
env get
```
Reads and prints the raw JSON content of the core `.env` file.

```
env set
```
Writes the core `.env` file. Paste JSON content into stdin; enter a blank
line to finish.

```
env auth
```
Checks whether the current user is authorised to edit the core `.env`.
Returns `authorised: true/false`. The real enforcement is server-side;
this is a pre-flight check only.

```
env status
```
Reports whether the `.env` file is encrypted (AES-GCM) or plaintext on disk.

```
env unlock
```
Decrypts the `.env` file in-place. The file will be re-encrypted on the
next backend startup. Use this when you need to inspect or hand-edit the
file outside the CLI.

---

## DB

Available only when a relational EF Core provider is configured (Postgres,
SQL Server, or SQLite). Not available for the default JSON file provider.

```
db status
```
Shows the migration gate state (`Idle`, `Draining`, or `Migrating`) and
lists applied and pending migration names.

```
db migrate
```
Drains in-flight requests and applies all pending EF Core migrations. All
new requests are held while the migration runs. Migrations are **never**
applied automatically — you must trigger this explicitly.

---

## Health

```
health
```
Runs the JSON persistence health check and prints the status of all
monitored components:

| Icon | Status |
|---|---|
| ✓ | Healthy |
| ⚠ | Degraded |
| ✗ | Unhealthy |

Checks include: disk writability, pending transactions, quarantine count,
flush backlog, checksums, event log integrity, snapshot age, and unclean
shutdown sentinel.

---

## Module-provided CLI commands

Modules may register CLI commands in two scopes:

- **Top-level:** A new root command name, dispatched by `TryHandleAsync`
  after the built-in switch fails.
- **Resource type:** A new sub-command under `resource <type>`, dispatched
  by `resource`.

The `help` command lists all currently registered module commands at the
bottom of its output, with their scope prefix, description, aliases, and
originating module ID. The Transcription module, for example, registers
`resource inputaudio` (alias `ia`).

See the individual module documentation in `docs/modules/` for the CLI
commands each module provides.
