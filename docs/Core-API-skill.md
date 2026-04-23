SharpClaw Core API — Agent Skill Reference

Base: http://127.0.0.1:48923
Auth: X-Api-Key header on every request. Key at %LOCALAPPDATA%/SharpClaw/.api-key.
All bodies JSON. Enums serialised as strings. Timestamps ISO 8601.

────────────────────────────────────────
HEALTH
────────────────────────────────────────
GET /echo              → 200 (no auth needed)
GET /ping              → 200 (auth check)

────────────────────────────────────────
AUTH
────────────────────────────────────────
POST /auth/register                { username, password }  → { id, username }
POST /auth/login                   { username, password, rememberMe }  → { accessToken, accessTokenExpiresAt, refreshToken?, refreshTokenExpiresAt? }
POST /auth/refresh                 { refreshToken }  → same shape as login
POST /auth/invalidate-access-tokens   { userIds: [guid] }  → 204
POST /auth/invalidate-refresh-tokens  { userIds: [guid] }  → 204

────────────────────────────────────────
PROVIDERS
────────────────────────────────────────
POST   /providers                  { name, providerType, apiEndpoint?, apiKey? }
GET    /providers
GET    /providers/{id}
PUT    /providers/{id}             { name?, apiEndpoint? }
DELETE /providers/{id}
POST   /providers/{id}/set-key     { apiKey }  → 204
POST   /providers/{id}/sync-models → synced model list
POST   /providers/{id}/auth/device-code  → { userCode, verificationUri, expiresInSeconds }

ProviderType values: OpenAI, Anthropic, OpenRouter, GoogleVertexAI, GoogleGemini, ZAI, VercelAIGateway, XAI, Groq, Cerebras, Mistral, GitHubCopilot, Minimax, Custom, Local.

apiEndpoint required for Custom only. sync-models is the preferred way to add models.

────────────────────────────────────────
MODELS
────────────────────────────────────────
POST   /models                     { name, providerId, capabilities? }
GET    /models
GET    /models/{id}
PUT    /models/{id}                { name?, capabilities? }
DELETE /models/{id}

name must be the exact provider identifier (e.g. gpt-4o, whisper-1).
capabilities: flags — None=0, Chat=1, Transcription=2, ImageGeneration=4, Embedding=8, TextToSpeech=16, Vision=32. Default Chat.

────────────────────────────────────────
LOCAL MODELS
────────────────────────────────────────
POST   /models/local/download      { url, name?, quantization?, gpuLayers? }
GET    /models/local/download/list?url={url}
GET    /models/local
POST   /models/local/{id}/load     { gpuLayers?, contextSize? }
POST   /models/local/{id}/unload
DELETE /models/local/{id}

LocalModelStatus: Pending, Downloading, Ready, Failed.

────────────────────────────────────────
AGENTS
────────────────────────────────────────
POST   /agents                     { name, modelId, systemPrompt?, maxCompletionTokens?, customChatHeader?, toolAwarenessSetId? }
GET    /agents
GET    /agents/{id}
PUT    /agents/{id}                { name?, modelId?, systemPrompt?, maxCompletionTokens?, customChatHeader?, toolAwarenessSetId? }
DELETE /agents/{id}
PUT    /agents/{id}/role           { roleId }

maxCompletionTokens (integer|null): caps the number of tokens the model may generate per response. Sent as max_tokens, max_completion_tokens, or max_output_tokens depending on the provider/API version. null (default) = no limit (provider default). Useful for controlling response size and latency — smaller limits yield faster responses.

customChatHeader (string|null): template that replaces the default chat header for this agent. Uses {{tag}} placeholders expanded at send time. See CUSTOM CHAT HEADER section.

toolAwarenessSetId (guid|null): links a tool awareness set that controls which tool-call schemas are sent in API requests. See TOOL AWARENESS SETS section. Channel's set overrides the agent's; null = all tools enabled. On PUT, pass Guid.Empty to clear.

AgentResponse includes: id, name, systemPrompt, modelId, modelName, providerName, roleId, roleName, maxCompletionTokens, customChatHeader, toolAwarenessSetId.

AgentSummary (embedded in channel/context responses): id, name, modelId, modelName, providerName, roleId, roleName, maxCompletionTokens, toolAwarenessSetId. Same as AgentResponse minus systemPrompt.

