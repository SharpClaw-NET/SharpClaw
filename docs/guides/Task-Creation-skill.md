SharpClaw Task Creation - Agent Skill Reference

Full guide:                guides/Task-Creation-Guide.md
Root task reference:       Tasks-documentation.md
Module authoring guide:    guides/Module-Creation-Guide.md

Ordinary C# task syntax is built into the task host: declarations, assignment,
control flow, return, logging, delay, structured response parsing, and
cancellation waits are not module-defined syntax. Modules contribute callable
operations and trigger attributes at startup. The exact set of module methods
callable inside RunAsync, and the exact set of trigger attributes recognised on
a task class, depend on which modules are loaded. For per-installation
specifics, consult the module pages under docs/modules/ and the live endpoint
GET /tasks/trigger-sources.

----------------------------------------
WHAT A TASK SCRIPT IS
----------------------------------------
A C# class posted to the API at runtime. One public class per script.
Entry point: public async Task RunAsync(CancellationToken ct)
Public properties = parameters. Inner classes = data types.
Task name comes from [Task("name")] on the class, not the class name.

Definition = stored script. Instance = one execution of that definition.

----------------------------------------
REGISTERING AND RUNNING
----------------------------------------
Validate (no store):   POST /tasks/validate  { sourceText }  -> { isValid, diagnostics[] }
Register:              POST /tasks           { sourceText }  -> TaskDefinitionResponse
Update script:         PUT  /tasks/{id}      { sourceText?, isActive? }
Delete:                DELETE /tasks/{id}
List:                  GET  /tasks
Get:                   GET  /tasks/{id}

Create instance:       POST /tasks/{id}/instances  { channelId?, parameterValues? }
Start instance:        POST /tasks/{id}/instances/{iid}/start
Stop (graceful):       POST /tasks/{id}/instances/{iid}/stop
Cancel (hard):         POST /tasks/{id}/instances/{iid}/cancel
Stream output (SSE):   GET  /tasks/{id}/instances/{iid}/stream
Get outputs:           GET  /tasks/{id}/instances/{iid}/outputs?since={datetime}

Statuses: Queued, Running, Paused, Completed, Failed, Cancelled

----------------------------------------
PARAMETERS
----------------------------------------
Every public property is a parameter.
No default + not nullable = required (missing -> TASK201 compile error).
Nullable or has default = optional.

Supported types: string, int, long, float, double, decimal, bool, Guid,
  DateTime, DateTimeOffset, nullable variants, List<T>, task-defined inner classes.

----------------------------------------
ATTRIBUTES - CLASS LEVEL (UNIVERSAL)
----------------------------------------
[Task("name")]                          required; canonical task name
[Description("...")]                    stored in definition + agent schemas
[RequiresProvider("Name")]              preflight: provider must exist with usable key
[RequiresModelCapability("Capability")] preflight: at least one model with capability
[RequiresModel("NameOrId")]             preflight: specific model must exist
[RequiresModule("module-id")]           preflight error if module not loaded
[RecommendsModule("module-id")]         preflight warning only (non-blocking)
[RequiresPlatform(TaskPlatform.X)]      restrict to host platform
[RequiresPermission("Key")]             caller must have permission/global flag
[ConcurrencyPolicy(TriggerConcurrency.X)]  SkipIfRunning | QueueNext | ForceNew

Trigger attributes (Schedule, OnEvent, OnFileChanged, OnWebhook, OnHotkey,
OsShortcut, OnQueryReturnsRows, etc.) are contributed by modules. Discover the
ones available on your host through docs/modules/ and GET /tasks/trigger-sources.

----------------------------------------
ATTRIBUTES - MEMBER LEVEL
----------------------------------------
[Output]                    on inner class: structured output type (max one per script)
[ToolCall("name")]          on public method: exposed as inline agent tool during execution
[AgentOutput("x")]          on method: output hint to model; values: json, markdown, text
[ModelId]                   on property: marks as model lookup for preflight capability checks
[RequiresCapability("X")]   on [ModelId] property: model must have this capability

----------------------------------------
ALLOWED RunAsync CONSTRUCTS
----------------------------------------
var x = expr;  |  TypeName x = expr;     variable declaration
x = expr;                                assignment
if (cond) { } else if (cond) { } else { }
foreach (var item in collection) { }
while (cond) { }
await expr;
return;  |  return expr;
calls to module methods registered by a loaded module

General-purpose C# (reflection, P/Invoke, System.IO, arbitrary type calls)
is NOT permitted.

