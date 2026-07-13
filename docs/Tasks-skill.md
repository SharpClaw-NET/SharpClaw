SharpClaw Tasks - Agent Skill Reference

Full human-readable reference: Tasks-documentation.md
Module authoring reference:    guides/Module-Creation-Guide.md
All bodies JSON. Enums as strings. Timestamps ISO 8601.

Ordinary C# task syntax is built into the task host: declarations, assignment,
control flow, return, logging, delay, structured response parsing, and
cancellation waits are not module-defined syntax. Modules contribute callable
operations and trigger attributes at startup. The exact set of module methods
callable inside RunAsync, and the exact set of trigger attributes recognised on
a task class, depend on which modules are loaded. For per-installation
specifics, consult the module pages under docs/modules/ and the live endpoint
GET /tasks/trigger-sources.

----------------------------------------
TASK DEFINITIONS
----------------------------------------
POST   /tasks/validate                 { sourceText }                          -> { isValid, diagnostics[] }
POST   /tasks                          { sourceText }                          -> TaskDefinitionResponse
GET    /tasks                                                                  -> TaskDefinitionResponse[]
GET    /tasks/{id}                                                             -> TaskDefinitionResponse
PUT    /tasks/{id}                     { sourceText?, isActive? }              -> TaskDefinitionResponse
DELETE /tasks/{id}                                                             -> 204
GET    /tasks/{id}/preflight?param.Name=value                                  -> TaskPreflightResponse
GET    /tasks/trigger-sources                                                  -> TaskTriggerSourceResponse[]
POST   /tasks/{id}/triggers/enable                                             -> { enabled }
POST   /tasks/{id}/triggers/disable                                            -> { disabled }
POST   /tasks/{id}/shortcuts/install                                           -> 204
DELETE /tasks/{id}/shortcuts                                                   -> 204

TaskDefinitionResponse:
  id, name, description?, outputTypeName?, isActive, createdAt, updatedAt, customId?
  parameters[]:    { name, typeName, description?, defaultValue?, isRequired }
  requirements[]:  { kind, severity, value?, capabilityValue?, parameterName? }
  triggers[]:      { kind, triggerValue?, filter?, isEnabled }

TaskPreflightResponse:
  isBlocked, findings[]: { requirementKind, severity, passed, message, parameterName? }

TaskTriggerSourceResponse:
  sourceName?, supportedKinds[], type, isCustom

----------------------------------------
TASK INSTANCES
----------------------------------------
POST   /tasks/{id}/instances           { channelId?, parameterValues? }        -> TaskInstanceResponse
GET    /tasks/{id}/instances                                                   -> TaskInstanceResponse[]
GET    /tasks/{id}/instances/{iid}                                             -> TaskInstanceResponse (channelCost populated if bound to channel)
POST   /tasks/{id}/instances/{iid}/start                                       -> 204  (Queued -> Running, or Paused -> Running)
POST   /tasks/{id}/instances/{iid}/cancel                                      -> 204  (hard cancel via CancellationToken)
POST   /tasks/{id}/instances/{iid}/stop                                        -> 204  (graceful stop via orchestrator)
GET    /tasks/{id}/instances/{iid}/outputs?since={datetime}                    -> TaskOutputEntryResponse[]
GET    /tasks/{id}/instances/{iid}/stream                                      -> SSE text/event-stream

Statuses: Queued, Running, Paused, Completed, Failed, Cancelled

TaskInstanceResponse:
  id, taskDefinitionId, taskName, status, outputSnapshotJson?, errorMessage?
  logs[]: { message, level, timestamp }
  createdAt, startedAt?, completedAt?, channelId?, channelCost?

TaskOutputEntryResponse:
  id, sequence, data?, timestamp

----------------------------------------
SSE STREAMING
----------------------------------------
Connect: GET /tasks/{id}/instances/{iid}/stream
Each frame: data:{ type, sequence?, timestamp, data? }

Event types:
  Output       - an output step was executed
  Log          - a log step wrote a message
  StatusChange - instance status changed
  Done         - terminal status reached; payload is final TaskInstanceResponse

----------------------------------------
PERMISSIONS
----------------------------------------
CanManageTasks         create, update, delete definitions
CanExecuteTasks        start, stop, pause, resume instances
CanInvokeTasksAsTool   see active definitions as agent tool schemas

----------------------------------------
AGENT TOOL EXPOSURE
----------------------------------------
Active task definitions are exposed as tools to agents with CanInvokeTasksAsTool.
Tool name: ao_invoke_task
Pass taskId or taskName, plus optional parameters keyed by parameter name.
Invoking the tool creates and starts an instance automatically.

----------------------------------------
SCRIPT SHAPE
----------------------------------------
One public class per script. Entry point: public async Task RunAsync(CancellationToken ct).
Public properties = parameters. Inner classes = data types.
Task name comes from [Task("name")] on the class, not the class name.

