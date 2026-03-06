SharpClaw Application API — Agent Skill Reference

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

ProviderType values: OpenAI, Anthropic, OpenRouter, GoogleVertexAI, GoogleGemini, ZAI, VercelAIGateway, XAI, Groq, Cerebras, Mistral, GitHubCopilot, Custom, Local.

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
POST   /agents                     { name, modelId, systemPrompt? }
GET    /agents
GET    /agents/{id}
PUT    /agents/{id}                { name?, modelId?, systemPrompt? }
DELETE /agents/{id}
PUT    /agents/{id}/role           { roleId, callerUserId }

AgentResponse includes: id, name, systemPrompt, modelId, modelName, providerName, roleId, roleName.

AgentSummary (embedded in channel/context responses): id, name, modelId, modelName, providerName, roleId, roleName. Same as AgentResponse minus systemPrompt.

────────────────────────────────────────
ROLES & PERMISSIONS
────────────────────────────────────────
GET    /roles
GET    /roles/{id}
GET    /roles/{id}/permissions
PUT    /roles/{id}/permissions     (full replacement)

SetRolePermissionsRequest fields:
  defaultClearance (PermissionClearance enum)
  Global flags: canCreateSubAgents, canCreateContainers, canRegisterInfoStores, canAccessLocalhostInBrowser, canAccessLocalhostCli, canClickDesktop, canTypeOnDesktop
  Per-resource arrays (each entry is { resourceId, clearance }):
    dangerousShellAccesses, safeShellAccesses, containerAccesses, websiteAccesses, searchEngineAccesses, localInfoStoreAccesses, externalInfoStoreAccesses, audioDeviceAccesses, agentAccesses, taskAccesses, skillAccesses

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
POST   /channels                   { agentId?, title?, contextId?, permissionSetId?, allowedAgentIds?, disableChatHeader? }
GET    /channels?agentId={guid}
GET    /channels/{id}
PUT    /channels/{id}              { title?, contextId?, permissionSetId?, allowedAgentIds?, disableChatHeader? }
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

Valid default resource keys: dangshell, safeshell, container, website, search, localinfo, externalinfo, audiodevice, displaydevice, agent, task, skill, transcriptionmodel, editor.

Either agentId or contextId (with agent) required on create.
allowedAgentIds on PUT replaces the set. permissionSetId=00000000-... removes the override; null leaves unchanged.

ChannelResponse returns: id, title, agent (AgentSummary?), contextId, contextName, permissionSetId, effectivePermissionSetId, allowedAgents (AgentSummary[]), disableChatHeader, createdAt, updatedAt.
ChannelAllowedAgentsResponse returns: channelId, defaultAgent (AgentSummary?), allowedAgents (AgentSummary[]).

All responses embed full AgentSummary objects (id, name, modelId, modelName, providerName, roleId, roleName) instead of bare GUIDs — no follow-up requests needed to resolve agent details.

────────────────────────────────────────
DEFAULT RESOURCES
────────────────────────────────────────
SetDefaultResourcesRequest fields (all Guid?):
  dangerousShellResourceId, safeShellResourceId, containerResourceId, websiteResourceId, searchEngineResourceId, localInfoStoreResourceId, externalInfoStoreResourceId, audioDeviceResourceId, displayDeviceResourceId, agentResourceId, taskResourceId, skillResourceId, transcriptionModelId, editorSessionResourceId

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

ChatClientType: CLI, API, Telegram, Discord, WhatsApp, VisualStudio, VisualStudioCode, Other.

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
GET    /channels/{channelId}/jobs/{jobId}
POST   /channels/{channelId}/jobs/{jobId}/approve   { approverAgentId? }
POST   /channels/{channelId}/jobs/{jobId}/stop      (transcription: complete normally)
POST   /channels/{channelId}/jobs/{jobId}/cancel

SubmitAgentJobRequest:
  actionType (required), resourceId?, agentId?, callerAgentId?,
  dangerousShellType?, safeShellType?, scriptJson?, workingDirectory?,
  transcriptionModelId?, language?

AgentJobStatus: Queued=0, Executing=1, AwaitingApproval=2, Completed=3, Failed=4, Denied=5, Cancelled=6.

Transcription segments:
  POST   /channels/{channelId}/jobs/{jobId}/segments  { text, startTime, endTime, confidence }
  GET    /channels/{channelId}/jobs/{jobId}/segments?since={datetime}

When resourceId is omitted for a per-resource action, default resources are resolved automatically from the channel's DefaultResourceSet → context's DefaultResourceSet → permission set defaults.

When transcriptionModelId is omitted for transcription actions, the default transcription model is resolved from the channel → context default resource set.

language is a BCP-47 hint (e.g. "en", "de"). Omit for auto-detect. Supplying it improves accuracy on short chunks.

Audio is automatically normalised to mono 16 kHz 16-bit PCM (Whisper-optimal).