----------------------------------------
MODULE METHODS AND TRIGGER ATTRIBUTES
----------------------------------------
The set of method names callable inside RunAsync, and the set of trigger
attributes recognised on the class, comes from the module parser extensions
loaded at startup. Discover what is available on the running host:

  GET /tasks/trigger-sources         (active trigger sources)
  task trigger-sources               (CLI equivalent)
  module list                        (loaded modules)
  module get <id>                    (per-module surface)

For the authoritative list of module methods and trigger attributes for a
specific module, consult its page under docs/modules/.

If your task uses a module method or trigger contributed by a module, prefer
[RequiresModule("module-id")] so preflight tells the operator exactly which
module to enable.

----------------------------------------
OUTPUT
----------------------------------------
Declare one nested class with [Output] for structured output.
The most recent emitted value of that type persists as outputSnapshotJson on
the instance and is also broadcast on the SSE stream.
Retrieve: GET /tasks/{id}/instances/{iid}  -> outputSnapshotJson field.

----------------------------------------
PREFLIGHT
----------------------------------------
GET /tasks/{id}/preflight?param.Name=value  -> { isBlocked, findings[] }
findings[]: { requirementKind, severity, passed, message, parameterName? }
severity Error = blocks instance creation. Warning = advisory only.
Validate syntax only (no store): POST /tasks/validate { sourceText }

----------------------------------------
TRIGGERS
----------------------------------------
Enable:    POST /tasks/{id}/triggers/enable   -> { enabled }
Disable:   POST /tasks/{id}/triggers/disable  -> { disabled }
List sources: GET /tasks/trigger-sources      -> TaskTriggerSourceResponse[]

Module-owned trigger attributes will not fire if the owning module is not
loaded. The definition still saves; the binding only activates once the
owning module is enabled. Module ownership is documented per-module under
docs/modules/.

OS shortcuts: POST /tasks/{id}/shortcuts/install  |  DELETE /tasks/{id}/shortcuts

----------------------------------------
AGENT TOOL EXPOSURE
----------------------------------------
Active definitions are exposed to agents with CanInvokeTasksAsTool.
Tool name: ao_invoke_task
Pass taskId or taskName, plus optional parameters keyed by parameter name.
Invoking the tool creates and starts an instance automatically.
Set active: PUT /tasks/{id} { "isActive": true }

----------------------------------------
PERMISSIONS
----------------------------------------
CanManageTasks        create, update, delete definitions
CanExecuteTasks       start, stop, cancel instances
CanInvokeTasksAsTool  see active definitions as agent tool schemas

----------------------------------------
DIAGNOSTIC CODES
----------------------------------------
Validation (run on register/update):
  TASK101  parameter has invalid type
  TASK102  data-type property has invalid type
  TASK103  more than one [Output] class declared
  TASK104  variable declared more than once in scope
  TASK105  variable declaration uses invalid type
  TASK106  foreach missing iteration variable
  TASK107  foreach missing source expression
  TASK108  ParseResponse<T> references unknown type

Compilation (run on instance start):
  TASK201  required parameter not supplied
  TASK202  supplied parameter value cannot convert to declared type
  TASK203  declared default value cannot convert to declared type

Requirement / trigger diagnostics use TASK4xx codes.

----------------------------------------
QUICK PATTERNS
----------------------------------------
One-shot:           set parameterValues at launch; instance completes and exits.
Daemon:             use a long-running step contributed by a module (e.g. a
                    wait-until-stopped helper); stop via POST .../stop.
Structured output:  mark one inner class [Output]; emit a value of that type
                    using the module-provided emit step.
Agent-invokable:    isActive=true and agent role has CanInvokeTasksAsTool.

----------------------------------------
TROUBLESHOOTING
----------------------------------------
Step method not recognised
  The owning module is not loaded. Run module list and enable the module.

Trigger never fires
  1. Check triggers[].isEnabled on GET /tasks/{id}.
  2. Confirm the owning module is loaded: task trigger-sources / module list.
  3. For cron, verify with task schedule preview "<expr>" [--timezone <tz>].

Preflight blocks with module finding
  [RecommendsModule] is advisory only. An Error finding from [RequiresModule]
  means the module is not loaded - enable it with module enable <id>.

Required parameter missing - TASK201
  Supply via parameterValues. Check parameters[] (isRequired=true) on the
  definition.

Output snapshot empty
  No emit was called, or the emitted value's type does not match the [Output]
  class.
