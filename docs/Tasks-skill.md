SharpClaw Tasks — Agent Skill Reference

Full human-readable reference: Tasks-documentation.md
All bodies JSON. Enums as strings. Timestamps ISO 8601.

────────────────────────────────────────
TASK DEFINITIONS
────────────────────────────────────────
POST   /tasks                          { sourceText }  → TaskDefinitionResponse
GET    /tasks                          → TaskDefinitionResponse[]
GET    /tasks/{id}                     → TaskDefinitionResponse
PUT    /tasks/{id}                     { sourceText?, isActive? }  → TaskDefinitionResponse
DELETE /tasks/{id}                     → 204

TaskDefinitionResponse fields:
  id, name, description?, outputTypeName?, isActive, createdAt, updatedAt, customId?,
  parameters[]: { name, typeName, description?, defaultValue?, isRequired }

────────────────────────────────────────
TASK INSTANCES
────────────────────────────────────────
POST   /tasks/{id}/instances           { channelId?, parameterValues? }  → TaskInstanceResponse
GET    /tasks/{id}/instances           → TaskInstanceResponse[]
GET    /tasks/{id}/instances/{iid}     → TaskInstanceResponse (channelCost populated if bound to channel)
POST   /tasks/{id}/instances/{iid}/start    → 204  (Queued → Running, or Paused → Running)
POST   /tasks/{id}/instances/{iid}/cancel   → 204  (hard cancel via CancellationToken)
POST   /tasks/{id}/instances/{iid}/stop     → 204  (graceful stop via orchestrator)
GET    /tasks/{id}/instances/{iid}/outputs?since={datetime}  → TaskOutputEntryResponse[]
GET    /tasks/{id}/instances/{iid}/stream   → SSE text/event-stream

Statuses: Queued, Running, Paused, Completed, Failed, Cancelled

TaskInstanceResponse fields:
  id, taskDefinitionId, taskName, status, outputSnapshotJson?, errorMessage?,
  logs[]: { message, level, timestamp },
  createdAt, startedAt?, completedAt?, channelId?, channelCost?

TaskOutputEntryResponse fields:
  id, sequence, data?, timestamp

────────────────────────────────────────
SSE STREAMING
────────────────────────────────────────
Connect: GET /tasks/{id}/instances/{iid}/stream
Each frame: data:{type, sequence?, timestamp, data?}

Event types:
  Output       — Emit or ChatStream produced output
  Log          — Log step wrote a message
  StatusChange — Instance status changed
  Done         — Terminal status reached; payload is final TaskInstanceResponse

────────────────────────────────────────
PERMISSIONS
────────────────────────────────────────
CanManageTasks         — create, update, delete definitions
CanExecuteTasks        — start, stop, pause, resume instances
CanInvokeTasksAsTool   — see active definitions as agent tool schemas

────────────────────────────────────────
AGENT TOOL EXPOSURE
────────────────────────────────────────
Active task definitions are exposed as tools to agents with CanInvokeTasksAsTool.
Tool name pattern: task_invoke__{task-name}
Example: task registered as "summarise" → tool task_invoke__summarise
Parameters are mapped to JSON Schema from the definition's parameter list.
Invoking the tool creates and starts an instance automatically.

────────────────────────────────────────
SCRIPT LANGUAGE — QUICK REFERENCE
────────────────────────────────────────
One public class per script. Entry point: public async Task RunAsync(CancellationToken ct).
Public properties = parameters. Inner classes = data types.

Attributes:
  [Task("name")]               — required; registers the task name
  [Description("...")]        — optional; stored in definition + agent schema
  [ToolCall("name")]           — marks a public method as an inline agent tool
  [AgentOutput("json|md|text")]— hints return-value format to the calling agent
  [Output]                     — marks one inner class as the structured output type

Allowed constructs:
  var x = expr;  |  TypeName x = expr;     variable declaration
  x = expr;                                assignment
  if (cond) { } else if (cond) { } else { }
  foreach (var item in collection) { }
  while (cond) { }
  await expr;
  return;  |  return expr;

Built-in step methods (all async, all propagate ct):
  await Chat(agent, message)                   — full response from agent
  await ChatStream(agent, message)             — stream response to SSE channel
  await ChatToThread(agent, threadId, message) — chat into a named thread
  await FindModel(nameOrId)                    — resolve a model
  await FindProvider(nameOrId)                 — resolve a provider
  await FindAgent(nameOrId)                    — resolve an agent
  await CreateAgent(name, modelId)             — create agent at runtime
  await CreateThread(channelId, name)          — create thread at runtime
  await Emit(value)                            — push output to SSE; updates outputSnapshotJson
  await ParseResponse<T>(text)                 — parse agent text into typed T
  await HttpGet(url)                           — HTTP GET → string body
  await HttpPost(url, body)                    — HTTP POST with JSON body string
  await HttpPut(url, body)                     — HTTP PUT with JSON body string
  await HttpDelete(url)                        — HTTP DELETE
  await Delay(ms)                              — pause for ms milliseconds
  await WaitUntilStopped()                     — block until cancelled (daemon pattern)
  await Log(message)                           — write to instance logs and SSE

Allowed parameter/variable types:
  string, int, long, float, double, decimal, bool, Guid, DateTime, DateTimeOffset
  nullable variants (e.g. string?, int?)
  List<T>, IList<T>, IEnumerable<T>, ICollection<T> where T is a primitive or data type
  task-defined inner classes

────────────────────────────────────────
DIAGNOSTIC CODES
────────────────────────────────────────
Validation (run on register/update):
  TASK101  parameter has invalid type
  TASK102  data type property has invalid type
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

────────────────────────────────────────
SCHEDULING
────────────────────────────────────────
Tasks can be launched on a schedule via ScheduledJobDB.
Fields: TaskDefinitionId, ParameterValuesJson, CallerAgentId.
Scheduler calls TaskService.CreateInstanceAsync + orchestrator start.
Manage scheduled jobs via the scheduler endpoints in the Core API.

────────────────────────────────────────
QUICK PATTERNS
────────────────────────────────────────
One-shot:   set parameterValues at launch; instance completes and exits.
Daemon:     call await WaitUntilStopped() at end of RunAsync; stop via POST .../stop.
Structured output: mark one inner class [Output]; call await Emit(value) at end.
Agent-invokable: ensure isActive=true and agent role has CanInvokeTasksAsTool.
