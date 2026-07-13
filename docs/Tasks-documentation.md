# SharpClaw Tasks

> **Creation guide:** [guides/Task-Creation-Guide.md](guides/Task-Creation-Guide.md)
> **Skill reference:** [Tasks-skill.md](Tasks-skill.md)
> **Module authoring:** [guides/Module-Creation-Guide.md](guides/Module-Creation-Guide.md)

Tasks are user-defined automation scripts that run as managed background
processes inside SharpClaw. A task is a small C# class registered through the
API or CLI at runtime — there is no application code change, no rebuild, and
no restart. The task host parses the script, validates it, stores it as a
reusable definition, and later compiles and runs instances of it on demand or
in response to triggers.

The intended authoring model is deliberately close to ordinary C# scripting.
Use `if`, `while`, `foreach`, `for`, `try/catch`, explicit retry loops,
ordinary local variables, and direct method calls. A task should feel like a
durable SharpClaw-aware version of a REPL or shell automation snippet: cheap
enough to throw away when the job is done, but saved, parameterized,
preflighted, cancellable, logged, streamable, and able to call SharpClaw
module, tool, model, and task operations through the runtime.

This document describes the task system at a conceptual level: what task
mechanics exist, how the pipeline fits together, and how modules contribute
callable operations and triggers that scripts can use. It deliberately does
**not** enumerate the specific module methods or trigger attributes available on your
installation — that surface is contributed by modules and is documented in the
corresponding module pages under [docs/modules/](modules/).

---

## Table of contents

