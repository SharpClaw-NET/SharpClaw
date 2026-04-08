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
BOT INTEGRATIONS
────────────────────────────────────────
GET    /bots                       → list all bot integrations
GET    /bots/{id}                  → get by id
GET    /bots/type/{type}           → get by type name (telegram, discord, whatsapp)
PUT    /bots/{id}                  { enabled?, botToken?, defaultChannelId? }
GET    /bots/config/{type}         → decrypted config for gateway use (enabled, botToken, defaultChannelId)

Rows are pre-seeded on startup for each BotType — no POST/DELETE.
Bot tokens are AES-GCM encrypted at rest (same as provider API keys).
PUT fields are all optional (partial update):
  enabled (bool): enable/disable the bot.
  botToken (string): set or replace the encrypted token. Empty string clears it.
  defaultChannelId (guid|null): the SharpClaw channel the bot forwards messages to. Guid.Empty clears it.

BotType values: Telegram, Discord, WhatsApp.

BotIntegrationResponse: id, botType, enabled, hasBotToken, defaultChannelId, createdAt, updatedAt.

CLI: bot list, bot get <id>, bot update <id> [--enabled true|false] [--token <tok>] [--channel <channelId>], bot config <type>.

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
  defaultClearance (PermissionClearance enum)
  Global flags: canCreateSubAgents, canCreateContainers, canRegisterInfoStores, canAccessLocalhostInBrowser, canAccessLocalhostCli, canClickDesktop, canTypeOnDesktop, canReadCrossThreadHistory, canEditAgentHeader, canEditChannelHeader, canCreateDocumentSessions, canEnumerateWindows, canFocusWindow, canCloseWindow, canResizeWindow, canSendHotkey, canReadClipboard, canWriteClipboard
  Clearance overrides for specialized globals: createDocumentSessionsClearance, enumerateWindowsClearance, focusWindowClearance, closeWindowClearance, resizeWindowClearance, sendHotkeyClearance, readClipboardClearance, writeClipboardClearance (PermissionClearance enum, default Unset = use defaultClearance)
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
GET    /channels/{channelId}/jobs/summaries    (lightweight: id, channelId, agentId, actionType, resourceId, status, createdAt, startedAt, completedAt — no resultData/errorLog/logs/segments)
GET    /channels/{channelId}/jobs/{jobId}
POST   /channels/{channelId}/jobs/{jobId}/approve   { approverAgentId? }
POST   /channels/{channelId}/jobs/{jobId}/stop      (transcription: complete normally; also accepts Paused)
POST   /channels/{channelId}/jobs/{jobId}/cancel    (also accepts Paused)
PUT    /channels/{channelId}/jobs/{jobId}/pause     (pause an Executing job; stops capture/inference)
PUT    /channels/{channelId}/jobs/{jobId}/resume    (resume a Paused job; restarts capture/inference)

SubmitAgentJobRequest:
  actionType (required), resourceId?, agentId?, callerAgentId?,
  dangerousShellType?, safeShellType?, scriptJson?, workingDirectory?,
  transcriptionModelId?, language?, transcriptionMode?, windowSeconds?, stepSeconds?

TranscriptionMode values: SlidingWindow (default, two-pass), StrictWindow.

AgentJobStatus: Queued=0, Executing=1, AwaitingApproval=2, Completed=3, Failed=4, Denied=5, Cancelled=6, Paused=7.

Transcription segments:
  POST   /channels/{channelId}/jobs/{jobId}/segments  { text, startTime, endTime, confidence }
  GET    /channels/{channelId}/jobs/{jobId}/segments?since={datetime}

When resourceId is omitted for a per-resource action, default resources are resolved automatically from the channel's DefaultResourceSet → context's DefaultResourceSet → permission set defaults.

When transcriptionModelId is omitted for transcription actions, the default transcription model is resolved from the channel → context default resource set.

language is a BCP-47 hint (e.g. "en", "de"). Omit for auto-detect. Supplying it improves accuracy on short chunks. When set, the orchestrator enforces it: prompt is seeded with a target-language phrase, and up to 4 retries with escalating reinforcement if Whisper returns the wrong language. Never drops audio — accepts after retries.