────────────────────────────────────────
ROLES & PERMISSIONS
────────────────────────────────────────
GET    /roles
GET    /roles/{id}
GET    /roles/{id}/permissions
PUT    /roles/{id}/permissions     (full replacement)

SetRolePermissionsRequest fields:
  Global flags:
  (PermissionClearance enum, default Unset = denied, no approval path)
  Per-resource arrays (each entry is { resourceId, clearance }):
    dangerousShellAccesses, safeShellAccesses, containerAccesses, websiteAccesses, searchEngineAccesses, localInfoStoreAccesses, externalInfoStoreAccesses, audioDeviceAccesses, agentAccesses, taskAccesses, skillAccesses, agentHeaderAccesses, channelHeaderAccesses, documentSessionAccesses, nativeApplicationAccesses

Wildcard resourceId: ffffffff-ffff-ffff-ffff-ffffffffffff (all resources of that type).

PermissionClearance: Unset=0, ApprovedBySameLevelUser=1, ApprovedByWhitelistedUser=2, ApprovedByPermittedAgent=3, ApprovedByWhitelistedAgent=4, Independent=5.

Caller can only grant permissions they themselves hold.

────────────────────────────────────────
CHANNEL CONTEXTS
────────────────────────────────────────
POST   /channel-contexts           { agentId, name?, permissionSetId?, disableChatHeader?, allowedAgentIds? }
GET    /channel-contexts?agentId={guid}
GET    /channel-contexts/{id}
PUT    /channel-contexts/{id}      { name?, permissionSetId?, disableChatHeader?, allowedAgentIds? }
DELETE /channel-contexts/{id}

Allowed agents (granular):
  GET    /channel-contexts/{id}/agents
  POST   /channel-contexts/{id}/agents         { agentId }
  DELETE /channel-contexts/{id}/agents/{agentId}

Default resources (bulk):
  GET    /channel-contexts/{id}/defaults
  PUT    /channel-contexts/{id}/defaults        (SetDefaultResourcesRequest)

Default resources (per-key):
  PUT    /channel-contexts/{id}/defaults/{key}  { resourceId }
  DELETE /channel-contexts/{id}/defaults/{key}

ContextResponse returns: id, name, agent (AgentSummary), permissionSetId, disableChatHeader, allowedAgents (AgentSummary[]), createdAt, updatedAt.
ContextAllowedAgentsResponse returns: contextId, defaultAgent (AgentSummary), allowedAgents (AgentSummary[]).

────────────────────────────────────────
CHANNELS
────────────────────────────────────────
POST   /channels                   { agentId?, title?, contextId?, permissionSetId?, allowedAgentIds?, disableChatHeader?, customChatHeader?, toolAwarenessSetId? }
GET    /channels?agentId={guid}
GET    /channels/{id}
PUT    /channels/{id}              { title?, contextId?, permissionSetId?, allowedAgentIds?, disableChatHeader?, customChatHeader?, toolAwarenessSetId? }
DELETE /channels/{id}

Set default agent:
  PUT    /channels/{id}/agent                   { agentId }

Allowed agents (granular):
  GET    /channels/{id}/agents
  POST   /channels/{id}/agents                  { agentId }
  DELETE /channels/{id}/agents/{agentId}

Default resources (bulk):
  GET    /channels/{id}/defaults
  PUT    /channels/{id}/defaults                (SetDefaultResourcesRequest)

Default resources (per-key):
  PUT    /channels/{id}/defaults/{key}          { resourceId }
  DELETE /channels/{id}/defaults/{key}

Valid default resource keys: dangshell, safeshell, container, website, search, localinfo, externalinfo, audiodevice, displaydevice, agent, task, skill, transcriptionmodel, editor, document, nativeapp.

Either agentId or contextId (with agent) required on create.
allowedAgentIds on PUT replaces the set. permissionSetId=00000000-... removes the override; null leaves unchanged.

toolAwarenessSetId (guid|null): links a tool awareness set. Overrides the agent's set. See TOOL AWARENESS SETS section. On PUT, pass Guid.Empty to clear; null leaves unchanged.

