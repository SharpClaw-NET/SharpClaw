<![CDATA[# Jobs & Tasks

Learn how agents execute background actions and how tasks automate complex workflows.

## Jobs

A **job** is a background action submitted by an agent. When an agent calls a tool that requires a permission-controlled action (such as running a shell command or clicking on the desktop), SharpClaw creates a job to track and manage that execution.

### Job Lifecycle

1. **Queued**: Job is created and waiting to start
2. **Executing**: Action is in progress
3. **AwaitingApproval**: Action requires user approval (when clearance is **PendingApproval**)
4. **Completed**: Action finished successfully
5. **Failed**: Action encountered an error
6. **Denied**: User rejected the approval request
7. **Cancelled**: User manually stopped the job
8. **Paused**: Job execution is suspended (no token cost while paused)

### Viewing Jobs

In a channel, click the **[jobs]** tab to see:

- **Job list**: All jobs for this channel with status indicators
- **Job detail**: Click a job to view its execution log, screenshots, and results

### Job Detail View

When a job is selected:

- **Status**: Current state (Queued, Executing, Completed, etc.)
- **Action Type**: What the agent is doing (ExecuteAsSafeShell, AccessLocalhostInBrowser, etc.)
- **Resource**: Which resource is being accessed (shell environment, display device, etc.)
- **Log**: Real-time execution output with timestamps
- **Screenshots**: Captured images from desktop actions (ClickDesktop, TypeOnDesktop, CaptureDisplay)

### Job Actions

**Approve**: Grants permission for a job awaiting approval (status changes to Executing).

**Deny**: Rejects a job awaiting approval (status changes to Denied).

**Pause**: Suspends execution without cancellation — stops capture/inference, preserving state. No token cost while paused.

**Resume**: Continues a paused job with its original parameters.

**Cancel**: Stops a running job permanently (cannot be resumed).

### Job Submission

Jobs are created automatically when an agent calls a tool that maps to an agent action. For example, when an agent invokes `execute_shell`, SharpClaw creates an ExecuteAsSafeShell job. If the agent's clearance is **Independent**, the job executes immediately. Otherwise it enters **AwaitingApproval** until you approve or deny it.

### Default Resources

Channels and contexts can pre-assign resources so agents don't need to specify them every time:

1. Open channel (or context) settings
2. Go to **Default Resources** tab
3. Assign defaults for: safe shell, dangerous shell, container, website, search engine, local/external info store, audio device, agent, task, skill

When a job is submitted, the channel's defaults are used if the agent doesn't specify a resource. Defaults cascade: channel → context.

## Tasks

A **task** is a C# script that compiles and runs inside SharpClaw to automate complex workflows. Tasks can create agents, channels, and threads, send chat messages, make HTTP requests, and orchestrate multi-agent pipelines — all from a single script definition.

Tasks are fundamentally different from mk8.shell (which is a sandboxed command DSL used only by the ExecuteAsSafeShell agent action). Tasks are full automation scripts that operate at the SharpClaw application level.

### How Tasks Work

1. You write a C# task script with a `[Task("name")]` attribute and a `RunAsync` entry point
2. SharpClaw parses the script using **Roslyn** (the C# compiler) to extract metadata, parameters, and execution steps
3. The parsed script is validated and compiled into a **CompiledTaskPlan** — a tree of step definitions
4. The **TaskOrchestrator** executes the plan step-by-step, interpreting each step kind

This pipeline (parse → validate → compile → execute) ensures scripts are safe and structured before they run.

### Task Script Structure

A task script is a C# class with:

- **`[Task("name")]`** attribute: Names the task (used for tool registration)
- **`[Description("...")]`** attribute (optional): Describes what the task does
- **Public properties**: Become task parameters that can be passed at runtime
- **Nested public classes**: Define data types (mark one with `[Output]` for structured output)
- **`[ToolCall("name")]`** methods (optional): Custom tool hooks that agents can invoke during task chat
- **`public async Task RunAsync(CancellationToken ct)`**: The entry point — contains the automation steps

**Example — a task that creates an agent and chats with it:**

```csharp
[Task("setup_research_agent")]
[Description("Creates a research agent and asks it to summarize a topic")]
public class SetupResearchAgent
{
    public string Topic { get; set; }

    public async Task RunAsync(CancellationToken ct)
    {
        var modelId = await FindModel("gpt-4o");
        var agentId = await CreateAgent("Researcher", modelId,
            "You are a research assistant. Provide concise summaries.");

        var response = await Chat("Summarize: " + Topic, agentId);
        await Log("Research complete: " + response);
        await Emit(response);
    }
}
```

### Available Step Kinds

The task engine supports the following operations. Each maps to a method call in your `RunAsync` body:

**Agent Interaction**
- **Chat**: Send a message to an agent and await the full response
- **ChatStream**: Send a message and stream the response in real time
- **ChatToThread**: Send a message into a specific thread (for persistent conversations)

**Entity Creation & Lookup**
- **CreateAgent**: Create (or upsert) an agent — auto-added to the task's channel
- **CreateThread**: Create a new conversation thread in a channel
- **FindModel**: Look up a model by name or custom ID
- **FindProvider**: Look up a provider by name or custom ID
- **FindAgent**: Look up an agent by name or custom ID

**HTTP**
- **HttpRequest**: Make GET or POST requests to external APIs

**Output & Data**
- **Emit**: Push a result to SSE/WebSocket listeners and persist output snapshots
- **ParseResponse**: Extract JSON from an agent's text response into structured data
- **Log**: Write a message to the task execution log

**Control Flow**
- **Conditional**: If/else branching based on variable values
- **Loop**: While loops for repeated operations
- **Delay**: Pause execution for a specified duration (milliseconds)
- **WaitUntilStopped**: Block until the task is cancelled externally (useful for long-running listeners)
- **Return**: Exit the task early
- **EventHandler**: Register a callback for event triggers

**Variables**
- **DeclareVariable**: Create a local variable with an optional initializer
- **Assign**: Set a variable to a new value
- **Evaluate**: Evaluate a restricted C# expression

### Variables & Expressions

Tasks support variables that persist across steps:

- Variables are set by **DeclareVariable**, **Assign**, or as **ResultVariable** from steps like Chat, FindModel, CreateAgent
- **Variable substitution**: Reference variables by name in expressions — they are replaced with their current value
- **JSON property access**: Use `variableName.PropertyName` to extract fields from JSON-valued variables
- **String concatenation**: C# style `"text" + variable + "more text"` is supported

### Shared Data Store

Each running task instance has a **shared data store** that enables communication between the task orchestrator and agents during execution:

- **Light data**: A short text visible in chat headers (max 500 words) — ideal for status updates agents can reference
- **Big data**: Larger JSON payloads persisted per-instance — for structured data exchange
- Changes are persisted and logged automatically
- Agents read/write shared data through special task tool calls (`task_write_light_data`, `task_read_light_data`, etc.)

### Custom Tool Hooks

Tasks can define `[ToolCall("name")]` methods that become additional tools available to agents during task chat interactions. This lets agents call back into the task to trigger custom logic:

- Each hook has parameters extracted from the method signature
- The hook body executes task steps, and a return variable provides the result
- Tool definitions are built automatically and included in chat API requests

### Task Instances

Each task execution creates a **task instance** with:

- **Status**: Queued, Running, Completed, Failed, Cancelled
- **Log**: Timestamped execution output from each step
- **Start/End Time**: Duration tracking
- **Output Snapshots**: Data emitted via `Emit` steps
- **Shared Data Snapshots**: Light and big data state at completion
- **Channel Cost**: Token usage from all chat steps

View instances in the **[tasks]** tab → click a task → see the instance list.

### Output Streaming

Task output is streamed in real time via SSE (Server-Sent Events):

- **Output events**: Data pushed by `Emit` steps — the task controls content, format, and frequency
- **Log events**: Step execution messages
- **StatusChange events**: Queued → Running → Completed/Failed/Cancelled
- **Done event**: Signals the stream is complete

Connect to `/tasks/{taskId}/instances/{instanceId}/stream` for live updates.

### Task Awareness

Tasks are exposed to agents as tool schemas (e.g., `task_setup_research_agent`). Control which agents see which tasks via **Tool Awareness Sets** on channels or agents. See **Advanced Topics** for configuration.

### Task Permissions

Tasks require the **EditTask** permission:

- Agent role must grant **TaskManageAccesses** for the task
- Clearance level controls whether task execution requires approval

### Best Practices

- **Keep tasks focused**: One task = one automation workflow
- **Use meaningful names**: The `[Task("name")]` becomes the tool name agents see
- **Write clear descriptions**: The `[Description]` text helps agents understand when to invoke the task
- **Test with PendingApproval first**: Verify scripts work before granting **Independent** clearance
- **Use shared data for coordination**: Light data lets agents see task state in their chat context
- **Leverage CreateAgent for specialization**: Tasks can spin up purpose-built agents with tailored system prompts
- **Chain with ChatToThread**: Use threads for persistent multi-turn conversations within a task

## Supported Agent Actions

Agents can execute the following actions (each creates a job):

### Global Actions

1. **ExecuteAsSafeShell**: Run mk8.shell commands in a sandboxed environment
2. **UnsafeExecuteAsDangerousShell**: Run unrestricted shell commands
3. **CreateSubAgent**: Create a new agent
4. **CreateContainer**: Provision a container environment
5. **AccessLocalhostInBrowser**: Open localhost URL in headless Chromium browser
6. **AccessLocalhostCli**: Make HTTP GET request to localhost
7. **ClickDesktop**: Simulate mouse click at screen coordinates
8. **TypeOnDesktop**: Simulate keyboard input
9. **ManageAgent**: Modify an existing agent

### Resource-Specific Actions

10. **AccessContainer**: Execute commands in a container
11. **AccessWebsite**: Browse a specific website
12. **QuerySearchEngine**: Search via a configured engine
13. **AccessLocalInfoStore**: Query a local information store
14. **AccessExternalInfoStore**: Query an external API
15. **RegisterInfoStore**: Create a new information store

### Editor Actions

16–25. **Editor bridge actions** (Read/Write/Refactor/Execute/etc. via IDE extensions)

### Task Actions

26. **EditTask**: Modify a task definition

### Skill Actions

27. **AccessSkill**: Invoke a skill

### Display Actions

28. **CaptureDisplay**: Take a screenshot from a specific display

## Job Logs

All job output is captured in the log:

- **Timestamp**: When each log entry was created
- **Level**: Info, Warning, Error
- **Message**: Log text

Logs stream in real-time during execution and are persisted after completion.

## Job Screenshots

Desktop actions (ClickDesktop, TypeOnDesktop, CaptureDisplay) include follow-up screenshots:

- Displayed in the job detail view
- Embedded as base64 images
- Sent to the agent for visual confirmation

## Next Steps

Continue to **Bot Integrations** to connect external platforms.
]]>