AgentActionType categories:
  Global: CreateSubAgent, CreateContainer, RegisterInfoStore, AccessLocalhostInBrowser, AccessLocalhostCli, ClickDesktop, TypeOnDesktop
  Per-resource: UnsafeExecuteAsDangerousShell, ExecuteAsSafeShell, AccessLocalInfoStore, AccessExternalInfoStore, AccessWebsite, QuerySearchEngine, AccessContainer, ManageAgent, EditTask, AccessSkill, CaptureDisplay
  Transcription: TranscribeFromAudioDevice, TranscribeFromAudioStream, TranscribeFromAudioFile
  Editor: EditorReadFile, EditorGetOpenFiles, EditorGetSelection, EditorGetDiagnostics, EditorApplyEdit, EditorCreateFile, EditorDeleteFile, EditorShowDiff, EditorRunBuild, EditorRunTerminal

DangerousShellType: Bash, PowerShell, CommandPrompt, Git.
SafeShellType: Mk8Shell.

────────────────────────────────────────
TRANSCRIPTION STREAMING
────────────────────────────────────────
WebSocket: /jobs/{jobId}/ws       → JSON text frames with segment objects
SSE:       /jobs/{jobId}/stream   → data: frames with segment JSON

────────────────────────────────────────
RESOURCES
────────────────────────────────────────
All resource types follow the same CRUD + sync pattern under /resources/{type}.

Containers:       /resources/containers        (+ /sync)
Audio devices:    /resources/audiodevices       (+ /sync)
Display devices:  /resources/displaydevices     (+ /sync)
Editor sessions:  /resources/editorsessions     (no sync)

Each type: POST (create), GET (list), GET /{id}, PUT /{id}, DELETE /{id}, POST /sync (where applicable).

────────────────────────────────────────
EDITOR BRIDGE
────────────────────────────────────────
WS  /editor/ws         WebSocket for IDE extensions. Registration → request/response loop. 30s timeout.
GET /editor/sessions   List connected editor sessions.

EditorType: VisualStudio2026, VisualStudioCode, Other.

────────────────────────────────────────
PERMISSION RESOLUTION
────────────────────────────────────────
Stage 1 — Agent capability: role's PermissionSetDB checked. Independent → approved. No grant → denied. Otherwise → Stage 2.
Stage 2 — Channel/context pre-auth: channel PS checked first; if it addresses the action, that result is final (context not consulted). If channel doesn't address it, context PS checked. Independent → pre-approved. Otherwise → AwaitingApproval.

Channel PS checked → context PS → fallback AwaitingApproval.

────────────────────────────────────────────────────────────────────
ADVANCED EXAMPLE: Transcription Channel Setup
────────────────────────────────────────────────────────────────────

Goal: Create a channel called "TranscriptionChannel" with a default agent using OpenAI's whisper-1 model, a role granting Independent transcription permission for all audio devices, and a default audio device — so that submitting a transcription job requires only { actionType } and nothing else.

Step 1 — Ensure provider and model exist.

  Assume an OpenAI provider already exists with id PROVIDER_ID and key set.
  Sync models to pick up whisper-1:

    POST /providers/PROVIDER_ID/sync-models

  Find the whisper-1 model id from the response (or GET /models and filter).
  Call it WHISPER_MODEL_ID.
  Verify it has Transcription capability. If sync didn't set it:

    PUT /models/WHISPER_MODEL_ID
    { "capabilities": "Transcription" }

Step 2 — Create an agent for transcription.

    POST /agents
    {
      "name": "Transcription Agent",
      "modelId": "WHISPER_MODEL_ID",
      "systemPrompt": null
    }

  → AGENT_ID

Step 3 — Set up a role with Independent transcription permission for all audio devices.

  Get the list of roles:

    GET /roles

  Pick one (or use the seeded Admin role). Call it ROLE_ID.
  Set its permissions to include audioDeviceAccesses with wildcard grant:

    PUT /roles/ROLE_ID/permissions
    {
      "defaultClearance": "Independent",
      "canCreateSubAgents": false,
      "canCreateContainers": false,
      "canRegisterInfoStores": false,
      "canAccessLocalhostInBrowser": false,
      "canAccessLocalhostCli": false,
      "canClickDesktop": false,
      "canTypeOnDesktop": false,
      "audioDeviceAccesses": [
        { "resourceId": "ffffffff-ffff-ffff-ffff-ffffffffffff", "clearance": "Independent" }
      ]
    }

Step 4 — Assign the role to the agent.

    PUT /agents/AGENT_ID/role
    { "roleId": "ROLE_ID", "callerUserId": "USER_ID" }

Step 5 — Discover audio devices.

    POST /resources/audiodevices/sync

    GET /resources/audiodevices

  Pick the target device. Call it AUDIO_DEVICE_ID.

Step 6 — Create the channel.

    POST /channels
    {
      "agentId": "AGENT_ID",
      "title": "TranscriptionChannel"
    }

  → CHANNEL_ID