ChannelResponse returns: id, title, agent (AgentSummary?), contextId, contextName, permissionSetId, effectivePermissionSetId, allowedAgents (AgentSummary[]), disableChatHeader, customChatHeader, toolAwarenessSetId, createdAt, updatedAt.
ChannelAllowedAgentsResponse returns: channelId, defaultAgent (AgentSummary?), allowedAgents (AgentSummary[]).

All responses embed full AgentSummary objects (id, name, modelId, modelName, providerName, roleId, roleName, maxCompletionTokens) instead of bare GUIDs — no follow-up requests needed to resolve agent details.

────────────────────────────────────────
DEFAULT RESOURCES
────────────────────────────────────────
SetDefaultResourcesRequest fields (all Guid?):
  dangerousShellResourceId, safeShellResourceId, containerResourceId, websiteResourceId, searchEngineResourceId, localInfoStoreResourceId, externalInfoStoreResourceId, audioDeviceResourceId, displayDeviceResourceId, agentResourceId, taskResourceId, skillResourceId, transcriptionModelId, editorSessionResourceId, documentSessionResourceId, nativeApplicationResourceId

Resolution order for jobs: channel DefaultResourceSet → context DefaultResourceSet → channel/context/role PermissionSet defaults.

────────────────────────────────────────
THREADS
────────────────────────────────────────
POST   /channels/{channelId}/threads           { name?, maxMessages?, maxCharacters? }
GET    /channels/{channelId}/threads
GET    /channels/{channelId}/threads/{id}
PUT    /channels/{channelId}/threads/{id}      { name?, maxMessages?, maxCharacters? }
DELETE /channels/{channelId}/threads/{id}

Defaults: maxMessages=50, maxCharacters=100000. Set to 0 in update to reset to null (system default).
Messages within a thread include history. Messages without a thread are one-shot (no history).

────────────────────────────────────────
CHAT
────────────────────────────────────────
POST   /channels/{id}/chat                     { message, agentId?, clientType?, editorContext? }
GET    /channels/{id}/chat                     → message history

Thread chat:
  POST   /channels/{id}/chat/threads/{threadId}
  GET    /channels/{id}/chat/threads/{threadId}/history

ChatClientType: CLI, API, Telegram, Discord, WhatsApp, VisualStudio, VisualStudioCode, UnoWindows, UnoAndroid, UnoMacOS, UnoLinux, UnoBrowser, Other.

editorContext: { editorType, editorVersion?, workspacePath?, activeFilePath?, activeFileLanguage?, selectionStartLine?, selectionEndLine?, selectedText? }

Without thread: one-shot (no history sent to model).
With thread: history included, trimmed by thread's maxMessages and maxCharacters.

────────────────────────────────────────
CHAT STREAMING (SSE)
────────────────────────────────────────
POST   /channels/{id}/chat/stream              Same body as POST /channels/{id}/chat
POST   /channels/{id}/chat/stream/approve/{jobId}  { approved: true|false }

Thread streaming:
  POST   /channels/{id}/chat/threads/{threadId}/stream
  POST   /channels/{id}/chat/threads/{threadId}/stream/approve/{jobId}

SSE event types: TextDelta (delta field), ToolCallStart (job), ToolCallResult (result), ApprovalRequired (pendingJob), ApprovalResult (approvalOutcome), Error (error), Done (finalResponse).

────────────────────────────────────────
AGENT JOBS
────────────────────────────────────────
POST   /channels/{channelId}/jobs              (SubmitAgentJobRequest)
GET    /channels/{channelId}/jobs
GET    /channels/{channelId}/jobs/summaries    (lightweight: id, channelId, agentId, actionKey, resourceId, status, createdAt, startedAt, completedAt — no resultData/errorLog/logs/segments)
GET    /channels/{channelId}/jobs/{jobId}
POST   /channels/{channelId}/jobs/{jobId}/approve   { approverAgentId? }
POST   /channels/{channelId}/jobs/{jobId}/stop      (graceful stop; also accepts Paused jobs)
POST   /channels/{channelId}/jobs/{jobId}/cancel    (also accepts Paused jobs)
PUT    /channels/{channelId}/jobs/{jobId}/pause     (pause an Executing job)
PUT    /channels/{channelId}/jobs/{jobId}/resume    (resume a Paused job)