transcriptionMode: SlidingWindow (default) or StrictWindow.
  SlidingWindow: two-pass with multi-layer dedup. Audio flows: mic → ring buffer → silence gate → sliding window → STT API → dedup → emit.
    Every step interval, a window of audio is extracted and sent to the API. The response is diffed against
    the previous window's text to extract genuinely new content. New text is split into per-sentence segments
    with proportionally distributed timestamps. Segments are emitted provisionally within ~2 s (isProvisional=true),
    then finalized after the commit delay confirms them. A HashSet of all emitted texts prevents hallucination
    replay (where the API regurgitates the entire transcript as new speech).
  StrictWindow: non-overlapping sequential windows (default 10 s). Each window transcribed exactly once — one API call per window. Full dedup pipeline runs as safety net. Minimal token cost; perceived latency equals the window length. Use windowSeconds to control window size (clamped [5, 15]).

windowSeconds: seconds of audio per inference tick. Clamped [5, 15]. Default 10.
stepSeconds: seconds between inference ticks (SlidingWindow mode only). Clamped [1, window]. Default 2. Ignored in StrictWindow mode where step equals window.

TranscriptionMode enum: SlidingWindow=0, StrictWindow=2.

Two-pass segment lifecycle (SlidingWindow mode):
  1. Provisional: emitted quickly with isProvisional=true after passing the dedup pipeline (~2 s). Text may shift.
  2. Finalized: same id re-pushed with isProvisional=false, updated text/confidence after commit delay (2 s).
  3. Retracted: stale provisional deleted, tombstone pushed (empty text, isProvisional=false).
  StrictWindow always emits isProvisional=false.

Dedup pipeline (non-timestamped API responses):
  When the STT API returns the full window as a single text blob without timestamps:
  1. Text diff: current response compared against previousWindowText. Containment check
     (strict for <10 words, 10% fuzzy floor for longer) suppresses subsets. Suffix-prefix
     overlap removes already-seen prefixes, returning only the genuinely new tail.
  2. Context tracking: previousWindowText only upgrades to longer responses when no new text
     is found (never downgrades to shorter subsets to prevent context loss and re-emission).
  3. Sentence splitting: multi-sentence new text split at [.!?]+space+uppercase boundaries.
     Each sentence gets its own segment with proportionally distributed timestamps.
  4. Fragment merge: ≤2-word lowercase residuals merged into the latest provisional.
  5. Emitted-text guard: HashSet of all emitted texts (trimmed, period-stripped,
     case-insensitive) blocks duplicates at emission time, including hallucination replay.
  6. Timestamp guard: segments with absEnd ≤ lastSeenEnd are skipped.

Audio is automatically normalised to mono 16 kHz 16-bit PCM (Whisper-optimal).

AgentActionType categories:
  Global: CreateSubAgent, CreateContainer, RegisterInfoStore, AccessLocalhostInBrowser, AccessLocalhostCli, ReadCrossThreadHistory
  Per-resource: UnsafeExecuteAsDangerousShell, ExecuteAsSafeShell, AccessLocalInfoStore, AccessExternalInfoStore, AccessWebsite, QuerySearchEngine, AccessContainer, ManageAgent, EditTask, AccessSkill
  Transcription: TranscribeFromAudioDevice, TranscribeFromAudioStream, TranscribeFromAudioFile
  Editor: EditorReadFile, EditorGetOpenFiles, EditorGetSelection, EditorGetDiagnostics, EditorApplyEdit, EditorCreateFile, EditorDeleteFile, EditorShowDiff, EditorRunBuild, EditorRunTerminal
  Module dispatch: ModuleAction (=100) — dispatches to a loaded module by tool name. See MODULE SYSTEM section.

Deprecated (still accepted, use module equivalents):
  Computer Use → cu_* tools: ClickDesktop, TypeOnDesktop, EnumerateWindows, FocusWindow, CloseWindow, ResizeWindow, SendHotkey, ReadClipboard, WriteClipboard, CaptureDisplay, CaptureWindow, StopProcess, LaunchNativeApplication
  Office Apps → oa_* tools: CreateDocumentSession, SpreadsheetReadRange, SpreadsheetWriteRange, SpreadsheetListSheets, SpreadsheetCreateSheet, SpreadsheetDeleteSheet, SpreadsheetGetInfo, SpreadsheetCreateWorkbook, SpreadsheetLiveReadRange, SpreadsheetLiveWriteRange

Inline tools (no job created, handled in the chat inference loop):
  wait — pause 1–300 seconds. No permissions required.
  list_accessible_threads — list threads from other channels the agent can read. Requires ReadCrossThreadHistory permission.
  read_thread_history — read conversation history from a cross-channel thread. Params: threadId (required), maxMessages (optional, 1–200). Requires ReadCrossThreadHistory + channel opt-in.

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

