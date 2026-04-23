SharpClaw Core CLI — Agent Skill Reference

Interactive REPL embedded in the backend process. Dispatches directly to
the same handlers as the REST API — no HTTP round-trip.

Launch: run the backend binary in an interactive terminal (stdin not
redirected). Headless/detached launches skip the REPL automatically.

────────────────────────────────────────
SESSION STATE
────────────────────────────────────────
Current user    — set on login/register; cleared on logout.
Current channel — set by channel add (auto) or channel select; used
                  as default by chat, thread, job list, task start.
Current thread  — set by thread select; deselected by thread deselect
                  or channel select. When set, chat includes history.
Chat mode       — toggled by chat toggle; all input → chat. Escape: !exit.

All commands require login except: register, login, help.

────────────────────────────────────────
ID SYSTEM
────────────────────────────────────────
Short IDs (#1, #2, …) assigned on first print; reset on process restart.
# prefix is optional. All forms accepted: #5  5  550e8400-e29b-41d4-a716-446655440000

CliIdMap.Resolve accepts all three. Unknown short ID → ArgumentException.
All JSON output injects "# <int>" before each "Id" GUID field.
Module handlers access the same system via ICliIdResolver DI service.

Module IDs are strings, not GUIDs — short IDs do not apply.

────────────────────────────────────────
ARGUMENT PARSING
────────────────────────────────────────
Space-separated. Double-quoted strings are single tokens (quotes stripped).
  agent add "My Agent" #3 "You are helpful" --max-tokens 1024

────────────────────────────────────────
AUTH
────────────────────────────────────────
register <user> <pass>              Register + auto-login
login <user> <pass> [--remember]    Login; --remember issues refresh token
logout                              Clear session
me                                  Show current user + role

────────────────────────────────────────
PROVIDER
────────────────────────────────────────
provider add <name> <type> [endpoint]
  Types: OpenAI, Anthropic, OpenRouter, GoogleVertexAI, GoogleVertexAIOpenAi,
         GoogleGemini, GoogleGeminiOpenAi, ZAI, VercelAIGateway, XAI, Groq,
         Cerebras, Mistral, GitHubCopilot, Minimax, Custom
  endpoint required for Custom only.
provider get <id> | list | update <id> <name> [endpoint] | delete <id>
provider set-key <id> <apiKey>
provider login <id>                         OAuth device-code flow (interactive)
provider sync-models <id>                   Import model list + refresh caps
provider refresh-caps <id>                  Re-infer caps without fetching list
provider cost <id> [--days <n>]             Default 30 days
provider cost-total [--days <n>] [--simple] [--all]

────────────────────────────────────────
MODEL
────────────────────────────────────────
model add <name> <providerId> [--cap <capabilities>]
  <name> = exact provider model ID (gpt-4o, claude-sonnet-4-20250514, …)
  Capabilities (comma-separated): Chat, Transcription, ImageGeneration,
    Embedding, TextToSpeech. Default: Chat.
  Prefer: provider sync-models <id>
model get <id> | list [--provider <id>] | update <id> <name> [--cap …] | delete <id>

Local models:
model download <url> [--name <alias>] [--quant <Q4_K_M>]
               [--gpu-layers <n>] [--provider <LlamaSharp|Whisper>]
  Omit --provider → register with both LlamaSharp and Whisper.
  --gpu-layers has no effect for Whisper.
model download list <url>              List available GGUF files at URL
model load <id> [--gpu-layers <n>] [--ctx <size>] [--mmproj <path>]
  Pins model in memory (stays loaded between requests).
model unload <id>                      Unpin; stops if no active requests.
model mmproj <id> <path|none>          Set/clear CLIP mmproj for vision model.
model local list

────────────────────────────────────────
AGENT
────────────────────────────────────────
agent add <name> <modelId> [system prompt] [flags...]
  Positional after modelId = system prompt (any text not matching a flag).
  If modelId resolves to a local model file, auto-resolved to parent model.
agent get <id> | list | update <id> <name> [modelId] [system prompt] [flags...] | delete <id>
  update: first optional positional tested as modelId; remaining = prompt.
agent role <id> <roleId|none>          Assign or remove role.
agent sync-with-models                 Create default-<model> agents for
                                       all chat-capable models lacking one.

Inference flags (add / update):
  --max-tokens <n>              Cap on tokens generated per response
  --temperature <f> / --temp    Sampling temperature
  --top-p <f>                   Nucleus sampling
  --top-k <n>                   Top-k sampling
  --frequency-penalty <f>
  --presence-penalty <f>
  --stop <s1,s2>                Stop sequences (comma-separated)
  --seed <n>
  --response-format <json>      Structured output format (JSON string)
  --reasoning-effort <s>        e.g. low, high
  --params <json>               Raw provider parameter overrides
  --header <template>           Custom chat header template
  --tools <setId>               Tool awareness set
  --no-tools                    Disable all tool schemas

────────────────────────────────────────
CONTEXT  (alias: ctx)
────────────────────────────────────────
context add <agentId> [name]
context get <id> | list [agentId] | update <id> <name> | delete <id>
context agents <id>
context agents <id> add <agentId>
context agents <id> remove <agentId>
context defaults <id>
context defaults <id> set <key> <resourceId>      Use 'all' for wildcard.
context defaults <id> clear <key>

Default-resource keys (same for channel):
  safeshell, dangshell/dangerousshell, container, website,
  search/searchengine, internaldb, externaldb, inputaudio/audio,
  displaydevice/display, agent, task, skill,
  transcriptionmodel/model, editorsession/editor

────────────────────────────────────────
CHANNEL  (alias: chan)
────────────────────────────────────────
channel add [--agent <id>] [--context <id>] [--header <template>]
            [--tools <setId>] [--no-tools] [title]
  Either --agent or --context required. Auto-selects on creation.
channel get <id> | list [agentId] | update <id> … | delete <id>
channel select <id>                    Set active channel; deselects thread.
channel cost <id>                      Token usage by agent.
channel attach <id> <contextId>
channel detach <id>
channel agents <id> [add|remove <agentId>]
channel defaults <id> [set <key> <resId> | clear <key>]
  Channel defaults override context defaults for this channel only.

Cascade from context when unset: agent, permissions,
  DisableChatHeader, AllowedAgents, DefaultResourceSet.

────────────────────────────────────────
THREAD
────────────────────────────────────────
thread add [channelId] [name] [--max-messages <n>] [--max-chars <n>]
  Uses active channel if omitted. Defaults: 50 msgs, 100 000 chars.
  Oldest messages trimmed first. Set 0 to reset to system default.
thread get <id> | list [channelId] | cost <id>
thread update <id> [--name <n>] [--max-messages <n>] [--max-chars <n>]
thread select <id>                     Subsequent chat includes history.
thread deselect                        Revert to one-shot mode.
thread delete <id>

────────────────────────────────────────
CHAT
────────────────────────────────────────
chat [--agent <id>] [--thread <id>] <message>
  No thread → one-shot (no history sent to model).
  With thread → history included (trimmed to thread limits).
  No channel selected → auto-selects latest active channel (with notice).

Thread inference: if first positional looks like a short ID or GUID,
  CLI checks if it's a thread (infer channel) or a channel (select it).
  chat #5 What is the status?  →  sends to thread #5

Streaming output:
  Text delta     → printed char-by-char
  [tool]         → #N actionKey → status
  [result]       → #N → status
  [approval]     → interactive y/n prompt
  [error]        → stderr

chat toggle                            Toggle chat mode. All input → chat.
Escape chat mode: !exit  or  !chat toggle

────────────────────────────────────────
JOB
────────────────────────────────────────
job submit <channelId> <actionKey> [resourceId]
           [--agent <id>] [--model <id>] [--lang <code>]
           [--mode <sliding|step|window>]
           [--window <seconds>] [--step <seconds>]
  actionKey = module tool name. Valid keys are dynamic (module list).
  resourceId omitted → resolved from DefaultResourceSet cascade.
  --mode/--window/--step: transcription jobs only.
  --model: transcription model override.
job list [channelId]                   Uses active channel if omitted.
job status <id>
job approve <id>
job stop <id>                          Graceful stop (also accepts Paused).
job cancel <id>                        Abort immediately.
job pause <id>
job resume <id>
job listen <id>                        Stream live transcription segments.
                                       Ctrl+C stops listening (not the job).

AgentJobStatus: Queued, Executing, AwaitingApproval, Completed, Failed,
                Denied, Cancelled, Paused.

────────────────────────────────────────
TASK
────────────────────────────────────────
Definitions (compiled C# scripts):
task create <sourceFilePath>           Validates before upload; prints diagnostics.
task list | get <id>
task update <id> <sourceFilePath>      Validates before upload.
task activate <id> | deactivate <id>
task delete <id>

Instances:
task start <taskId> [channelId] [--param key=value ...]   Uses active channel.
task run <taskId> …                    Alias for start.
task create-queued <taskId> [channelId] [--param key=value ...]
task start-instance <instanceId>       Start an existing queued instance.
task instances <taskId>                List instances for a definition.
task instance <instanceId>
task outputs <instanceId> [--since <timestamp>]
task cancel <instanceId>
task stop <instanceId>
task pause <instanceId>
task resume <instanceId>
task listen <instanceId>               Stream output events (output/log/status/done).
                                       Ctrl+C stops listening (not the instance).

────────────────────────────────────────
ROLE
────────────────────────────────────────
role list | get <id>
role permissions <id>                  Show role permissions.
role permissions <id> set [flags...]   Full replacement.

Global capability flags:
  --create-sub-agents    --create-containers    --register-databases
  --localhost-browser    --localhost-cli        --click-desktop
  --type-on-desktop      --read-cross-thread-history
  --edit-agent-header    --edit-channel-header  --create-document-sessions
  --enumerate-windows    --focus-window         --close-window
  --resize-window        --send-hotkey          --read-clipboard
  --write-clipboard
  Default clearance: Independent. Append :<clearance> to override.
  Arbitrary flags: --flag <FlagKey>[:<clearance>]

Resource grant flags (each accepts <id>[:<clearance>], 'all' for wildcard):
  --dangerous-shell  --safe-shell  --container  --website  --search-engine
  --internal-db  --external-db  --input-audio  --agent  --task  --skill

Clearance levels (ascending):
  Unset, ApprovedBySameLevelUser, ApprovedByWhitelistedUser,
  ApprovedByPermittedAgent, ApprovedByWhitelistedAgent, Independent.

Example:
  role permissions #2 set --create-sub-agents --safe-shell all:Independent

────────────────────────────────────────
USER  (admin only)
────────────────────────────────────────
user list
user role <userId> <roleId|none>       Assign or remove a user's role.

────────────────────────────────────────
BIO
────────────────────────────────────────
bio get | bio set <text> | bio clear
  Bio appears in the chat header so agents know who they are talking to.

────────────────────────────────────────
TOOL AWARENESS SETS
────────────────────────────────────────
Control which tool schemas are sent to the model. Reduces prompt-token cost.

tools add <name> [json]                json: {"tool_name": true, ...}
tools list | get <id>                  Omitted tools default to enabled.
tools update <id> [--name <n>] [json]
tools delete <id>

Override chain: channel → agent → null (all tools enabled).
Assign: agent add/update --tools <setId>  |  channel add/update --tools <setId>
Clear:  pass Guid.Empty as setId on update.
Tool names are dynamic — use module list for the authoritative list.

────────────────────────────────────────
RESOURCE
────────────────────────────────────────
resource <type> <command> [args...]
  All types are module-provided. Valid types depend on enabled modules.
  Commands: add, get, list, update, delete, sync (where supported).
  sync: imports from system / local registry.
  Use: help  or  module list  to see available types at runtime.

────────────────────────────────────────
MODULE
────────────────────────────────────────
module list | get <id>
module enable <id> | disable <id>      Runtime toggle; no restart required.
  disable rejected (409) if another module depends on an exported contract.
module scan                            Discover + load external modules.
module reload <id>                     Unload + re-load single external module.
module unload <id>

Module IDs are strings (e.g. sharpclaw_transcription). Not short-ID eligible.

────────────────────────────────────────
ENV
────────────────────────────────────────
Manages Infrastructure/Environment/.env. Changes require backend restart.

env get                                Print raw JSON content.
env set                                Write from stdin (blank line = end).
env auth                               Pre-check: is current user authorised?
env status                             Encrypted (AES-GCM) or plaintext?
env unlock                             Decrypt in-place; re-encrypts on restart.

Real authorisation enforcement is server-side (auth status, admin flag, or
EnvEditor:AllowNonAdmin=true in .env).

────────────────────────────────────────
DB  (relational providers only)
────────────────────────────────────────
Not available for the default JSON file provider.

db status                              Gate state + applied/pending migrations.
db migrate                             Drain requests, apply pending migrations.
  Migrations are NEVER automatic. Must be triggered explicitly.
  Gate states: Idle | Draining | Migrating

────────────────────────────────────────
HEALTH
────────────────────────────────────────
health
  Runs JsonPersistenceHealthCheck. Prints each component status.
  ✓ Healthy  ⚠ Degraded  ✗ Unhealthy
  Checks: disk writable, pending transactions, quarantine count,
          flush backlog, checksums, event log, snapshot age, sentinel.

────────────────────────────────────────
MODULE-PROVIDED CLI COMMANDS
────────────────────────────────────────
Modules register in two scopes:
  TopLevel     — new root command handled by TryHandleAsync fallback.
  ResourceType — new sub-command under resource <type>.

help lists all active module commands with scope prefix, description,
aliases, and module ID.

Handler signature: (string[] args, IServiceProvider sp, CancellationToken ct)
ID access: ICliIdResolver.Resolve / GetOrAssign / PrintJson

See docs/modules/ for per-module CLI details.
Key example: resource inputaudio (alias ia) — Transcription module.