SubmitAgentJobRequest:
  actionKey (string, required): identifies the action to execute. The set of valid
    action keys is dynamic — query GET /modules for the authoritative list.
  resourceId?: target resource for per-resource actions.
  agentId?: override the channel's default agent (must be in allowed set).
  callerAgentId?: the agent that triggered the job (for sub-agent chains).

  Module-specific fields may also be present on the DTO (e.g. shell type/script,
  transcription parameters). These are consumed by the module that owns the action
  key. See individual module documentation for details.

Segments (generic data push/pull for long-running jobs):
  POST   /channels/{channelId}/jobs/{jobId}/segments  { text, startTime, endTime, confidence }
  GET    /channels/{channelId}/jobs/{jobId}/segments?since={datetime}

AgentJobStatus: Queued=0, Executing=1, AwaitingApproval=2, Completed=3, Failed=4, Denied=5, Cancelled=6, Paused=7.

When resourceId is omitted for a per-resource action, default resources are resolved
automatically from the channel's DefaultResourceSet → context's DefaultResourceSet →
permission set defaults.

Job streaming endpoints (WebSocket, SSE) are module-provided. See individual module
documentation for available transports (e.g. Module-Transcription for live segments).

────────────────────────────────────────
RESOURCES
────────────────────────────────────────
Modules may register resource types at startup. All resource types follow the
same CRUD pattern under /resources/{type}:

  POST (create), GET (list), GET /{id}, PUT /{id}, DELETE /{id}.
  Some types also expose POST /sync for external-source reconciliation.

