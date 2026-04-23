# SharpClaw Tasks

> **Full API reference:** [Core API — Task endpoints](#rest-api-reference)
> **Agent skill file:** [Tasks-skill.md](Tasks-skill.md)

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
  - [Parameters](#parameters)
  - [Data types and the output type](#data-types-and-the-output-type)
  - [The entry point](#the-entry-point)
  - [Allowed language constructs](#allowed-language-constructs)
  - [Step reference](#step-reference)
  - [Tool-call methods](#tool-call-methods-toolcall)
  - [AgentOutput format hints](#agentoutput-format-hints)
- [Validation diagnostic codes](#validation-diagnostic-codes)
- [Compilation diagnostic codes](#compilation-diagnostic-codes)
- [Execution lifecycle](#execution-lifecycle)
- [Permissions](#permissions)
- [Agent tool exposure](#agent-tool-exposure)
- [Scheduling](#scheduling)
- [REST API reference](#rest-api-reference)
  - [Task definitions](#task-definitions)
  - [Task instances](#task-instances)
  - [SSE streaming](#sse-streaming)
  - [Response shapes](#response-shapes)
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

Each construct in `RunAsync` maps to a `TaskStepKind`. The following built-in
methods are available inside a task body:

#### Agent interaction

| Call | Step kind | Description |
|------|-----------|-------------|
| `await Chat(agent, message)` | `Chat` | Send a message to an agent and await the full text response. |
| `await ChatStream(agent, message)` | `ChatStream` | Send a message and stream the response token by token to the task's SSE output channel. |
| `await ChatToThread(agent, threadId, message)` | `ChatToThread` | Send a message into a specific thread and await the response. |

`agent` is a reference obtained from `FindAgent`. `message` is a string expression.

#### Lookup and creation

| Call | Step kind | Description |
|------|-----------|-------------|
| `await FindModel(nameOrId)` | `FindModel` | Locate a model by display name or custom ID. |
| `await FindProvider(nameOrId)` | `FindProvider` | Locate a provider by display name or custom ID. |
| `await FindAgent(nameOrId)` | `FindAgent` | Locate an agent by display name or custom ID. |
| `await CreateAgent(name, modelId)` | `CreateAgent` | Create a new agent at runtime. |
| `await CreateThread(channelId, name)` | `CreateThread` | Create a new thread in a channel. |

#### Output and parsing

| Call | Step kind | Description |
|------|-----------|-------------|
| `await Emit(value)` | `Emit` | Push a result object to all SSE listeners. If `value` matches the `[Output]` type, `outputSnapshotJson` is updated. |
| `await ParseResponse<T>(text)` | `ParseResponse` | Ask the runtime to parse a string into a typed `T` (must be a task-defined data type or primitive). |

#### HTTP

| Call | Step kind | Description |
|------|-----------|-------------|
| `await HttpGet(url)` | `HttpRequest` | GET request; returns the response body as a string. |
| `await HttpPost(url, body)` | `HttpRequest` | POST with a JSON body string. |
| `await HttpPut(url, body)` | `HttpRequest` | PUT with a JSON body string. |
| `await HttpDelete(url)` | `HttpRequest` | DELETE request. |

#### Control and state

| Call | Step kind | Description |
|------|-----------|-------------|
| `await Delay(ms)` | `Delay` | Pause execution for `ms` milliseconds, respecting cancellation. |
| `await WaitUntilStopped()` | `WaitUntilStopped` | Block until the instance is cancelled. Use for daemon tasks. |
| `await Log(message)` | `Log` | Write a log entry visible in instance logs and the SSE stream. |

#### Transcription

| Call | Step kind | Description |
|------|-----------|-------------|
| `await StartTranscription(deviceId, modelId)` | `StartTranscription` | Start a live transcription job on an audio device. Requires the Transcription module. |
| `await StopTranscription(jobId)` | `StopTranscription` | Stop a running transcription job. |
| `await GetDefaultInputAudio()` | `GetDefaultInputAudio` | Resolve the system-default audio input device ID. |

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

Tasks can be launched on a schedule through `ScheduledJobDB`. A scheduled
job entry may reference a `TaskDefinitionId` along with a
`ParameterValuesJson` dictionary and an optional `CallerAgentId` for audit
purposes. When the scheduler fires the job it calls
`TaskService.CreateInstanceAsync` and immediately starts the orchestrator,
exactly as if a user had called `POST /tasks/{id}/instances` with
`channelId` set.

Scheduled jobs are managed through the job scheduler endpoints; see the
[Core API reference](Core-API-documentation.md) for the scheduler section.

---

## REST API reference

> **Base URL:** `http://127.0.0.1:48923`
> **Auth:** `X-Api-Key` header on every request.

### Task definitions

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
  "createdAt": "datetime",
  "updatedAt": "datetime",
  "customId": "string | null"
}
```

`outputTypeName` is the name of the `[Output]`-marked class, if any.
`isActive` controls whether the definition is surfaced to agents as a tool.

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