- [Concepts](#concepts)
- [Pipeline](#pipeline)
- [Task scripts at a glance](#task-scripts-at-a-glance)
- [Parameters and data types](#parameters-and-data-types)
- [Requirements and preflight](#requirements-and-preflight)
- [Triggers](#triggers)
- [Scheduling](#scheduling)
- [Execution lifecycle](#execution-lifecycle)
- [Output and streaming](#output-and-streaming)
- [Permissions](#permissions)
- [Agent tool exposure](#agent-tool-exposure)
- [How modules contribute the task surface](#how-modules-contribute-the-task-surface)
- [REST API](#rest-api)
- [CLI reference](#cli-reference)

---

## Concepts

| Term | Meaning |
|------|---------|
| **Task definition** | A registered script. Has a name, optional description, parameter declarations, parsed requirements, parsed triggers, and source text. |
| **Task instance** | A single execution of a definition. Has its own status, logs, and output snapshot. |
| **Task orchestrator** | Server component that walks the compiled execution plan step by step, managing pause/resume gates and the output channel. |
| **Task runtime host** | Per-instance singleton that owns the CancellationToken, pause gates, and the SSE output channel for one running instance. |
| **Compiled plan** | Artifact produced by the compiler from a validated definition plus resolved parameter values. Passed directly to the orchestrator on instance start. |
| **Step** | One executable operation in a compiled plan. Ordinary C# language statements are lowered by Core; module/tool/model calls are contributed by modules. |
| **Trigger** | A declaration that causes the task host to launch instances automatically in response to an external condition. Triggers are also module-owned. |

---

## Pipeline

```
register source  ->  parse  ->  validate  ->  store definition
submit instance  ->  compile (resolve params)  ->  orchestrate
```

Parsing and validation happen when the definition is registered or updated.
Compilation runs when an instance starts because parameter values are supplied
at launch time.

The parser owns ordinary C# task-language syntax directly. Variable
declarations, assignment, conditionals, loops, return, `Log`, `Task.Delay`,
`WaitUntilStopped`, and structured response parsing are not module-defined
syntax. Modules extend the surface by registering callable
operations such as `Chat`, `Emit`, `CreateAgent`, or domain-specific tools, and
by registering trigger attributes. A script that calls a module operation or
declares a trigger whose owning module is not loaded will still parse, but
preflight will flag the missing module before an instance is created.

---

## Task scripts at a glance

A task script is a single `public` class with a single `RunAsync` entry point.
Public properties become parameters. Inner classes can be used as data types.
Class-level attributes drive metadata, requirements, and triggers.

```csharp
[Task("greet")]
[Description("Sends a greeting and logs the response.")]
public class GreetTask
{
    public string AgentName { get; set; } = "Assistant";
    public string Message   { get; set; } = "Hello!";

    public async Task RunAsync(CancellationToken ct)
    {
        // Step calls here are contributed by enabled modules.
    }
}
```

Universal class-level attributes:

| Attribute | Purpose |
|-----------|---------|
| `[Task("name")]` | Required. Canonical task name used by the API and exposed to agents. |
| `[Description("...")]` | Stored in the definition and surfaced in agent tool schemas. |
| `[Output]` (on an inner class) | Marks a nested class as the structured output type. At most one per script. |
| `[ToolCall("name")]` (on a method) | Exposes a public instance method as an inline agent tool while the instance is running. |
| `[AgentOutput("json|markdown|text")]` (on a method) | Hints at the return-value format for `[ToolCall]` methods. |

Inside `RunAsync` the parser allows a restricted whitelist of constructs:

- variable declaration (`var x = expr;` or `T x = expr;`)
- assignment (`x = expr;`)
- `if` / `else if` / `else`
- `foreach (var item in collection)`
- `while (condition)`
- `await expr;`
- `return;` / `return expr;`
- calls to module methods registered by a loaded module

General-purpose C# is not permitted. There is no reflection, no P/Invoke, no
`System.IO`, and no arbitrary calls to outside types. The exact set of module
methods available to your scripts is controlled entirely by which modules are
loaded — see [How modules contribute the task surface](#how-modules-contribute-the-task-surface).

---

## Parameters and data types

Every public property on the task class is a parameter. The parameter name,
CLR type, and any XML-doc summary are stored in the definition and surfaced in
agent tool schemas.

Supported parameter types:

- Primitives: `string`, `int`, `long`, `float`, `double`, `decimal`, `bool`,
  `Guid`, `DateTime`, `DateTimeOffset`
- Nullable variants of all primitives
- Collections: `List<T>`, `IList<T>`, `IEnumerable<T>`, `ICollection<T>` where
  `T` is a primitive or a task-defined data type
- Inner classes declared in the same script

Properties that have no default and whose type is not nullable are **required**
at launch. Missing required parameters produce a `TASK201` compilation error
and prevent the instance from starting.

A nested class marked `[Output]` becomes the task structured output type. The
last value emitted of that type is persisted as `outputSnapshotJson` on the
instance.

---

## Requirements and preflight

Tasks can declare runtime prerequisites directly on the class and on selected
parameters. Requirement attributes are parsed into structured requirement
records, returned in the definition response, and enforced by the preflight
checker before an instance is created.

```csharp
[Task("vision-report")]
[RequiresProvider("OpenAI")]
[RequiresModelCapability("Vision")]
[RequiresPermission("CanExecuteTasks")]
public class VisionReportTask
{
    [ModelId]
    [RequiresCapability("Vision")]
    public string Model { get; set; } = "gpt-4.1";

    public async Task RunAsync(CancellationToken ct) { }
}
```

Universal requirement attributes:

| Attribute | Meaning |
|---|---|
| `[RequiresProvider("Name")]` | A configured provider with a usable key must exist. |
| `[RequiresModelCapability("Capability")]` | At least one model with the named capability must exist. |
| `[RequiresModel("NameOrCustomId")]` | A specific model must exist. |
| `[RequiresModule("module-id")]` | A given module must be loaded. |
| `[RecommendsModule("module-id")]` | Non-blocking recommendation surfaced as a warning. |
| `[RequiresPlatform(TaskPlatform.X)]` | Restricts the task to supported host platforms. |
| `[RequiresPermission("PermissionKey")]` | Requires a matching permission/global flag for the caller agent. |
| `[ModelId]` (parameter) | Marks a parameter as a model lookup parameter for preflight checks. |
| `[RequiresCapability("Capability")]` (parameter) | The model referenced by that parameter must expose the named capability. |

Preflight is exposed at:

- `GET /tasks/{taskId}/preflight?param.<Name>=<value>`
- `task preflight <taskId> [--param key=value ...]`

Preflight returns `isBlocked` plus a `findings[]` array — one row per
requirement with severity, pass/fail, and a human-readable message. Preflight
also runs automatically during instance creation; a blocking failure surfaces
as `422 Unprocessable Entity` with the same structured payload.

When a task uses a module method or trigger whose owning module is not loaded,
preflight will surface that as a recommendation finding. The exact list of
module methods and trigger attributes available on your installation is
documented per module.

---

## Triggers

A trigger is a class-level attribute that causes the task host to launch
instances automatically in response to an external condition. Cron triggers
become scheduled jobs; everything else becomes a runtime trigger binding
watched by the trigger host service.

```csharp
[Task("notify-on-change")]
[Schedule("0 */15 * * *", Timezone = "UTC")]
[ConcurrencyPolicy(TriggerConcurrency.SkipIfRunning)]
public class NotifyOnChangeTask
{
    public async Task RunAsync(CancellationToken ct) { }
}
```

`[ConcurrencyPolicy(TriggerConcurrency.X)]` controls what happens when a
trigger fires while a previous instance is still running:

| Value | Behaviour |
|-------|-----------|
| `SkipIfRunning` | Drop the new fire. |
| `QueueNext` | Queue exactly one follow-up instance. |
| `ForceNew` | Always start a new instance. |

The catalogue of trigger attributes is contributed entirely by modules. Each
module page under [docs/modules/](modules/) lists the trigger attributes it
owns and the runtime conditions that fire them. Trigger sources actually
available on your installation are reported by `GET /tasks/trigger-sources`
and `task trigger-sources`.

If a task declares a trigger attribute owned by a module that is not loaded,
the definition still saves, but the trigger binding cannot activate until the
owning module is enabled.

Trigger management endpoints:

- `POST /tasks/{taskId}/triggers/enable`
- `POST /tasks/{taskId}/triggers/disable`

---

## Scheduling

Cron-based triggers feed into the scheduled-job system. The scheduler supports
cron expressions with optional timezones, future-fire previewing, pause/resume,
and missed-fire handling.

Useful endpoints:

- `GET /scheduled-jobs/preview?expression=...&timezone=...&count=10`
- `GET /scheduled-jobs/{id}/preview?count=10`

Useful CLI verbs:

- `task schedule preview "0 9 * * 1-5" [--timezone UTC] [--count 10]`
- `task schedule create | update | pause | resume | delete`

When the scheduler fires a job it calls the same instance creation path as a
manual launch.

---

## Execution lifecycle

```
Queued  ->  Running  ->  Completed
                   \->  Failed
                   \->  Cancelled
        <->  Paused
```

| Status | Meaning |
|--------|---------|
| `Queued` | Instance created but not yet started. Call `POST .../start` to begin, or supply `channelId` at creation to start immediately. |
| `Running` | Orchestrator is actively executing steps. |
| `Paused` | Execution suspended at the current step. Resumable via `POST .../start`. |
| `Completed` | `RunAsync` returned normally. |
| `Failed` | An unhandled exception occurred or compilation failed at launch. `errorMessage` is populated. |
| `Cancelled` | `POST .../cancel` was called; the CancellationToken was signalled. |

Transitions:

- `POST /tasks/{id}/instances/{iid}/start` — `Queued -> Running` or `Paused -> Running`
- `POST /tasks/{id}/instances/{iid}/cancel` — any running state -> `Cancelled` (hard cancel)
- `POST /tasks/{id}/instances/{iid}/stop` — `Running -> Cancelled` (graceful stop via orchestrator)

---

## Output and streaming

Output is delivered in two channels:

- A persistent log on the instance (`logs[]` plus the `outputs` endpoint).
- A live SSE stream at `GET /tasks/{taskId}/instances/{instanceId}/stream`.

SSE event types:

| Event | When emitted |
|-------|--------------|
| `Output` | An output step was executed. |
| `Log` | A log step wrote a message. |
| `StatusChange` | The instance status changed. |
| `Done` | The instance reached a terminal status; payload includes the final TaskInstanceResponse. |

If the script declares an `[Output]` data type, the most recently emitted
value of that type is also persisted as `outputSnapshotJson` on the instance
and is returned by the single-instance `GET` endpoint.

---

## Permissions

| Permission key | Grants |
|----------------|--------|
| `CanManageTasks` | Create, update, and delete task definitions. |
| `CanExecuteTasks` | Start, stop, pause, and resume task instances. |
| `CanInvokeTasksAsTool` | See active task definitions as agent tool-call schemas. |

Assign these through `PUT /roles/{id}/permissions`.

---

## Agent tool exposure

When an agent has `CanInvokeTasksAsTool`, Agent Orchestration exposes the
static `ao_invoke_task` tool. The agent supplies either a `taskId` or
`taskName`, plus an optional `parameters` object keyed by task parameter name.

```
ao_invoke_task
```

When the agent invokes the tool, the normal module tool pipeline evaluates the
`CanInvokeTasksAsTool` permission first. If approved, an instance is created
on the current channel and started automatically.

---

## How modules contribute the task surface

The task host ships the language shape. Modules do not teach the parser what
`if`, `while`, `foreach`, `return`, assignment, logging, delay, or cancellation
waits mean. They teach SharpClaw which host operations are callable inside
`RunAsync` and which trigger attributes can launch a saved definition.

From a script-author point of view this means that ordinary control flow is
always available, while calls such as `Chat(...)`, `Emit(...)`,
`CreateChannel(...)`, or a module-specific operation depend on the loaded
module set. A validation or preflight failure for a module operation should be
read the same way as a missing command in a shell script: the script syntax is
valid, but the runtime command surface is not present.

Modules contribute to the task surface through three extension points. Each of
them is fully documented in [Module-Creation-Guide.md](guides/Module-Creation-Guide.md);
this section is a high-level map of the contract surface.

### Module methods (callable inside RunAsync)

A module declares its C#-style task methods to the parser through
`ITaskParserModuleExtension`, and to the orchestrator through
`ITaskStepExecutorExtension` (or by pre-registering descriptors with the
shared `TaskStepRegistry`).

- `ITaskParserModuleExtension` tells the parser the method names or event
  handler names your module recognises, the namespaced step or trigger keys
  they map to, and which arguments are expressions versus literals. The parser
  resolves all registered extensions from DI at startup.
- `ITaskStepExecutorExtension` is the orchestrator-side counterpart. The
  orchestrator routes every runtime dispatch key to the first extension whose
  `CanExecute` returns true and invokes `ExecuteAsync` to perform the work.
- `TaskStepRegistry` is the shared method-name <-> runtime-key index. The parser
  uses it to translate script calls into runtime keys; tooling can use it for
  diagnostics. Modules normally populate it indirectly via their parser
  extension.

Runtime keys should be namespaced per module — `{module_id}.{operation_name}` — so
they do not collide across module authors.

### Trigger attributes (declared on the task class)

A module can register additional trigger attributes through its parser
extension event-trigger mappings, and provide the runtime watcher behind
those attributes by implementing `ITaskTriggerSource` (registered as
`ITaskTriggerSourceProvider`).

- The parser uses event-trigger mappings to recognise the attribute and bind
  it to a module-owned trigger key.
- `ITaskTriggerSource` exposes `EnableTriggerAsync` / `DisableTriggerAsync`
  which the trigger host service calls when bindings are turned on or off.

Trigger sources actually available on the running host are reported by
`GET /tasks/trigger-sources` and `task trigger-sources`.

### Authoring summary

When adding a new operation, define a stable, namespaced runtime key in your
module, add the method mapping to `ITaskParserModuleExtension`, and implement
execution behind `ITaskStepExecutorExtension`. When adding a trigger, map the
attribute or event-handler name to a trigger key and register an
`ITaskTriggerSource` that can enable and watch those bindings. In both cases,
document the operation or trigger in the module page under
[docs/modules/](modules/). Do not model ordinary C# language constructs as
module operations; write the task with normal C# control flow and reserve
module steps for real SharpClaw capabilities.

The full authoring walkthrough — interfaces, registration, lifecycle, and
testing — lives in [Module-Creation-Guide.md](guides/Module-Creation-Guide.md).

---

## REST API

> **Base URL:** `http://127.0.0.1:48923`
> **Auth:** `X-Api-Key` header on every request.

### Definitions

| Method | Path | Purpose |
|--------|------|---------|
| `POST` | `/tasks/validate` | Parse and validate without saving. |
| `POST` | `/tasks` | Register a new definition. |
| `GET`  | `/tasks` | List definitions. |
| `GET`  | `/tasks/{id}` | Get a single definition. |
| `PUT`  | `/tasks/{id}` | Update source or active state. |
| `DELETE` | `/tasks/{id}` | Delete a definition; running instances are cancelled. |
| `GET`  | `/tasks/{id}/preflight` | Run requirement checks without creating an instance. |
| `GET`  | `/tasks/trigger-sources` | List trigger sources currently available on the host. |

### Instances

| Method | Path | Purpose |
|--------|------|---------|
| `POST` | `/tasks/{id}/instances` | Create (and optionally start) an instance. |
| `GET`  | `/tasks/{id}/instances` | List instances for a definition. |
| `GET`  | `/tasks/{id}/instances/{iid}` | Get a single instance. |
| `POST` | `/tasks/{id}/instances/{iid}/start` | Start a Queued or resume a Paused instance. |
| `POST` | `/tasks/{id}/instances/{iid}/cancel` | Hard cancel via CancellationToken. |
| `POST` | `/tasks/{id}/instances/{iid}/stop` | Graceful stop via orchestrator. |
| `GET`  | `/tasks/{id}/instances/{iid}/outputs?since=...` | Poll output entries since a timestamp. |
| `GET`  | `/tasks/{id}/instances/{iid}/stream` | SSE stream of Output / Log / StatusChange / Done. |

### Triggers and shortcuts

| Method | Path | Purpose |
|--------|------|---------|
| `POST` | `/tasks/{id}/triggers/enable` | Enable all persisted trigger bindings. |
| `POST` | `/tasks/{id}/triggers/disable` | Disable bindings without deleting them. |
| `POST` | `/tasks/{id}/shortcuts/install` | Install OS launcher shortcuts declared by the task. |
| `DELETE` | `/tasks/{id}/shortcuts` | Remove installed shortcuts. |

### Diagnostic codes

Validation diagnostics (run on register/update):

| Code | Meaning |
|------|---------|
| `TASK101` | Invalid parameter type. |
| `TASK102` | Invalid data-type property. |
| `TASK103` | More than one `[Output]` class. |
| `TASK104` | Variable redeclared in the same scope. |
| `TASK105` | Variable declaration uses an invalid type. |
| `TASK106` | foreach missing iteration variable. |
| `TASK107` | foreach missing source expression. |
| `TASK108` | ParseResponse<T> references an unknown type. |

Compilation diagnostics (run on instance start):

| Code | Meaning |
|------|---------|
| `TASK201` | Required parameter not supplied. |
| `TASK202` | Supplied parameter value cannot be converted to the declared type. |
| `TASK203` | Declared default value cannot be converted to the declared type. |

Requirement and trigger diagnostics use `TASK4xx` codes.

---

## CLI reference

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
