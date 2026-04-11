SharpClaw Module: Context Tools — Agent Skill Reference

Module ID: sharpclaw_context_tools
Display Name: Context Tools
Tool Prefix: ct
Version: 1.0.0
Platforms: All
Exports: none
Requires: none

────────────────────────────────────────
OVERVIEW
────────────────────────────────────────
Lightweight inline context tools that execute directly in the ChatService
streaming loop without creating job records. Results return to the model
in the same turn.

────────────────────────────────────────
TOOLS (3, all inline)
────────────────────────────────────────

ct_wait
  Pause for 1-300 seconds. No permissions needed. No tokens consumed.
  Params: seconds (int, required — 1-300)
  Permission: none

ct_list_accessible_threads
  List readable threads from other channels (IDs, names, parent info).
  Double-gate: agent role needs CanReadCrossThreadHistory AND target
  channel must opt in (unless agent has Independent clearance).
  Params: none
  Permission: global (ReadCrossThreadHistory)

ct_read_thread_history
  Read cross-channel thread history. Same double-gate requirement.
  Params: threadId (GUID, required), maxMessages (int, optional — 1-200, default 50)
  Permission: global (ReadCrossThreadHistory)

────────────────────────────────────────
ROLE PERMISSIONS (relevant)
────────────────────────────────────────
Global flags: canReadCrossThreadHistory
Cross-thread access: double-gate model (agent role + channel opt-in).
Independent clearance overrides channel opt-in.