Step 7 — Set channel defaults so jobs resolve automatically.

  Set the default audio device:

    PUT /channels/CHANNEL_ID/defaults/audiodevice
    { "resourceId": "AUDIO_DEVICE_ID" }

  Set the default transcription model:

    PUT /channels/CHANNEL_ID/defaults/transcriptionmodel
    { "resourceId": "WHISPER_MODEL_ID" }

Step 8 — Submit a transcription job with minimal params.

  Everything is inferred from the channel:
  - Agent → channel default (Transcription Agent)
  - Resource → channel default audiodevice (AUDIO_DEVICE_ID)
  - Model → channel default transcriptionmodel (WHISPER_MODEL_ID)
  - Permission → agent's role grants Independent on all audio devices

    POST /channels/CHANNEL_ID/jobs
    {
      "actionType": "TranscribeFromAudioDevice"
    }

  That's it. No resourceId, no transcriptionModelId, no agentId needed.
  The job is auto-approved (Independent clearance) and begins capturing audio immediately.

  Optional: pass language for better accuracy on short chunks:

    POST /channels/CHANNEL_ID/jobs
    {
      "actionType": "TranscribeFromAudioDevice",
      "language": "en"
    }

Step 9 — Stream live segments.

  From the job response, take JOB_ID.

  WebSocket: connect to /jobs/JOB_ID/ws → receive JSON segment frames.
  SSE: GET /jobs/JOB_ID/stream → data: frames with segment JSON.
  Poll: GET /channels/CHANNEL_ID/jobs/JOB_ID/segments?since=2025-01-01T00:00:00Z

Step 10 — Stop the transcription.

    POST /channels/CHANNEL_ID/jobs/JOB_ID/stop

  Job transitions to Completed. Remaining buffered audio is flushed and transcribed.

────────────────────────────────────────────────────────────────────
ADVANCED EXAMPLE: Multi-Agent Channel with Context
────────────────────────────────────────────────────────────────────

Goal: A context with a primary chat agent and a secondary transcription agent,
where channels inherit the context's defaults. Agents can be switched per-message.

Step 1 — Assume CHAT_AGENT_ID and TRANSCRIPTION_AGENT_ID already exist with appropriate roles.

Step 2 — Create a context.

    POST /channel-contexts
    {
      "agentId": "CHAT_AGENT_ID",
      "name": "Multi-Agent Context"
    }

  → CONTEXT_ID

Step 3 — Add the transcription agent as an allowed agent.

    POST /channel-contexts/CONTEXT_ID/agents
    { "agentId": "TRANSCRIPTION_AGENT_ID" }

Step 4 — Set context-level defaults (inherited by all channels in it).

    PUT /channel-contexts/CONTEXT_ID/defaults/audiodevice
    { "resourceId": "AUDIO_DEVICE_ID" }

    PUT /channel-contexts/CONTEXT_ID/defaults/safeshell
    { "resourceId": "MK8_CONTAINER_ID" }

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

Step 7 — Override to the transcription agent for a specific request.

    POST /channels/CHANNEL_ID/jobs
    {
      "actionType": "TranscribeFromAudioDevice",
      "agentId": "TRANSCRIPTION_AGENT_ID"
    }

  resourceId resolved from context defaults. Agent override is allowed because TRANSCRIPTION_AGENT_ID is in the context's allowed agents.

Step 8 — Channel-level override. Set a different default audio device just for this channel.

    PUT /channels/CHANNEL_ID/defaults/audiodevice
    { "resourceId": "DIFFERENT_AUDIO_DEVICE_ID" }

  Now jobs on this channel use DIFFERENT_AUDIO_DEVICE_ID. Other channels in the same context still use AUDIO_DEVICE_ID from the context defaults.

────────────────────────────────────────────────────────────────────
ADVANCED EXAMPLE: Safe Shell Execution
────────────────────────────────────────────────────────────────────

Goal: Submit an mk8.shell job on a channel with defaults so only the script needs to be provided.

Assuming channel CHANNEL_ID already has:
- A default agent with a role granting Independent on safeShellAccesses (wildcard)
- A default safeshell resource (mk8.shell container)

    POST /channels/CHANNEL_ID/jobs
    {
      "actionType": "ExecuteAsSafeShell",
      "safeShellType": "Mk8Shell",
      "scriptJson": "{\"operations\":[{\"verb\":\"Echo\",\"args\":[\"Hello from mk8.shell\"]}]}"
    }

resourceId is resolved from channel defaults (safeshell key).
Agent is resolved from channel default.
Permission is auto-approved via Independent clearance.

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
    { "message": "Now refactor that method" }

Step 4 — Chat without a thread on the same channel is one-shot (no history).

    POST /channels/CHANNEL_ID/chat
    { "message": "Unrelated question" }

  This sees no prior messages.
