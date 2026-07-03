# Creating a SharpClaw Task

> **Reference:** [../Tasks-documentation.md](../Tasks-documentation.md)
> **Skill version:** [Task-Creation-skill.md](Task-Creation-skill.md)
> **Module authoring:** [Module-Creation-Guide.md](Module-Creation-Guide.md)

This guide walks through writing, registering, running, and debugging
SharpClaw tasks. It focuses on the parts of the task system that are
universal: the script shape, parameters, requirements, preflight, the
execution lifecycle, output, and how triggers and module operations slot in.

Ordinary C# task syntax is fixed by the task host. The actual catalogue of
module methods you can call inside `RunAsync`, and the catalogue of trigger
attributes you can declare on the task class, are contributed by modules. To
find out what is available on your installation, consult the module pages under
[../modules/](../modules/) and the live endpoint
`GET /tasks/trigger-sources`.

---

## Table of contents

- [What a task is](#what-a-task-is)
- [Your first task](#your-first-task)
- [Parameters](#parameters)
- [Structured output](#structured-output)
- [Requirements and preflight](#requirements-and-preflight)
- [Triggers in general](#triggers-in-general)
- [Concurrency policy](#concurrency-policy)
- [Tool-call methods](#tool-call-methods)
- [Agent tool exposure](#agent-tool-exposure)
- [Discovering module methods and triggers](#discovering-module-methods-and-triggers)
- [Debugging and troubleshooting](#debugging-and-troubleshooting)

---

## What a task is

A task is a C# script registered in SharpClaw at runtime through the API or
CLI. You don't change application code and you don't touch the solution â€” you
write a single class and post it. The task system parses it, validates it,
and stores the definition. When you run it, the system compiles the
definition with the supplied parameter values and hands the plan to the
orchestrator.

A task definition and a task instance are distinct. A definition is the
stored script. An instance is one execution of it â€” with its own status,
logs, output, and lifecycle.

Module methods and triggers are registered by modules at startup. The precise
calls available inside `RunAsync` and the precise attributes recognised on the
class come from whichever modules are loaded.

---

## Your first task

### Write the script

```csharp
[Task("hello")]
[Description("Asks an agent to say hello and logs the response.")]
public class HelloTask
{
    public string AgentName { get; set; } = "Assistant";

    public async Task RunAsync(CancellationToken ct)
    {
        // Step calls inside RunAsync are contributed by loaded modules.
        // See module docs for the calls available on your installation.
    }
}
```

The class name does not matter. The canonical name is whatever you pass to
`[Task]`.

### Register it

Via CLI:

```
task create hello.cs
```

Via API:

```http
POST /tasks
Content-Type: application/json

{ "sourceText": "<the script text>" }
```

The response gives you the task `id`. The definition is now stored.

### Run it

```
task run hello --param AgentName=Assistant
```

Via API â€” create an instance, then start it:

```http
POST /tasks/{id}/instances
{ "parameterValues": { "AgentName": "Assistant" } }

POST /tasks/{id}/instances/{iid}/start
```

### Watch the output

```
task listen {iid}
```

Or stream over HTTP:

```http
GET /tasks/{id}/instances/{iid}/stream
Accept: text/event-stream
```

Each SSE frame has a `type` field: `Output`, `Log`, `StatusChange`, `Done`.

---

## Parameters

Every public property on the class is a parameter. The name, type, and any
XML-doc summary are stored in the definition and surfaced in agent tool
schemas.

```csharp
[Task("report")]
public class ReportTask
{
    /// <summary>Channel to post results to.</summary>
    public Guid ChannelId { get; set; }

    /// <summary>Maximum items to return.</summary>
    public int Limit { get; set; } = 20;

    /// <summary>Optional keyword filter.</summary>
    public string? Filter { get; set; }

    public async Task RunAsync(CancellationToken ct) { }
}
```

- Properties **without a default and not nullable** are required at launch.
  Missing required parameters produce a `TASK201` error and block the
  instance.
- Nullable types (`string?`, `int?`) and properties with defaults are
  optional.

Supported types: `string`, `int`, `long`, `float`, `double`, `decimal`,
`bool`, `Guid`, `DateTime`, `DateTimeOffset`, nullable variants of all of
the above, `List<T>`, and inner data-type classes you define in the same
script.

---

## Structured output

Declare a nested class with `[Output]` to give the task a typed output
shape. The most recent value emitted of that type is persisted as
`outputSnapshotJson` on the instance and is also broadcast on the SSE stream.

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
        // Use whatever emit step is contributed by the module that owns it.
    }
}
```

Retrieve the final snapshot via `GET /tasks/{id}/instances/{iid}` â€”
`outputSnapshotJson` holds the last emitted value.

Only one class per script may carry `[Output]`. Declaring more produces a
`TASK103` validation error.

---

## Requirements and preflight

Declare runtime prerequisites on the class so misconfiguration is caught
before a broken instance is created:

```csharp
[Task("vision-caption")]
[RequiresProvider("OpenAI")]
[RequiresModelCapability("Vision")]
public class VisionCaptionTask
{
    [ModelId]
    [RequiresCapability("Vision")]
    public string Model { get; set; } = "gpt-4.1";

    public async Task RunAsync(CancellationToken ct) { }
}
```

Run a preflight check before launching â€” it tells you exactly what is
missing:

```
task preflight {id} --param Model=gpt-4.1
```

Via API:

```http
GET /tasks/{id}/preflight?param.Model=gpt-4.1
```

The response includes `isBlocked` and a list of `findings` with severity
and message for each requirement. `Error` findings block instance creation.
`Warning` findings (such as `[RecommendsModule]`) are advisory only.

If your task uses a step or trigger contributed by a module, prefer
`[RequiresModule("module-id")]` so preflight tells the operator exactly
which module to enable.

---

## Triggers in general

A trigger attribute on the class causes the task host to launch instances
automatically when an external condition is met. Cron-style triggers feed
into the scheduler; everything else feeds into the trigger host service.

```csharp
[Task("notify-on-change")]
[Schedule("0 */15 * * *", Timezone = "UTC")]
[ConcurrencyPolicy(TriggerConcurrency.SkipIfRunning)]
public class NotifyOnChangeTask
{
    public async Task RunAsync(CancellationToken ct) { }
}
```

The set of trigger attributes recognised on a task class is contributed by
modules. To see which attributes are available on your installation, consult
the module pages under [../modules/](../modules/) and check the live
trigger-source list:

```
task trigger-sources
```

Or via API:

```http
GET /tasks/trigger-sources
```

Enable persisted trigger bindings for a task definition:

```
task triggers enable {id}
```

Via API:

```http
POST /tasks/{id}/triggers/enable
```

If a task declares a trigger attribute owned by a module that is not
currently loaded, the definition still saves and validates, but the trigger
binding will not become active until the owning module is enabled. Preflight
will recommend enabling the owning module.

---

## Concurrency policy

`ConcurrencyPolicy` controls what happens when a trigger fires while a prior
instance is still running:

```csharp
[ConcurrencyPolicy(TriggerConcurrency.SkipIfRunning)]
```

| Value | Behaviour |
|-------|-----------|
| `SkipIfRunning` | Drop the new fire. |
| `QueueNext` | Queue exactly one follow-up instance. |
| `ForceNew` | Always start a new instance. |

---

## Tool-call methods

A `[ToolCall]` method is exposed to agents as an inline tool while the task
instance is running. Agents can call it by name during any chat that has
access to the running instance:

```csharp
[Task("monitor")]
public class MonitorTask
{
    private bool _healthy = true;

    [ToolCall("get_health")]
    [AgentOutput("json")]
    public async Task<string> GetHealthAsync()
    {
        return _healthy ? """{"healthy":true}""" : """{"healthy":false}""";
    }

    [ToolCall("set_unhealthy")]
    public async Task SetUnhealthyAsync()
    {
        _healthy = false;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // Keep the instance alive using a wait step contributed by a module.
    }
}
```

`[AgentOutput("json")]` hints to the agent that the return value is JSON.
Other values: `"markdown"`, `"text"`.

---

## Agent tool exposure

Active task definitions can be started by agents that have the
`CanInvokeTasksAsTool` permission. Agent Orchestration exposes the static
`ao_invoke_task` tool; the agent passes either `taskId` or `taskName`, plus
an optional `parameters` object keyed by task parameter name.

When an agent invokes the tool, an instance is created and started
automatically after the normal module tool permission check passes.

Activate a definition so it appears in agent tool schemas:

```
task activate {id}
```

---

## Discovering module methods and triggers

Because the step and trigger surface is module-owned, the way to find out
what your scripts can actually do is to look at the modules currently
loaded on your host:

```
module list
module get <module-id>
task trigger-sources
```

Then read the corresponding module page under [../modules/](../modules/) for
the exact module methods and trigger attributes that module owns, plus any
permissions, resources, or configuration they require.

If you are authoring a module yourself and want to add new task-callable methods or
triggers, see [Module-Creation-Guide.md](Module-Creation-Guide.md). The
high-level extension points are:

- `ITaskParserModuleExtension` - declare the method names and trigger
  attributes your module recognises and the namespaced runtime keys they map to.
- `ITaskStepExecutorExtension` - implement execution for your module's
  registered task-callable methods.
- `ITaskTriggerSource` (registered as `ITaskTriggerSourceProvider`) â€”
  implement the runtime watcher behind your trigger attributes.

---

## Debugging and troubleshooting

**Validation error on register â€” `TASK101` / `TASK102`**
The script has a syntax or attribute error. The `diagnostics[]` array in
the `POST /tasks` response lists the exact line and message.

**Instance stuck in `Queued`**
The orchestrator queue may be busy, or the instance was created without
`channelId` and never started. Call `POST .../start` or check
`GET /tasks/{id}/instances/{iid}` for `errorMessage`.

**Instance status is `Failed`**
Check the instance `logs[]` array â€” step-level errors are logged there.
`errorMessage` on the instance carries the top-level failure reason.

**Required parameter not found â€” `TASK201`**
A required parameter was not supplied in `parameterValues`. Check the
definition's `parameters[]` for required fields (`isRequired: true`).

**Trigger never fires**
1. Confirm `triggers[].isEnabled` is `true` on `GET /tasks/{id}`.
2. Confirm the owning module is loaded:
   `task trigger-sources` and `module list`.
3. For scheduled tasks, verify the cron expression with
   `task schedule preview "<expr>" [--timezone <tz>]`.

**Preflight blocks with a module finding**
`[RecommendsModule]` is advisory â€” it will not block instance creation.
An `Error`-severity module finding means you used `[RequiresModule]` and
the module is not loaded. Enable it with `module enable <id>`.

**Output snapshot is empty**
Either no value was emitted, or the emitted value's type does not match
the `[Output]` class. Check that the inner class carries `[Output]` and
that an emit step is called at least once before the task ends.

**A step method is unrecognised**
The module that owns it is not loaded. Check `module list` and enable the
module, or pick an equivalent step from a module that is loaded.