Containers:           /resources/containers        (+ /sync)
Audio devices:        /resources/audiodevices       (+ /sync)
Display devices:      /resources/displaydevices     (+ /sync)
Editor sessions:      /resources/editorsessions     (no sync)
Document sessions:    /resources/documentsessions   (no sync)
Native applications:  /resources/nativeapplications (no sync)

Each type: POST (create), GET (list), GET /{id}, PUT /{id}, DELETE /{id}, POST /sync (where applicable).

DocumentType: Spreadsheet=0, Csv=1, Document=2, Presentation=3. Inferred from file extension on create.
CreateDocumentSessionRequest: { filePath, name?, description? }. DocumentSessionResponse: { id, name, filePath, documentType, description, createdAt, updatedAt }.
CreateNativeApplicationRequest: { name, executablePath, alias?, description? }. NativeApplicationResponse: { id, name, executablePath, alias, description, createdAt, updatedAt }.

Resource lookup: GET /resources/lookup/{type} → [{id, name}]
  Valid types: dangerousShellAccesses, safeShellAccesses, containerAccesses, websiteAccesses, searchEngineAccesses, localInfoStoreAccesses, externalInfoStoreAccesses, inputAudioAccesses, displayDeviceAccesses, editorSessionAccesses, agentAccesses, taskAccesses, skillAccesses, documentSessionAccesses, nativeApplicationAccesses.

────────────────────────────────────────
EDITOR BRIDGE
────────────────────────────────────────
WS  /editor/ws         WebSocket for IDE extensions. Registration → request/response loop. 30s timeout.
GET /editor/sessions   List connected editor sessions.

EditorType: VisualStudio2026, VisualStudioCode, Other.

────────────────────────────────────────
MODULE SYSTEM
────────────────────────────────────────
Tools are organized into loadable modules. Each module has an ID, a tool prefix,
and a set of tool definitions. Module tools are dispatched via AgentActionType = ModuleAction (100).

Module manifests stored at modules/{dir}/module.json in published output.
Tool names are prefixed: {prefix}_{toolName} (e.g. cu_capture_display, oa_read_range).

Default modules:
  Computer Use  id=sharpclaw_computer_use  prefix=cu  13 tools  Windows only
    Desktop awareness, window management, input simulation, clipboard, display capture, process control.
    Exports: window_management (IWindowManager), desktop_input (IDesktopInput).
    Tools: cu_capture_display, cu_click_desktop, cu_type_on_desktop, cu_enumerate_windows,
           cu_launch_application, cu_focus_window, cu_close_window, cu_resize_window,
           cu_send_hotkey, cu_capture_window, cu_read_clipboard, cu_write_clipboard, cu_stop_process.
    CLI: cu windows | cu displays | cu apps (aliases: computer-use)

  Office Apps  id=sharpclaw_office_apps  prefix=oa  10 tools  Windows/Linux/macOS
    Document sessions, spreadsheet CRUD (ClosedXML/CsvHelper), live Excel COM Interop (Windows only).
    Tools: oa_register_document, oa_read_range, oa_write_range, oa_list_sheets,
           oa_create_sheet, oa_delete_sheet, oa_get_info, oa_create_workbook,
           oa_live_read_range, oa_live_write_range.
    CLI: docs list (aliases: office, oa)

  mk8.shell  id=sharpclaw_mk8shell  prefix=mk8  2 tools  All platforms
    Sandboxed mk8.shell script execution and sandbox container lifecycle.
    Tools: mk8_execute_mk8_shell, mk8_create_mk8_sandbox.

  Dangerous Shell  id=sharpclaw_dangerous_shell  prefix=ds  1 tool  All platforms
    Unsandboxed real shell execution (Bash, PowerShell, Cmd, Git) — clearance-gated.
    Tools: ds_execute_dangerous_shell.

  Database Access  id=sharpclaw_database_access  prefix=db  3 tools  All platforms
    Register and query internal/external databases (PostgreSQL, MySQL, SQLite, MSSQL, MongoDB, Redis).
    Tools: db_register_database, db_access_internal_databases, db_access_external_database.
    CLI: db list-internal | db list-external (aliases: database-access)

  Transcription  id=transcription  prefix=(none — uses core AgentActionType 17-19)  3 tools  Windows (audio capture)
    Live audio transcription, input audio device management, and STT provider integration.
    Owns: TranscriptionService, LiveTranscriptionOrchestrator, STT clients (OpenAI Whisper, Groq, Local), WASAPI audio capture, WhisperModelManager, LanguageScriptValidator.
    Core interface: ILiveTranscriptionOrchestrator decouples AgentJobService from module.
    Permission delegation: DelegateTo:"AccessInputAudioAsync" routes through AgentActionService.
    Execution: IsTranscriptionAction → StartTranscriptionAsync (long-running, stays Executing).
    Tools: transcribe_from_audio_device, transcribe_from_audio_stream, transcribe_from_audio_file.
    CLI: resource inputaudio (alias ia) with list/get/add/update/delete/default sub-commands.
    Seeds default input audio device on startup via SeedDataAsync.