The set of available /resources/* endpoints is not fixed — it grows or shrinks
as modules are enabled or disabled. Query GET /modules for each module's
registered resource types.

Resource lookup: GET /resources/lookup/{type} → [{id, name}]
  Returns a lightweight list of IDs and display names for a given access type.
  Available access types are module-defined; the host guarantees the endpoint
  but not the set of valid type strings.

────────────────────────────────────────
MODULE SYSTEM
────────────────────────────────────────
"If you wish to make an apple pie from scratch, you must first invent the
universe." — Carl Sagan (via Dawkins, The Selfish Gene, extended-phenotype
argument: the interesting thing about a system is not its current list of
parts but the rules by which parts can be added, removed, and swapped.)

The host application is deliberately agnostic about which modules ship or
what they do. This section describes only the framework; individual module
capabilities live in their own documentation.

Concepts:
  Module: a self-contained unit of tools, CLI commands, REST endpoints,
    resource types, and optional DI service exports, loaded at startup
    from a manifest and an entry assembly.
  Tool: a single callable operation exposed to the LLM. Dispatched via
    ActionKey (string) — the sole routing mechanism for job execution.
  Tool prefix: a short string (e.g. "cu_", "oa_") prepended to all of a
    module's tool names to avoid collisions.
  Contract: a named DI service a module exports for others to consume.
    Modules may declare required contracts; the dependency graph is
    resolved via topological sort at startup.

Lifecycle:
  Startup: manifests loaded from modules/{dir}/module.json → topological
    sort by requires/exports → InitializeAsync per module → tools and
    endpoints registered in ModuleRegistry.
  Runtime enable:  Register + CacheManifest + check unsatisfied deps +
    InitializeAsync (rollback on failure).
  Runtime disable: safety check (reject if other modules depend on
    exports) + ShutdownAsync + Unregister. Disabling a module that
    exports a required contract is rejected (409 Conflict).
  State persisted in DB + .modules.env.

Tool resolution: module tools are resolved by prefix lookup via
  ModuleRegistry. Inline tools (executed in the chat loop without a job
  record) are also module-registered.

ModuleManifest fields: id, displayName, version, toolPrefix, entryAssembly,
  minHostVersion?, author?, description?, license?, platforms[], enabled,
  defaultEnabled, executionTimeoutSeconds?,
  exports[{contractName, serviceType}], requires[string].

REST:
  GET  /modules                          List all modules with tools and status
  GET  /modules/{moduleId}               Single module detail
  POST /modules/{moduleId}/enable        Enable a disabled module at runtime
  POST /modules/{moduleId}/disable       Disable an enabled module at runtime

CLI:
  module list | module get <id> | module enable <id> | module disable <id>

────────────────────────────────────────
PERMISSION RESOLUTION
────────────────────────────────────────
Stage 1 — Agent capability: role's PermissionSetDB checked. Independent → approved. No grant → denied. Otherwise → Stage 2.
Stage 2 — Channel/context pre-auth: channel PS checked first; if it addresses the action, that result is final (context not consulted). If channel doesn't address it, context PS checked. Independent → pre-approved. Otherwise → AwaitingApproval.

Channel PS checked → context PS → fallback AwaitingApproval.

Cross-thread history access (double-gate):
  Agent role PS must have CanReadCrossThreadHistory=true AND target channel effective PS must also have CanReadCrossThreadHistory=true (opt-in).
  Channels without opt-in are private even if the agent holds the permission.
  Independent clearance on the agent overrides the channel opt-in requirement.
  Agent must be primary or in AllowedAgents on the target channel.
  Accessible threads listed in chat header (accessible-threads section) and via list_accessible_threads / read_thread_history inline tools.

────────────────────────────────────────────────────────────────────
ADVANCED EXAMPLE: Multi-Agent Channel with Context
────────────────────────────────────────────────────────────────────

Goal: A context with a primary agent and a secondary specialist agent,
where channels inherit the context's defaults. Agents can be switched per-message.

Step 1 — Assume MAIN_AGENT_ID and SPECIALIST_AGENT_ID already exist with appropriate roles.

Step 2 — Create a context.

    POST /channel-contexts
    {
      "agentId": "MAIN_AGENT_ID",
      "name": "Multi-Agent Context"
    }

  → CONTEXT_ID

Step 3 — Add the specialist agent as an allowed agent.

    POST /channel-contexts/CONTEXT_ID/agents
    { "agentId": "SPECIALIST_AGENT_ID" }

Step 4 — Set context-level defaults (inherited by all channels in it).

  Default resources are configured per module. For example, if a shell module
  is enabled:

    PUT /channel-contexts/CONTEXT_ID/defaults/safeshell
    { "resourceId": "CONTAINER_ID" }

Step 5 — Create a channel inside the context.

    POST /channels
    {
      "contextId": "CONTEXT_ID",
      "title": "Ops Channel"
    }

  → CHANNEL_ID
  Agent, allowed agents, and defaults all cascade from the context.

Step 6 — Chat with the default agent.

    POST /channels/CHANNEL_ID/chat
    { "message": "What's the server status?" }

Step 7 — Override to the specialist agent for a specific message.

    POST /channels/CHANNEL_ID/chat
    {
      "message": "Analyse this code for security issues",
      "agentId": "SPECIALIST_AGENT_ID"
    }

  Agent override is allowed because SPECIALIST_AGENT_ID is in the context's allowed agents.

Step 8 — Channel-level override. Set different defaults just for this channel.

  Per-key endpoint overrides the context's default for this channel only:

    PUT /channels/CHANNEL_ID/defaults/safeshell
    { "resourceId": "DIFFERENT_CONTAINER_ID" }

  Other channels in the same context still use the context defaults.

────────────────────────────────────────────────────────────────────
ADVANCED EXAMPLE: Threaded Conversation with History
────────────────────────────────────────────────────────────────────

Goal: Multi-turn conversation within a channel where the model sees history.

Step 1 — Create a thread on an existing channel.

    POST /channels/CHANNEL_ID/threads
    { "name": "Debug Session", "maxMessages": 20, "maxCharacters": 50000 }

  → THREAD_ID

Step 2 — Chat within the thread. Each message includes the previous history (up to limits).

    POST /channels/CHANNEL_ID/chat/threads/THREAD_ID
    { "message": "I'm seeing a null reference in UserService.cs" }

    POST /channels/CHANNEL_ID/chat/threads/THREAD_ID
    { "message": "Can you check line 42?" }

  The second message sees the first exchange as context.

Step 3 — Stream within the thread.

    POST /channels/CHANNEL_ID/chat/threads/THREAD_ID/stream

Step 4 — Chat without a thread on the same channel is one-shot (no history).

    POST /channels/CHANNEL_ID/chat
    { "message": "Unrelated question" }

  This sees no prior messages.

────────────────────────────────────────
DATABASE ADMINISTRATION
────────────────────────────────────────
Multi-provider EF Core support. Provider selected via Database:Provider in Core .env.
Supported: JsonFile (default, InMemory+JSON), Postgres, SqlServer, SQLite.
Stubbed (blocked on EFC 10 packages): MySql, Oracle.
See docs/Database-Configuration.md for full setup.

Admin endpoints (require authenticated user admin):

  GET  /admin/db/status   → { state, applied[], pending[] }
    state: Idle | Draining | Migrating

  POST /admin/db/migrate  → { applied (int), migrations[], message }
    409 if already in progress. Drains in-flight requests first.
    ⚠️ All requests are held during migration.

CLI equivalent: db migrate

Migrations are NEVER automatic. App starts normally with pending migrations
(warns at startup). User must explicitly trigger via API or CLI.

────────────────────────────────────────
ENCRYPTION & KEY MANAGEMENT
────────────────────────────────────────
Provider API keys encrypted at rest with AES-256-GCM.
Key resolution: Encryption:Key in Core .env (Base64, exactly 32 bytes decoded) → auto-generated via PersistentKeyStore at %LOCALAPPDATA%/SharpClaw/.encryption-key if unset.
Startup validation: invalid Base64 or wrong key length → backend crashes with clear error message.
⚠️ Changing/losing the key makes previously encrypted provider API keys permanently unreadable.

────────────────────────────────────────
ENV FILE MANAGEMENT
────────────────────────────────────────
Two .env files (JSON-with-comments, loaded into IConfiguration):
  Core — server-side (Infrastructure/Environment/.env). Managed exclusively via API.
  Interface — client-side (SharpClaw.Uno/Environment/.env). Direct file I/O by client.

All /env/core/* endpoints require JWT auth. Caller must be admin OR EnvEditor:AllowNonAdmin=true in Core .env.

GET  /env/core/auth  → { authorised: bool }  (pre-check — is caller allowed to edit Core .env?)
GET  /env/core       → { content: "raw JSON string" }  (403 if not authorised, 404 if file missing)
PUT  /env/core       { content }  → { saved: true }  (403 if not authorised)

Core .env keys: Encryption:Key (AES-256-GCM, 32-byte Base64; auto-generated if unset; invalid value crashes backend), Jwt:Secret,
Interface .env keys: Api:Url (default http://127.0.0.1:48923), Backend:Enabled (default true).

Changes to Core .env require a backend restart to take effect.

────────────────────────────────────────
CUSTOM CHAT HEADER
────────────────────────────────────────
Agents and channels have an optional customChatHeader field (string|null) that replaces the default metadata header prepended to each user message.

Override chain: channel.customChatHeader > agent.customChatHeader > built-in default. disableChatHeader=true suppresses all headers.

Template syntax: {{tagName}} placeholders, case-insensitive.

Context tags (single value):
  {{time}}               → 2025-07-14 09:30:00 UTC
  {{user}}               → marko
  {{via}}                → CLI | API | Telegram | Discord | WhatsApp | VisualStudio | VisualStudioCode | Uno* | Other
  {{role}}               → Admin (CreateSubAgents, SafeShell)
  {{bio}}                → Backend developer, likes Rust
  {{agent-name}}         → CodeReview Agent
  {{agent-role}}         → DevOps clearance=Independent (SafeShell[guid,...], ManageAgent[guid,...])
  {{clearance}}          → Independent
  {{grants}}             → CreateSubAgents, SafeShell, ManageAgent  (user grants, name-only)
  {{agent-grants}}       → SafeShell[guid,...], EditTask[guid,...]  (agent grants with resource IDs)
  {{editor}}             → VisualStudio2026 18.4 file=Program.cs lang=csharp sel=10-25 selection="public async Task RunAsync()"
  {{accessible-threads}} → Debug Session [Ops Channel] (guid), Planning [Strategy] (guid)

Wildcard grants (ffffffff-...) resolve to all concrete resource IDs of that type.

Resource tags (enumerate entities):
  {{Agents}}             → comma-separated GUIDs (no template)
  {{Agents:{Name} ({Id})}}  → per-item formatted: CodeReview Agent (3fa8...), DevOps Agent (7c9e...)

Supported resource tag names: Agents, Models, Providers, Channels, Threads, Roles, Users, Containers, Websites, SearchEngines, AudioDevices, DisplayDevices, EditorSessions, Skills, SystemUsers, LocalInfoStores, ExternalInfoStores, ScheduledTasks, Tasks.

Note: Some entity types (Containers, Websites, SearchEngines, AudioDevices, DisplayDevices, EditorSessions, SystemUsers, LocalInfoStores, ExternalInfoStores) are module-registered resources. They return results only when their owning module is enabled.

Fields with [HeaderSensitive] (PasswordHash, PasswordSalt, EncryptedApiKey) → [redacted]. Unknown fields → [FieldName?].

Permissions: canEditAgentHeader / canEditChannelHeader (global flags) + agentHeaderAccesses / channelHeaderAccesses (per-resource arrays in role permissions).

Examples:

  Template: [{{time}} | {{user}} via {{via}}]
  Output:   [2025-07-14 09:30:00 UTC | marko via CLI]

  Template: [time: {{time}} | user: {{user}} | agent: {{agent-name}} | role: {{agent-role}}]
  Output:   [time: 2025-07-14 09:30:00 UTC | user: marko | agent: CodeReview Agent | role: DevOps clearance=Independent (SafeShell[3fa85f64-...], ManageAgent[7c9e6679-...])]

  Template: Agents: {{Agents:{Name} (model={ModelName})}}
  Output:   Agents: CodeReview Agent (model=gpt-4o), DevOps Agent (model=claude-sonnet-4-20250514)

  Template: Users: {{Users:{Username} hash={PasswordHash}}}
  Output:   Users: marko hash=[redacted], admin hash=[redacted]

  Template: [{{time}} | {{editor}}]
  Output:   [2025-07-14 09:30:00 UTC | VisualStudio2026 18.4.2 workspace=E:\source\SharpClaw file=Program.cs lang=csharp sel=10-25 selection="public async Task RunAsync()"]

────────────────────────────────────────
TOOL AWARENESS SETS
────────────────────────────────────────
A reusable named configuration controlling which tool-call schemas are sent in API requests. Reduces prompt-token overhead by excluding tools the agent will never use.

POST   /tool-awareness-sets                   { name, tools? }
GET    /tool-awareness-sets
GET    /tool-awareness-sets/{id}
PUT    /tool-awareness-sets/{id}               { name?, tools? }
DELETE /tool-awareness-sets/{id}

tools: Dictionary<string, bool>. Keys are tool names (module-defined, e.g. query GET /modules for valid names). Tools whose key is true or absent are included; only tools explicitly set to false are excluded. Empty dict {} = all tools enabled.

Override chain: channel.toolAwarenessSetId > agent.toolAwarenessSetId > null (all tools). Assign via toolAwarenessSetId field on POST/PUT agents and channels.

On DELETE, agents/channels referencing the set have their toolAwarenessSetId set to null (SetNull cascade).

ToolAwarenessSetResponse: id, name, tools (Dictionary<string,bool>), createdAt, updatedAt.

CLI:
  tools add <name> [json]            Create a tool awareness set
  tools list                         List all sets
  tools get <id>                     Show set details
  tools update <id> [--name <n>] [json]  Update a set
  tools delete <id>                  Delete a set
  agent add <name> <modelId> --tools <setId>    Assign on creation
  agent update <id> <name> --tools <setId>      Assign on update
  channel add --agent <id> --tools <setId>      Assign on creation

Available tool names are dynamic — they depend on which modules are enabled.
Query GET /modules for the authoritative list; each module entry includes a
Tools array with the exact names to use as dictionary keys.

────────────────────────────────────────
TASKS
────────────────────────────────────────
Full task reference (endpoints, script language, step kinds, diagnostics, permissions,
agent tool exposure, scheduling, SSE streaming): Tasks-skill.md

Short summary: tasks are user-defined C# scripts that run as managed background
processes. Register a definition with POST /tasks, launch an instance with
POST /tasks/{id}/instances, stream output from GET /tasks/{id}/instances/{iid}/stream.
Active definitions are exposed as task_invoke__{name} tools to agents with
CanInvokeTasksAsTool permission.
