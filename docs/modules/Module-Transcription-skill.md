SharpClaw Module: Transcription — Agent Skill Reference

Module ID: sharpclaw_transcription
Display Name: Transcription
Tool Prefix: tr
Version: 1.0.0
Platforms: Windows only
Exports: transcription_stt, transcription_audio_capture
Requires: none

────────────────────────────────────────
ENABLING
────────────────────────────────────────
.env key: Modules:sharpclaw_transcription
Default: disabled
Prerequisites: none
Platform: Windows only (WASAPI audio capture)

To enable, add to your core .env (Infrastructure/Environment/.env) Modules section:
  "sharpclaw_transcription": "true"

To disable, set to "false" or remove the key (missing = disabled).

Exports: transcription_stt, transcription_audio_capture.

Runtime toggle (no restart required):
  module disable sharpclaw_transcription
  module enable sharpclaw_transcription

See docs/Module-Enablement-Guide.md for full details.

────────────────────────────────────────
OVERVIEW
────────────────────────────────────────
Live audio transcription, input audio device management, and STT provider
integration. WASAPI audio capture (Windows only), normalised to mono
16 kHz 16-bit PCM. Providers: OpenAI Whisper, Groq, local Whisper.net.

Tools are dispatched via the module system. Transcription jobs are
intercepted by the core AgentJobService and routed to
ILiveTranscriptionOrchestrator.StartTranscriptionAsync.

────────────────────────────────────────
ENUMS
────────────────────────────────────────
TranscriptionMode:
  SlidingWindow (0) — two-pass, provisional then finalized after commit delay.
  StrictWindow (2)  — non-overlapping sequential windows, final-only.

────────────────────────────────────────
TOOLS (3)
────────────────────────────────────────

tr_transcribe_audio_device
  Start live transcription from an input audio device.
  Alias: tr_transcribe_from_audio_device
  Tool schema: resource_id (audio device GUID, required)
  Job-level: transcriptionModelId (GUID, optional — override model),
             language (string, optional — BCP-47 hint e.g. "en", "de"),
             transcriptionMode (string, optional — "SlidingWindow"|"StrictWindow"),
             windowSeconds (int, optional — 5-15, default 10),
             stepSeconds (int, optional — 1-window, default 2, SlidingWindow only)
  Permission: per-resource (InputAudioDevice)

tr_transcribe_audio_stream
  Start live transcription from an audio stream.
  Params: same as tr_transcribe_audio_device
  Permission: per-resource (InputAudioDevice)

tr_transcribe_audio_file
  Transcribe an audio file.
  Params: same as tr_transcribe_audio_device
  Permission: per-resource (InputAudioDevice)

────────────────────────────────────────
TRANSCRIPTION MODES
────────────────────────────────────────
SlidingWindow (default):
  Provisional → Finalized (commit delay 2s) or stale-finalized (2× delay).
  First inference at 1s (fast-start), then step interval ~2s. Low latency.

StrictWindow:
  Non-overlapping windows (default 10s). One API call per window.
  All segments final. Minimal token cost. Latency = window length.

Pipeline constants: WindowSeconds=10, InferenceInterval=2,
  BufferCapacity=15, CommitDelay=2.0, MaxPromptChars=250, SampleRate=16000.

────────────────────────────────────────
STREAMING
────────────────────────────────────────
WebSocket: /jobs/{jobId}/ws
SSE:       /jobs/{jobId}/stream

Both send transcription segment JSON. 404 if job not found.

────────────────────────────────────────
CLI
────────────────────────────────────────
resource inputaudio add <name> [identifier] [description]
resource inputaudio get <id>
resource inputaudio list
resource inputaudio update <id> [name] [identifier]
resource inputaudio delete <id>
resource inputaudio sync            Import system input audio devices

Aliases: ia

────────────────────────────────────────
RESOURCE DEPENDENCIES
────────────────────────────────────────
- InputAudio — for all transcription tools

────────────────────────────────────────
ROLE PERMISSIONS (relevant)
────────────────────────────────────────
Per-resource: TrAudio (InputAudio)

────────────────────────────────────────
EXPORTED CONTRACTS
────────────────────────────────────────
transcription_stt          → ITranscriptionApiClient
transcription_audio_capture → IAudioCaptureProvider