Runtime enable/disable:
  Modules can be toggled at runtime without restart. DI services are pre-registered for all modules.
  Enable: Register + CacheManifest + check unsatisfied deps + InitializeAsync (rollback on failure).
  Disable: safety check (reject if other modules depend on exports) + ShutdownAsync + Unregister.
  State persisted in DB + .modules.env.
  REST: GET /modules, GET /modules/{moduleId}, POST /modules/{moduleId}/enable, POST /modules/{moduleId}/disable.
  CLI: module list | module get <id> | module enable <id> | module disable <id>.

ModuleManifest fields: id, displayName, version, toolPrefix, entryAssembly, minHostVersion?,
  author?, description?, license?, platforms[], enabled, defaultEnabled, executionTimeoutSeconds?,
  exports[{contractName, serviceType}], requires[string].

Tool resolution order: core tools first, then module tools (prefix lookup via ModuleRegistry).
Modules can export/require named contracts; dependency graph resolved via topological sort at startup.
Disabling a module that exports a required contract is rejected (409 Conflict).

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
      "canReadCrossThreadHistory": false,
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

  Optional: pass language for strict enforcement — prompt is seeded in target language, up to 4 retries on mismatch:

    POST /channels/CHANNEL_ID/jobs
    {
      "actionType": "TranscribeFromAudioDevice",
      "language": "en"
    }

  Optional: use StrictWindow mode for cheap, sequential transcription:

    POST /channels/CHANNEL_ID/jobs
    {
      "actionType": "TranscribeFromAudioDevice",
      "language": "en",
      "transcriptionMode": "StrictWindow"
    }

  Optional: custom sliding window timing (12s window, 4s step):

    POST /channels/CHANNEL_ID/jobs
    {
      "actionType": "TranscribeFromAudioDevice",
      "transcriptionMode": "SlidingWindow",
      "windowSeconds": 12,
      "stepSeconds": 4
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

Core .env keys: Encryption:Key, Jwt:Secret, Jwt:AccessTokenLifetime, Jwt:RefreshTokenLifetime, ConnectionStrings:Postgres, Api:ListenUrl, Admin:Username, Admin:Password, Admin:ReconcilePermissions, Browser:Executable, Browser:Arguments, Local:GpuLayerCount, Local:ContextSize, Local:KeepLoaded, Local:IdleCooldownMinutes, EnvEditor:AllowNonAdmin, Backend:Enabled, Auth:DisableApiKeyCheck, Auth:DisableAccessTokenCheck, Agent:DisableCustomProviderParameters.
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

tools: Dictionary<string, bool>. Keys are tool names (e.g. "execute_mk8_shell", "cu_capture_display"). Tools whose key is true or absent are included; only tools explicitly set to false are excluded. Empty dict {} = all tools enabled.

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

Available tool names:

Core tools (34):
  wait, list_accessible_threads, read_thread_history,
  execute_mk8_shell, execute_dangerous_shell,
  transcribe_from_audio_device, transcribe_from_audio_stream, transcribe_from_audio_file,
  create_sub_agent, create_container, register_info_store,
  access_localhost_in_browser, access_localhost_cli,
  access_local_info_store, access_external_info_store,
  access_website, query_search_engine, access_container,
  manage_agent, edit_task, access_skill,
  editor_read_file, editor_get_open_files, editor_get_selection, editor_get_diagnostics,
  editor_apply_edit, editor_create_file, editor_delete_file,
  editor_show_diff, editor_run_build, editor_run_terminal,
  send_bot_message,
  task_view_info, task_view_source, task_output

Computer Use module tools (cu_ prefix, 13):
  cu_capture_display, cu_click_desktop, cu_type_on_desktop,
  cu_enumerate_windows, cu_launch_application,
  cu_focus_window, cu_close_window, cu_resize_window, cu_send_hotkey,
  cu_capture_window, cu_read_clipboard, cu_write_clipboard, cu_stop_process

Office Apps module tools (oa_ prefix, 10):
  oa_register_document, oa_read_range, oa_write_range,
  oa_list_sheets, oa_create_sheet, oa_delete_sheet,
  oa_get_info, oa_create_workbook,
  oa_live_read_range, oa_live_write_range