Universal class-level attributes:
  [Task("name")]                          required; canonical task name
  [Description("...")]                    stored in definition + agent schema
  [RequiresProvider("Name")]              preflight: provider must exist with usable key
  [RequiresModelCapability("Capability")] preflight: at least one model with capability
  [RequiresModel("NameOrId")]             preflight: specific model must exist
  [RequiresModule("module-id")]           preflight error if module not loaded
  [RecommendsModule("module-id")]         preflight warning only (non-blocking)
  [RequiresPlatform(TaskPlatform.X)]      restrict to host platform
  [RequiresPermission("Key")]             caller must have permission/global flag
  [ConcurrencyPolicy(TriggerConcurrency.X)]  SkipIfRunning | QueueNext | ForceNew

Member-level attributes:
  [Output]                    on inner class: marks structured output type (max one per script)
  [ToolCall("name")]          on public method: exposed as inline agent tool during execution
  [AgentOutput("x")]          on method: output hint to model; values: json, markdown, text
  [ModelId]                   on property: marks as model lookup for preflight capability checks
  [RequiresCapability("X")]   on [ModelId] property: model must have this capability

Allowed RunAsync constructs:
  var x = expr;  |  TypeName x = expr;     variable declaration
  x = expr;                                assignment
  if (cond) { } else if (cond) { } else { }
  foreach (var item in collection) { }
  while (cond) { }
  await expr;
  return;  |  return expr;
calls to module methods registered by a loaded module

Allowed parameter / variable types:
  string, int, long, float, double, decimal, bool, Guid, DateTime, DateTimeOffset
  nullable variants
  List<T>, IList<T>, IEnumerable<T>, ICollection<T> where T is primitive or task-defined
  inner classes declared in the same script

----------------------------------------
MODULE METHODS AND TRIGGER ATTRIBUTES
----------------------------------------
Module methods and triggers are module-owned. The set of method names callable
inside RunAsync, and the set of [On...] / [Schedule] / [Os...] / [OnTrigger]
style attributes recognised on the class, comes from the module parser
extensions that are loaded at startup. To discover what is available on the
running host:

  GET /tasks/trigger-sources         (active trigger sources)
  task trigger-sources               (CLI equivalent)
  module list                        (loaded modules)
  module get <id>                    (per-module surface)

For the authoritative list of module methods and trigger attributes for any
specific module, consult its page under docs/modules/.

----------------------------------------
MODULE EXTENSION POINTS (high level)
----------------------------------------
Modules contribute the task operation and trigger surface through three interfaces. Full authoring
walkthrough lives in guides/Module-Creation-Guide.md.

  ITaskParserModuleExtension
    StepKeyMappings:           method name -> (runtime dispatch key, module id)
    EventTriggerMappings:      trigger attribute -> (trigger key, module id)
    SingleArgExpressionMethods: method names whose first arg is an expression

  ITaskStepExecutorExtension
    ModuleId
    CanExecute(stepKey)
    ExecuteAsync(stepKey, context, arguments, expression, resultVariable)

  ITaskTriggerSource (registered as ITaskTriggerSourceProvider)
    TriggerKeys
    EnableTriggerAsync(definition, ct)
    DisableTriggerAsync(taskId, ct)

TaskStepRegistry is the shared method-name <-> runtime-key index, populated
from parser extensions at startup. Runtime keys should be namespaced
{module_id}.{step_name} to avoid collisions.

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

Requirement / trigger diagnostics use TASK4xx codes for invalid declarations.

----------------------------------------
PREFLIGHT, SCHEDULING, TRIGGERS
----------------------------------------
Preflight:
  task preflight <taskId> [--param key=value ...]

Scheduling:
  task schedule list
  task schedule get <jobId>
  task schedule create <taskId> --cron <expr> [--timezone <tz>] [--name <n>]
  task schedule update <jobId> --cron <expr> [--timezone <tz>]
  task schedule pause <jobId>
  task schedule resume <jobId>
  task schedule delete <jobId>
  task schedule preview <expr> [--timezone <tz>] [--count N]

Triggers and shortcuts:
  task trigger-sources
  task triggers enable <taskId>
  task triggers disable <taskId>
  task shortcuts install <taskId>
  task shortcuts remove <taskId>

----------------------------------------
QUICK PATTERNS
----------------------------------------
One-shot:           set parameterValues at launch; instance completes and exits.
Daemon:             use a long-running step (such as a wait-until-stopped helper
                    contributed by a module); stop via POST .../stop.
Structured output:  mark one inner class [Output]; emit a value of that type
                    using the module-provided emit step.
Agent-invokable:    isActive=true and agent role has CanInvokeTasksAsTool.
