# SharpClaw Tasks

> **Full API reference:** [Core API — Task endpoints](#rest-api-reference)
> **Agent skill file:** [Tasks-skill.md](Tasks-skill.md)
> **Creation guide:** [guides/Task-Creation-Guide.md](guides/Task-Creation-Guide.md)
> **Agent creation skill:** [guides/Task-Creation-skill.md](guides/Task-Creation-skill.md)

Tasks are user-defined automation scripts that run as managed background processes
inside SharpClaw. A task can orchestrate agent conversations, make HTTP calls, perform
structured data transforms, drive live transcription sessions, loop indefinitely as a
daemon, or execute a one-shot pipeline — all without touching application code. Every
task is written in a restricted subset of C# and registered through the API or CLI at
runtime.

---

## Table of contents

- [Concepts](#concepts)
- [Writing a task script](#writing-a-task-script)
  - [Minimal example](#minimal-example)
  - [Attributes](#attributes)
  - [Requirements and preflight attributes](#requirements-and-preflight-attributes)
  - [Trigger attributes](#trigger-attributes)
  - [Parameters](#parameters)
  - [Data types and the output type](#data-types-and-the-output-type)
  - [The entry point](#the-entry-point)
  - [Allowed language constructs](#allowed-language-constructs)
  - [Step reference](#step-reference)
  - [Tool-call methods](#tool-call-methods-toolcall)
  - [AgentOutput format hints](#agentoutput-format-hints)
- [Validation diagnostic codes](#validation-diagnostic-codes)
- [Compilation diagnostic codes](#compilation-diagnostic-codes)
- [Preflight and requirement checks](#preflight-and-requirement-checks)
- [Execution lifecycle](#execution-lifecycle)
- [Permissions](#permissions)
- [Agent tool exposure](#agent-tool-exposure)
- [Scheduling](#scheduling)
- [Trigger system](#trigger-system)
- [CLI reference](#cli-reference)
- [REST API reference](#rest-api-reference)
  - [Task definitions](#task-definitions)
  - [Task instances](#task-instances)
  - [Task trigger and shortcut endpoints](#task-trigger-and-shortcut-endpoints)
  - [SSE streaming](#sse-streaming)
  - [Response shapes](#response-shapes)
- [Module authoring — extending the task step system](#module-authoring--extending-the-task-step-system)
  - [Step keys](#step-keys)
  - [TaskStepRegistry](#taskstepregistry)
  - [TaskStepDescriptor](#taskstepdescriptor)
  - [Implementing ITaskParserModuleExtension](#implementing-itaskparsermoduleextension)
  - [Implementing ITaskStepExecutorExtension](#implementing-itaskstepexecutorextension)
  - [ITaskStepExecutionContext](#itaskstepexecutioncontext)
  - [Event trigger extensions](#event-trigger-extensions)
  - [Module step checklist](#module-step-checklist)
- [Practical examples](#practical-examples)

---

## Concepts

| Term | Meaning |
|------|---------|
| **Task definition** | A registered C# script. Stored in the database; has a name, optional description, parameter declarations, and source text. |
| **Task instance** | A single execution of a definition. Created by `POST /tasks/{id}/instances`. Has its own status, logs, and output snapshot. |
| **Task orchestrator** | The server component that interprets the compiled execution plan step by step, managing pause/resume gates and the output channel. |
| **Task runtime host** | A per-instance singleton that owns the `CancellationToken`, pause gates, and the SSE output channel. |
| **Compiled plan** | The artefact produced by the compiler from a validated definition plus resolved parameter values. Passed directly to the orchestrator on instance start. |

The pipeline for every execution is:

```
register source  →  parse  →  validate  →  store definition
submit instance  →  compile (resolve params)  →  orchestrate
```

Parsing and validation happen when the definition is registered. Compilation
(parameter resolution) happens when an instance starts, because parameter values
are supplied at launch time.

---

## Writing a task script

### Minimal example

```csharp
[Task("greet")]
[Description("Sends a greeting to an agent and returns the response.")]
public class GreetTask
{
    public string AgentName { get; set; } = "Assistant";
    public string Message   { get; set; } = "Hello!";

    public async Task RunAsync(CancellationToken ct)
    {
        var agent    = await FindAgent(AgentName);
        var response = await Chat(agent, Message);
        await Log(response);
    }
}
```

The class name is arbitrary; the canonical task name comes from `[Task(...)]`.

---

### Attributes

| Attribute | Target | Required | Purpose |
|-----------|--------|----------|---------|
| `[Task("name")]` | Class | **Yes** | Registers the task under the given name. The name becomes the key used by the API and by agents invoking the task as a tool. |
| `[Description("...")]` | Class | No | Human-readable description stored in the definition and surfaced in agent tool schemas. |
| `[ToolCall("name")]` | Method | No | Marks a public method as an inline agent tool. See [Tool-call methods](#tool-call-methods-toolcall). |
| `[AgentOutput("format")]` | Method | No | Hints to the agent how to interpret the method's return value (`json`, `markdown`, `text`). |
| `[Output]` | Data type class | No | Marks a nested class as the structured output type. At most one `[Output]` class per task. The instance's `outputSnapshotJson` reflects the last `Emit` of this type. |

---

### Requirements and preflight attributes

Tasks can declare runtime prerequisites directly on the class and on selected
parameters. These are parsed into structured requirement records, returned in the
 definition response, and enforced by the preflight checker before instance creation.

```csharp
[Task("vision-report")]
[RequiresProvider("OpenAI")]
[RequiresModelCapability("Vision")]
[RequiresModule("transcription")]
[RequiresPermission("CanExecuteTasks")]
public class VisionReportTask
{
    [ModelId]
    [RequiresCapability("Vision")]
    public string Model { get; set; } = "gpt-4.1";

    public async Task RunAsync(CancellationToken ct) { }
}
```

Supported requirement attributes:

| Attribute | Meaning |
|---|---|
| `[RequiresProvider("Name")]` | A configured provider with a usable key must exist. |
| `[RequiresModelCapability("Capability")]` | At least one model with the named capability must exist. |
| `[RequiresModel("NameOrCustomId")]` | A specific model must exist. |
| `[RequiresModule("module-id")]` | A module must be enabled. |
| `[RecommendsModule("module-id")]` | A non-blocking recommendation shown as a warning. |
| `[RequiresPlatform(TaskPlatform.Windows | TaskPlatform.Linux)]` | Restricts the task to supported host platforms. |
| `[RequiresPermission("PermissionKey")]` | Requires a matching permission/global flag for the caller agent. |
| `[ModelId]` | Marks a parameter as a model lookup parameter for preflight checks. |
| `[RequiresCapability("Capability")]` | Requires the model referenced by that parameter to expose the named capability. |

---

### Trigger attributes

Tasks can self-register triggers directly on the class. Cron triggers become
scheduled jobs. Other triggers become runtime trigger bindings watched by the
trigger host service.

```csharp
[Task("notify-on-change")]
[Schedule("0 */15 * * *", Timezone = "UTC")]
[OnFileChanged("C:\\watch", Pattern = "*.pdf")]
[OnTrigger("MyCustomSource", Filter = "important")]
public class NotifyOnChangeTask
{
    public async Task RunAsync(CancellationToken ct) { }
}
```

Common trigger attributes:

| Attribute | Purpose |
|---|---|
| `[Schedule("cron")]` | Register a cron-based scheduled job. |
| `[OnEvent("EventType")]` | Fire on a SharpClaw event bus event. |
| `[OnFileChanged(path, Pattern = "...")]` | Fire when files change under a watched path. |
| `[OnProcessStarted("name")]` / `[OnProcessStopped("name")]` | Fire on process lifecycle events. |
| `[OnWebhook("/route")]` | Expose the task as a webhook target. |
| `[OnStartup]` / `[OnShutdown]` | Fire on application lifecycle transitions. |
| `[OsShortcut("Label")]` | Install an OS launcher shortcut for the task. |
| `[OnTrigger("SourceName")]` | Bind to a custom module-provided trigger source. |

---

### Module-owned trigger notes

Some trigger attributes are implemented by modules rather than by the core task host.
Those attributes still parse and register normally, but preflight emits a non-blocking
module recommendation if the owning module is disabled.

| Trigger attributes | Owning module |
|---|---|
| `[OnWindowFocused]`, `[OnWindowBlurred]`, `[OnHotkey]`, `[OnSystemIdle]`, `[OnSystemActive]`, `[OnScreenLocked]`, `[OnScreenUnlocked]`, `[OnDeviceConnected]`, `[OnDeviceDisconnected]`, `[OnProcessStarted]`, `[OnProcessStopped]`, `[OsShortcut]` | `sharpclaw_computer_use` |
| `[OnQueryReturnsRows]` | `sharpclaw_database_access` |

That means a task using `[OnHotkey("Ctrl+Shift+R")]` can still be saved on a host where
Computer Use is off, but the task's preflight result will recommend enabling
`sharpclaw_computer_use`, and the trigger will not become active until that module is
available.

---

### Parameters

Every **public property** on the task class is a parameter. The task system
reads the property name, CLR type, and any XML-doc description to build the
parameter schema that is stored in the definition and exposed in agent tool
calls.

```csharp
public class ReportTask
{
    /// <summary>Which channel to post results to.</summary>
    public Guid   ChannelId   { get; set; }

    /// <summary>Maximum items to include.</summary>
    public int    Limit       { get; set; } = 10;

    /// <summary>Optional filter keyword.</summary>
    public string? Filter     { get; set; }

    public async Task RunAsync(CancellationToken ct) { ... }
}
```

Supported parameter types:

- Primitive: `string`, `int`, `long`, `float`, `double`, `decimal`, `bool`, `Guid`, `DateTime`, `DateTimeOffset`
- Nullable variants of all primitives (`string?`, `int?`, …)
- Collection variants: `List<T>`, `IList<T>`, `IEnumerable<T>`, `ICollection<T>` where `T` is a primitive or a task-defined data type
- Task-defined data types (inner classes in the same script)

Parameters without a default value and whose type is not nullable are treated
as **required** at launch time. Missing required parameters produce a `TASK201`
compilation error and prevent the instance from starting.

---

### Data types and the output type

You can declare inner classes to hold structured data. Use them as parameter
types, local variable types, or as the task's declared output.

```csharp
[Task("summarise")]
public class SummariseTask
{
    [Output]
    public class Summary
    {
        public string Title    { get; set; } = "";
        public string Body     { get; set; } = "";
        public int    WordCount { get; set; }
    }

    public string SourceText { get; set; } = "";

    public async Task RunAsync(CancellationToken ct)
    {
        var agent   = await FindAgent("Summariser");
        var raw     = await Chat(agent, $"Summarise:\n{SourceText}");
        var result  = await ParseResponse<Summary>(raw);
        await Emit(result);
    }
}
```

Only one class per script may carry `[Output]`. Its last `Emit` value is
persisted as `outputSnapshotJson` on the instance.

---

### The entry point

The runtime calls `public async Task RunAsync(CancellationToken ct)`. The
`CancellationToken` is signalled when the instance is cancelled externally.
All built-in step methods accept or propagate this token automatically.

A task that should run until stopped should call `await WaitUntilStopped()` at
the end of its body rather than spinning in a loop.

---

### Allowed language constructs

The script is executed by a compiled execution plan; it is not run through
Roslyn directly. The parser recognises a specific whitelist of C# constructs:

- Variable declarations: `var x = expr;` or `TypeName x = expr;`
- Assignment: `x = expr;`
- `if` / `else if` / `else`
- `foreach (var item in collection)`
- `while (condition)`
- `await expr;`
- `return;` or `return expr;`
- All built-in step calls (see [Step reference](#step-reference))

General-purpose C# beyond this whitelist is not permitted. There is no
reflection, no P/Invoke, no file I/O through `System.IO`, and no arbitrary
method calls to outside types.

---

### Step reference

Each call in `RunAsync` maps to a registered step key (a stable
`{moduleId}.{step_name}` wire-format string contributed by a module). The
following built-in methods are available inside a task body:

#### Agent interaction

| Call | Step key | Description |
|------|----------|-------------|
| `await Chat(agent, message)` | `core.chat` | Send a message to an agent and await the full text response. |
| `await ChatStream(agent, message)` | `core.chat_stream` | Send a message and stream the response token by token to the task's SSE output channel. |
| `await ChatToThread(agent, threadId, message)` | `core.chat_to_thread` | Send a message into a specific thread and await the response. |

`agent` is a reference obtained from `FindAgent`. `message` is a string expression.

#### Lookup and creation

| Call | Step key | Description |
|------|----------|-------------|
| `await FindModel(nameOrId)` | `core.find_model` | Locate a model by display name or custom ID. |
| `await FindProvider(nameOrId)` | `core.find_provider` | Locate a provider by display name or custom ID. |
| `await FindAgent(nameOrId)` | `core.find_agent` | Locate an agent by display name or custom ID. |
| `await CreateAgent(name, modelId)` | `core.create_agent` | Create a new agent at runtime. |
| `await CreateThread(channelId, name)` | `core.create_thread` | Create a new thread in a channel. |

#### Output and parsing

| Call | Step key | Description |
|------|----------|-------------|
| `await Emit(value)` | `core.emit` | Push a result object to all SSE listeners. If `value` matches the `[Output]` type, `outputSnapshotJson` is updated. |
| `await ParseResponse<T>(text)` | `core.parse_response` | Ask the runtime to parse a string into a typed `T` (must be a task-defined data type or primitive). |

#### HTTP

| Call | Step key | Description |
|------|----------|-------------|
| `await HttpGet(url)` | `core.http_request` | GET request; returns the response body as a string. |
| `await HttpPost(url, body)` | `core.http_request` | POST with a JSON body string. |
| `await HttpPut(url, body)` | `core.http_request` | PUT with a JSON body string. |
| `await HttpDelete(url)` | `core.http_request` | DELETE request. |

#### Control and state

| Call | Step key | Description |
|------|----------|-------------|
| `await Delay(ms)` | `core.delay` | Pause execution for `ms` milliseconds, respecting cancellation. |
| `await WaitUntilStopped()` | `core.wait_until_stopped` | Block until the instance is cancelled. Use for daemon tasks. |
| `await Log(message)` | `core.log` | Write a log entry visible in instance logs and the SSE stream. |

#### Transcription

| Call | Step key | Description |
|------|----------|-------------|
| `await StartTranscription(deviceId, modelId)` | `sharpclaw.transcription.start_transcription` | Start a live transcription job on an audio device. Requires the Transcription module. |
| `await StopTranscription(jobId)` | `sharpclaw.transcription.stop_transcription` | Stop a running transcription job. |
| `await GetDefaultInputAudio()` | `sharpclaw.transcription.get_default_input_audio` | Resolve the system-default audio input device ID. |

---

### Tool-call methods (`[ToolCall]`)

A `[ToolCall]` method is a public instance method that the runtime exposes
as an agent tool during the task's execution. When an agent calls the tool,
the runtime routes the call back into the running task instance.

```csharp
[Task("monitor")]
public class MonitorTask
{
    [ToolCall("get_status")]
    [AgentOutput("json")]
    public async Task<string> GetStatusAsync()
    {
        return """{ "healthy": true }""";
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await WaitUntilStopped();
    }
}
```

`[AgentOutput("format")]` hints to the agent how to interpret the return
value. Supported values: `json`, `markdown`, `text`.

---

### AgentOutput format hints

When a `[ToolCall]` method is decorated with `[AgentOutput("format")]`,
the format string is embedded in the tool schema so the calling agent can
apply appropriate post-processing:

| Value | Meaning |
|-------|---------|
| `json` | Return value is a JSON string; the agent should parse it. |
| `markdown` | Return value is Markdown; the agent may render it. |
| `text` | Plain text; no special processing. |

---

## Validation diagnostic codes

Validation runs on the parsed definition when it is registered or updated.
A definition with any `Error`-severity diagnostic cannot be saved.

| Code | Severity | Meaning |
|------|----------|---------|
| `TASK101` | Error | A parameter has an invalid type. Only primitives and task-defined data types are allowed. |
| `TASK102` | Error | A property on a data type has an invalid type. |
| `TASK103` | Error | More than one class is marked with `[Output]`. Only one output type is allowed. |
| `TASK104` | Error | A variable is declared more than once in the same scope. |
| `TASK105` | Error | A variable declaration uses an invalid type. |
| `TASK106` | Error | A `foreach` loop is missing its iteration variable. |
| `TASK107` | Error | A `foreach` loop is missing its source expression. |
| `TASK108` | Error | `ParseResponse<T>` references an unknown type `T`. |

---

## Compilation diagnostic codes

Compilation runs when an instance is started. It resolves parameter values
against the definition's parameter schema.

| Code | Severity | Meaning |
|------|----------|---------|
| `TASK201` | Error | A required parameter was not supplied at launch time. |
| `TASK202` | Error | A supplied parameter value could not be converted to the declared type. |
| `TASK203` | Error | The declared default value for a parameter could not be converted to the declared type. |

---

## Preflight and requirement checks

Before a task instance is created, SharpClaw can evaluate the declared requirement
set without starting execution.

- API: `GET /tasks/{taskId}/preflight?param.Model=my-model`
- CLI: `task preflight <taskId> [--param key=value ...]`

The preflight result returns:

- `isBlocked` — `true` when any error-severity requirement failed
- `findings[]` — one row per requirement, including pass/fail, severity, and message

For module-owned triggers, the parser also emits implicit recommendation findings.
Typical examples are:

- `RecommendsModule("sharpclaw_computer_use")` for desktop, process, hotkey, and
  `OsShortcut` triggers
- `RecommendsModule("sharpclaw_database_access")` for `[OnQueryReturnsRows]`

Preflight also runs automatically during instance creation. If a blocking check
fails, the API returns `422 Unprocessable Entity` with the same structured result.

---

## Execution lifecycle

An instance moves through a defined set of statuses:

```
Queued  →  Running  →  Completed
                  ↘  Failed
                  ↘  Cancelled
        ↔  Paused
```

| Status | Meaning |
|--------|---------|
| `Queued` | Instance created but not yet started. Call `POST .../start` to begin. When `channelId` is supplied at launch the instance also starts immediately. |
| `Running` | Orchestrator is actively executing steps. |
| `Paused` | Execution suspended at the current step. Resumable via `POST .../start`. |
| `Completed` | `RunAsync` returned normally. |
| `Failed` | An unhandled exception occurred or a compilation error was found at launch. `errorMessage` is populated. |
| `Cancelled` | `POST .../cancel` was called; the `CancellationToken` was signalled and the task exited. |

Transitions:

- `POST /tasks/{id}/instances/{iid}/start` — `Queued → Running` or `Paused → Running`
- `POST /tasks/{id}/instances/{iid}/cancel` — any running state → `Cancelled` (hard cancellation via token)
- `POST /tasks/{id}/instances/{iid}/stop` — `Running → Cancelled` (graceful stop via orchestrator signal)

---

## Permissions

| Permission key | What it grants |
|----------------|----------------|
| `CanManageTasks` | Create, update, and delete task **definitions**. |
| `CanExecuteTasks` | Start, stop, pause, and resume task **instances**. |
| `CanInvokeTasksAsTool` | See active task definitions as agent tool-call schemas. |

These are role-level permissions. Assign them through `PUT /roles/{id}/permissions`.

---

## Agent tool exposure

When an agent has the `CanInvokeTasksAsTool` permission, every active task
definition is surfaced to it as a tool. The tool name is:

```
task_invoke__{task-name}
```

For example, a task registered as `"summarise"` becomes the tool
`task_invoke__summarise`. The tool schema is built from the task's
parameter definitions, with types mapped to JSON Schema. The agent
supplies parameter values as the tool's arguments; the runtime creates
and starts an instance automatically.

This mechanism lets an agent decide at runtime to launch a background
task — for example, starting a monitoring loop or triggering a report
pipeline — without any hardcoded wiring.

---

## Scheduling

Tasks can be launched on a schedule through scheduled jobs. SharpClaw now supports
both interval-based repetition and cron expressions, including optional timezones,
previewing future fire times, pausing/resuming jobs, and missed-fire handling.

Common scheduling flows:

- declare `[Schedule("0 9 * * 1-5", Timezone = "UTC")]` in the task source
- create a scheduled job explicitly through the scheduler endpoints or CLI
- preview future executions before saving a cron expression

Useful scheduler operations:

- API preview: `GET /scheduled-jobs/preview?expression=...&timezone=...&count=10`
- API preview existing job: `GET /scheduled-jobs/{id}/preview?count=10`
- CLI preview: `task schedule preview "0 9 * * 1-5" [--timezone UTC] [--count 10]`
- CLI create/update: `task schedule create ...`, `task schedule update ...`
- CLI pause/resume: `task schedule pause <jobId>`, `task schedule resume <jobId>`

When the scheduler fires a job it calls `TaskService.CreateInstanceAsync` and then
starts the orchestrator, the same as a normal manual task launch.

---

## Trigger system

The trigger system lets a task definition register itself with runtime watchers.

- `TaskTriggerRegistrar` persists trigger bindings extracted from task source
- `TaskTriggerHostService` starts and reloads the active watcher set
- core-owned sources cover events, file changes, host reachability, network changes,
  lifecycle events, and generic metric polling
- module-owned sources extend the host with domain-specific watchers such as desktop,
  hotkey, process, shortcut, and database-query triggers
- module authors can register custom `ITaskTriggerSource` implementations and bind
  tasks to them with `[OnTrigger("SourceName")]`

For the current host, `GET /tasks/trigger-sources` and `task trigger-sources` are the
authoritative way to see which trigger sources are actually available. If a module that
owns a trigger kind is disabled, existing definitions stay registered but those trigger
bindings cannot activate until the module is enabled again.

Useful trigger-related endpoints:

- `GET /tasks/trigger-sources`
- `POST /tasks/{taskId}/triggers/enable`
- `POST /tasks/{taskId}/triggers/disable`
- `POST /tasks/{taskId}/shortcuts/install`
- `DELETE /tasks/{taskId}/shortcuts`

Webhook triggers are exposed under `/webhooks/tasks/{route}` and can optionally
validate HMAC signatures when a secret environment variable is configured.

---

## CLI reference

Definition and instance commands:

```text
task create <sourceFilePath>
task list
task get <id>
task update <id> <sourceFilePath>
task activate <id>
task deactivate <id>
task delete <id>
task preflight <taskId> [--param key=value ...]
task create-queued <taskId> [channelId] [--param key=value ...]
task start <taskId> [channelId] [--param key=value ...]
task run <taskId> [channelId] [--param key=value ...]
task start-instance <instanceId>
task instances <taskId>
task instance <instanceId>
task outputs <instanceId> [--since <timestamp>]
task cancel <instanceId>
task stop <instanceId>
task pause <instanceId>
task resume <instanceId>
task listen <instanceId>
```

Scheduling, triggers, and shortcuts:

```text
task schedule list
task schedule get <jobId>
task schedule create <taskId> --cron <expr> [--timezone <tz>] [--name <name>]
task schedule update <jobId> --cron <expr> [--timezone <tz>]
task schedule pause <jobId>
task schedule resume <jobId>
task schedule delete <jobId>
task schedule preview <expr> [--timezone <tz>] [--count N]
task trigger-sources
task triggers enable <taskId>
task triggers disable <taskId>
task shortcuts install <taskId>
task shortcuts remove <taskId>
```

---

## REST API reference

> **Base URL:** `http://127.0.0.1:48923`
> **Auth:** `X-Api-Key` header on every request.

### Task definitions

#### `POST /tasks/validate`

Validate a task source document without saving it.

**Request:**

```json
{
  "sourceText": "string"
}
```

**Response `200`:** validation result with `isValid` and `diagnostics[]`.

---

#### `POST /tasks`

Register a new task definition. The source is parsed and validated
immediately; a definition with validation errors is rejected.

**Request:**

```json
{
  "sourceText": "string"
}
```

**Response `200`:** [`TaskDefinitionResponse`](#taskdefinitionresponse)

---

#### `GET /tasks`

List all registered task definitions.

**Response `200`:** `TaskDefinitionResponse[]`

---

#### `GET /tasks/{taskId}`

Get a single definition.

**Response `200`:** `TaskDefinitionResponse`
**Response `404`:** Not found.

---

#### `PUT /tasks/{taskId}`

Update an existing definition's source or active state. Changing
`sourceText` re-runs parse and validation.

**Request:**

```json
{
  "sourceText": "string | null",
  "isActive": "bool | null"
}
```

**Response `200`:** `TaskDefinitionResponse`
**Response `404`:** Not found.

---

#### `DELETE /tasks/{taskId}`

Delete a definition. Running instances are cancelled first.

**Response `204`:** Deleted.
**Response `404`:** Not found.

---

#### `GET /tasks/{taskId}/preflight`

Runs requirement checks without creating an instance.

Query parameters are passed as `param.<name>=value`.

**Response `200`:** `TaskPreflightResponse`

---

#### `GET /tasks/trigger-sources`

Lists all registered trigger sources, including built-in and custom module-provided
sources.

**Response `200`:** `TaskTriggerSourceResponse[]`

---

### Task instances

#### `POST /tasks/{taskId}/instances`

Create and optionally start a new instance. If `channelId` is provided,
the instance starts immediately and any token usage is attributed to that
channel. If `channelId` is omitted the instance is created in `Queued`
status and must be started manually.

**Request:**

```json
{
  "channelId": "guid | null",
  "parameterValues": {
    "paramName": "value"
  }
}
```

**Response `200`:** [`TaskInstanceResponse`](#taskinstanceresponse)

---

#### `GET /tasks/{taskId}/instances`

List all instances for a definition, newest first.

**Response `200`:** `TaskInstanceResponse[]`

---

#### `GET /tasks/{taskId}/instances/{instanceId}`

Get a single instance. When the instance is bound to a channel,
`channelCost` is populated with aggregated token usage for that channel.

**Response `200`:** `TaskInstanceResponse`
**Response `404`:** Not found.

---

#### `POST /tasks/{taskId}/instances/{instanceId}/start`

Start a `Queued` instance or resume a `Paused` instance.

**Response `204`**

---

#### `POST /tasks/{taskId}/instances/{instanceId}/cancel`

Cancel a running or paused instance. The `CancellationToken` is signalled
immediately. This is a hard stop.

**Response `204`:** Cancelled.
**Response `404`:** Not found.

---

#### `POST /tasks/{taskId}/instances/{instanceId}/stop`

Request a graceful stop through the orchestrator. The task has an
opportunity to handle the signal before exiting.

**Response `204`**

---

#### `GET /tasks/{taskId}/instances/{instanceId}/outputs?since={datetime}`

Retrieve output entries, optionally filtered to those after a given
timestamp. Useful for polling a running instance.

**Response `200`:**

```json
[
  {
    "id": "guid",
    "sequence": 1,
    "data": "string | null",
    "timestamp": "datetime"
  }
]
```

---

### Task trigger and shortcut endpoints

#### `POST /tasks/{taskId}/triggers/enable`

Enable all persisted trigger bindings for a task definition.

**Response `200`:** `{ "enabled": number }`

---

#### `POST /tasks/{taskId}/triggers/disable`

Disable all persisted trigger bindings for a task definition without deleting them.

**Response `200`:** `{ "disabled": number }`

---

#### `POST /tasks/{taskId}/shortcuts/install`

Install OS launcher shortcuts declared with `[OsShortcut]` in the task source.
Requires the `sharpclaw_computer_use` module.

**Response `200`:** `{ "installed": number }`

---

#### `DELETE /tasks/{taskId}/shortcuts`

Remove all OS launcher shortcuts for a task definition.

**Response `204`**

---

### SSE streaming

Connect to the live output stream of a running instance:

```
GET /tasks/{taskId}/instances/{instanceId}/stream
Accept: text/event-stream
```

Events are emitted as `data: <json>` lines. Each event has the shape:

```json
{
  "type": "output | log | status",
  "data": "string | null",
  "timestamp": "datetime"
}
```

---

### Response shapes

#### `TaskDefinitionResponse`

```json
{
  "id": "guid",
  "name": "string",
  "description": "string | null",
  "sourceText": "string",
  "isActive": "bool",
  "parameters": [
    {
      "name": "string",
      "typeName": "string",
      "description": "string | null",
      "isRequired": "bool",
      "defaultValue": "string | null"
    }
  ],
  "requirements": [
    {
      "kind": "string",
      "value": "string | null",
      "severity": "Error | Warning",
      "description": "string"
    }
  ],
  "diagnostics": [
    {
      "code": "string",
      "severity": "Error | Warning",
      "message": "string"
    }
  ],
  "createdAt": "datetime",
  "updatedAt": "datetime"
}
```

#### `TaskInstanceResponse`

```json
{
  "id": "guid",
  "taskDefinitionId": "guid",
  "status": "Queued | Running | Paused | Completed | Failed | Cancelled",
  "errorMessage": "string | null",
  "outputSnapshotJson": "string | null",
  "channelId": "guid | null",
  "channelCost": { "promptTokens": 0, "completionTokens": 0 },
  "createdAt": "datetime",
  "updatedAt": "datetime"
}
```

#### `TaskPreflightResponse`

```json
{
  "isBlocked": "bool",
  "findings": [
    {
      "kind": "string",
      "value": "string | null",
      "passed": "bool",
      "severity": "Error | Warning",
      "message": "string"
    }
  ]
}
```

#### `TaskTriggerSourceResponse`

```json
{
  "id": "string",
  "displayName": "string",
  "ownerModuleId": "string | null",
  "isAvailable": "bool"
}
```

---

## Module authoring — extending the task step system

This section is for authors building SharpClaw modules that need to add
new callable steps to the task scripting language.

---

### Step keys

Every parsed step carries a `StepKey` string stored in
`TaskStepDefinition.StepKey`. The orchestrator routes execution by looking
the key up in `TaskStepRegistry.Default`, where every descriptor is owned
by a module. There are no core-owned step keys; the built-in step set is
contributed by the bundled modules `sharpclaw_task_scripting`,
`sharpclaw_agent_orchestration`, and `sharpclaw_http`, which expose their
keys through the module-local constant classes `TaskScriptingStepKeys`,
`AgentOrchestrationStepKeys`, and `HttpStepKeys` respectively. The literal
string values (for example `core.chat`, `core.http_request`,
`core.declare_variable`) are kept stable for backward compatibility with
serialized task scripts; only the C# location of the constants changed.

New modules should use namespaced keys of the form
`{moduleId}.{step_name}` to avoid collisions with the bundled modules or
with other third-party modules.

```csharp
// Recommended pattern — define your keys as constants in your module project.
public static class TranscriptionStepKeys
{
    public const string StartTranscription   = "sharpclaw.transcription.start_transcription";
    public const string StopTranscription    = "sharpclaw.transcription.stop_transcription";
    public const string GetDefaultInputAudio = "sharpclaw.transcription.get_default_input_audio";
}
```

---

### `TaskStepRegistry`

`TaskStepRegistry.Default` is a thread-safe singleton populated at startup.
It maps script method names to `TaskStepDescriptor` records and supports
lookup in both directions. The parser calls `FindByMethod` to translate a
method call in a script body into a `StepKey`; tooling and diagnostics can
call `FindByKey` to go the other way.

```csharp
// Reading — called internally by TaskScriptParser:
TaskStepDescriptor? descriptor = TaskStepRegistry.Default.FindByMethod("StartTranscription");

// Reading — useful in diagnostics or editors:
TaskStepDescriptor? descriptor = TaskStepRegistry.Default.FindByKey(
    TranscriptionStepKeys.StartTranscription);
```

You do not call `Register` directly. Descriptors are added when you return
them from `ITaskParserModuleExtension.StepKeyMappings` and the parser calls
`RegisterModule` with your extension.

---

### `TaskStepDescriptor`

A `TaskStepDescriptor` fully describes one step operation to the parser and
registry. The fields you need when building module steps are:

| Property | Type | Purpose |
|---|---|---|
| `MethodName` | `string?` | Script method name (e.g. `"StartTranscription"`). `null` for non-method constructs. |
| `StepKey` | `string` | Namespaced wire key (e.g. `"sharpclaw.transcription.start_transcription"`). |
| `OwnerId` | `string` | Your module ID (e.g. `"sharpclaw_transcription"`). |
| `FirstArgIsExpression` | `bool` | Capture the first argument as `Expression` on the step. Use when the first arg is a runtime string or variable reference. |
| `ExpressionArgIndex` | `int` | If `Expression` is not the first arg, set the zero-based index here. |
| `CapturesGenericType` | `bool` | Set `true` for methods like `ParseResponse<T>` where the generic type name should be captured as `TypeName`. |
| `HttpMethod` | `string?` | HTTP verb string for HTTP-request variants; `null` for all other steps. |

---

### Implementing `ITaskParserModuleExtension`

`ITaskParserModuleExtension` tells the parser which method names belong to
your module and how to map them to step keys. Register one implementation
per module. The parser calls `RegisterModule` once at startup with each
implementation it receives from DI.

```csharp
public sealed class TranscriptionParserExtension : ITaskParserModuleExtension
{
    public IReadOnlyDictionary<string, (string StepKey, string ModuleId)> StepKeyMappings
        => new Dictionary<string, (string, string)>
        {
            ["StartTranscription"]   = (TranscriptionStepKeys.StartTranscription,   "sharpclaw_transcription"),
            ["StopTranscription"]    = (TranscriptionStepKeys.StopTranscription,    "sharpclaw_transcription"),
            ["GetDefaultInputAudio"] = (TranscriptionStepKeys.GetDefaultInputAudio, "sharpclaw_transcription"),
        };

    // Map event-handler method names to module-owned trigger keys.
    // Leave empty if your module adds no event trigger types.
    public IReadOnlyDictionary<string, (string TriggerKey, string ModuleId)> EventTriggerMappings
        => new Dictionary<string, (string, string)>();

    // Method names whose first argument should be captured as Expression.
    public IReadOnlySet<string> SingleArgExpressionMethods
        => new HashSet<string> { "StartTranscription" };
}
```

Register the extension in your module's DI setup:

```csharp
services.AddSingleton<ITaskParserModuleExtension, TranscriptionParserExtension>();
```

The built-in `TaskScriptParser` resolves all registered
`ITaskParserModuleExtension` instances from DI and calls `RegisterModule`
for each one before any parsing takes place.

---

### Implementing `ITaskStepExecutorExtension`

`ITaskStepExecutorExtension` handles execution of the steps your module
declared in the parser extension. The orchestrator calls `CanExecute` on
every registered extension for any step key that is not a built-in core key,
then calls `ExecuteAsync` on the first matching extension.

```csharp
public sealed class TranscriptionExecutorExtension : ITaskStepExecutorExtension
{
    private readonly ILiveTranscriptionOrchestrator _transcription;

    public TranscriptionExecutorExtension(ILiveTranscriptionOrchestrator transcription)
        => _transcription = transcription;

    public string ModuleId => "sharpclaw_transcription";

    public bool CanExecute(string moduleStepKey)
        => moduleStepKey.StartsWith("sharpclaw.transcription.", StringComparison.Ordinal);

    public async Task<bool> ExecuteAsync(
        string moduleStepKey,
        ITaskStepExecutionContext context,
        IReadOnlyList<string>? arguments,
        string? expression,
        string? resultVariable)
    {
        switch (moduleStepKey)
        {
            case TranscriptionStepKeys.StartTranscription:
            {
                var deviceId = expression ?? "";
                var modelId  = arguments?.ElementAtOrDefault(1) ?? "";
                var jobId    = await _transcription.StartAsync(deviceId, modelId, context.CancellationToken);
                if (resultVariable is not null)
                    context.SetVariable(resultVariable, jobId.ToString());
                return true;
            }

            case TranscriptionStepKeys.StopTranscription:
            {
                var jobId = Guid.Parse(arguments?.FirstOrDefault() ?? "");
                await _transcription.StopAsync(jobId, context.CancellationToken);
                return true;
            }

            case TranscriptionStepKeys.GetDefaultInputAudio:
            {
                var deviceId = await _transcription.GetDefaultInputAudioAsync(context.CancellationToken);
                if (resultVariable is not null)
                    context.SetVariable(resultVariable, deviceId.ToString());
                return true;
            }

            default:
                throw new InvalidOperationException(
                    $"TranscriptionExecutorExtension received unrecognised step key '{moduleStepKey}'.");
        }
    }
}
```

Register the extension in DI:

```csharp
services.AddSingleton<ITaskStepExecutorExtension, TranscriptionExecutorExtension>();
```

`ExecuteAsync` returns `true` to continue task execution and `false` to
signal an early exit from the task body. Throw any exception to propagate
a step execution failure, which will transition the instance to `Failed`.

---

### `ITaskStepExecutionContext`

The context object passed to `ExecuteAsync` provides access to the runtime
state of the current task instance. Key members:

| Member | Purpose |
|---|---|
| `CancellationToken` | Token signalled when the instance is cancelled. Always pass to async I/O. |
| `SetVariable(name, value)` | Store a string result in the task's variable scope. |
| `GetVariable(name)` | Read a previously set variable. |
| `LogAsync(message, ct)` | Append a log entry to the instance log and SSE stream. |
| `EmitAsync(obj, ct)` | Push a structured output object to SSE listeners. |

---

### Event trigger extensions

If your module adds a new event trigger type (backed by
`ITaskParserModuleExtension.EventTriggerMappings`), the trigger key is stored
in `TaskStepDefinition.ModuleTriggerKey`. You are responsible for firing those triggers
through the `TaskTriggerHostService` extension point — typically by
registering a custom `ITaskTriggerSource` implementation and including its
source ID in the trigger key mapping.

---

### Module step checklist

When adding a new step to a module:

1. Add a string constant in your module's step-key class using the pattern
   `{moduleId}.{step_name}`.
2. Add an entry to `ITaskParserModuleExtension.StepKeyMappings` with the
   method name and step key.
3. If the first argument is a runtime expression, add the method name to
   `SingleArgExpressionMethods`; if a different argument is the expression,
   set `ExpressionArgIndex` in the descriptor.
4. Handle the new step key in `ITaskStepExecutorExtension.ExecuteAsync`.
5. Update `CanExecute` if you are using a prefix check — no change needed
   if the new key already matches your existing prefix.
6. Add the method call to the step reference table in `Tasks-documentation.md`
   under the appropriate section heading (or create a new module section
   if this is the first step from your module).
Disable all persisted trigger bindings for a task definition.

**Response `200`:** `{ "disabled": number }`

---

#### `POST /tasks/{taskId}/shortcuts/install`

Install or refresh the first `OsShortcut` trigger binding for the task.

**Response `204`**
**Response `404`:** Task not found.
**Response `422`:** Task has no `OsShortcut` trigger.

---

#### `DELETE /tasks/{taskId}/shortcuts`

Remove installed shortcut artifacts for the task.

**Response `204`**
**Response `404`:** Task not found.

---

### SSE streaming

#### `GET /tasks/{taskId}/instances/{instanceId}/stream`

Opens a server-sent event stream for the instance. The connection stays
open until the instance reaches a terminal status.

Each frame:

```
data:{"type":"Output","sequence":1,"timestamp":"2025-01-01T00:00:00Z","data":"..."}
```

| Event type | When emitted |
|------------|-------------|
| `Output` | A step called `Emit` or `ChatStream` produced output. |
| `Log` | A step called `Log`. |
| `StatusChange` | The instance status changed (e.g. `Queued → Running`). |
| `Done` | The instance reached a terminal status. The payload includes the final `TaskInstanceResponse`. |

---

### Response shapes

#### `TaskDefinitionResponse`

```json
{
  "id": "guid",
  "name": "string",
  "description": "string | null",
  "outputTypeName": "string | null",
  "isActive": true,
  "parameters": [
    {
      "name": "string",
      "typeName": "string",
      "description": "string | null",
      "defaultValue": "string | null",
      "isRequired": true
    }
  ],
  "requirements": [
    {
      "kind": "RequiresProvider",
      "severity": "Error",
      "value": "OpenAI",
      "capabilityValue": null,
      "parameterName": null
    }
  ],
  "triggers": [
    {
      "kind": "Custom",
      "triggerValue": "my-source",
      "filter": "important",
      "isEnabled": true
    }
  ],
  "createdAt": "datetime",
  "updatedAt": "datetime",
  "customId": "string | null"
}
```

`outputTypeName` is the name of the `[Output]`-marked class, if any.
`isActive` controls whether the definition is surfaced to agents as a tool.

`requirements` is the parsed requirement list used by preflight checks.
`triggers` is the parsed trigger list surfaced in a client-friendly shape.

#### `TaskPreflightResponse`

```json
{
  "isBlocked": true,
  "findings": [
    {
      "requirementKind": "RequiresProvider",
      "severity": "Error",
      "passed": false,
      "message": "Provider 'OpenAI' is not configured.",
      "parameterName": null
    }
  ]
}
```

#### `TaskTriggerSourceResponse`

```json
{
  "sourceName": "my-source",
  "supportedKinds": ["Custom"],
  "type": "MyCustomTriggerSource",
  "isCustom": true
}
```

#### `TaskInstanceResponse`

```json
{
  "id": "guid",
  "taskDefinitionId": "guid",
  "taskName": "string",
  "status": "Queued | Running | Paused | Completed | Failed | Cancelled",
  "outputSnapshotJson": "string | null",
  "errorMessage": "string | null",
  "logs": [
    {
      "message": "string",
      "level": "string",
      "timestamp": "datetime"
    }
  ],
  "createdAt": "datetime",
  "startedAt": "datetime | null",
  "completedAt": "datetime | null",
  "channelId": "guid | null",
  "channelCost": { }
}
```

`outputSnapshotJson` holds the JSON-serialised value of the last `Emit`
call whose type matches the `[Output]`-marked data class.

`channelCost` is a `ChannelCostResponse` (see the
[Core API — Token cost tracking](Core-API-documentation.md#token-cost-tracking)
section) and is populated only on the single-instance `GET` endpoint when
the instance is bound to a channel.

---

## Practical examples

### One-shot report task

```csharp
[Task("weekly-report")]
[Description("Generates a weekly summary and posts it to a channel.")]
public class WeeklyReportTask
{
    public Guid   ChannelId  { get; set; }
    public string AgentName  { get; set; } = "Analyst";
    public int    WeekOffset { get; set; } = 0;

    public async Task RunAsync(CancellationToken ct)
    {
        var agent    = await FindAgent(AgentName);
        var response = await Chat(agent, $"Generate a weekly report for week offset {WeekOffset}.");
        await Log(response);
    }
}
```

Launch:

```http
POST /tasks/{taskId}/instances
{
  "channelId": "...",
  "parameterValues": { "WeekOffset": -1 }
}
```

---

### Daemon task (run until cancelled)

```csharp
[Task("health-monitor")]
[Description("Polls a service URL and logs the result until stopped.")]
public class HealthMonitorTask
{
    public string Url           { get; set; } = "http://localhost/health";
    public int    IntervalMs    { get; set; } = 30000;

    public async Task RunAsync(CancellationToken ct)
    {
        while (true)
        {
            var body = await HttpGet(Url);
            await Log(body);
            await Delay(IntervalMs);
        }
    }
}
```

Stop gracefully:

```http
POST /tasks/{taskId}/instances/{instanceId}/stop
```

---

### Structured-output task

```csharp
[Task("extract-entities")]
[Description("Extracts named entities from a block of text.")]
public class ExtractEntitiesTask
{
    [Output]
    public class EntityList
    {
        public List<string> People       { get; set; } = new();
        public List<string> Organisations { get; set; } = new();
        public List<string> Locations    { get; set; } = new();
    }

    public string Text      { get; set; } = "";
    public string AgentName { get; set; } = "Extractor";

    public async Task RunAsync(CancellationToken ct)
    {
        var agent   = await FindAgent(AgentName);
        var raw     = await Chat(agent, $"Extract named entities as JSON from:\n{Text}");
        var result  = await ParseResponse<EntityList>(raw);
        await Emit(result);
    }
}
```

The `outputSnapshotJson` on the instance will contain the serialised
`EntityList` after the task completes.

---

### Agent-invokable task

With `CanInvokeTasksAsTool` granted to the agent's role, the agent can
invoke `task_invoke__extract-entities` directly as a tool call, supplying
`Text` and optionally `AgentName` as arguments.

---

### Transcription-driven task

```csharp
[Task("live-notes")]
[Description("Transcribes audio and streams notes to a channel.")]
public class LiveNotesTask
{
    public Guid   ChannelId   { get; set; }
    public string AgentName   { get; set; } = "Scribe";

    public async Task RunAsync(CancellationToken ct)
    {
        var deviceId = await GetDefaultInputAudio();
        var model    = await FindModel("whisper-1");
        var jobId    = await StartTranscription(deviceId, model);

        await WaitUntilStopped();

        await StopTranscription(jobId);
    }
}
```
