# SharpClaw Module: Transcription

> **Module ID:** `sharpclaw_transcription`
> **Display Name:** Transcription
> **Version:** 1.0.0
> **Tool Prefix:** `tr`
> **Platforms:** Windows only
> **Exports:** `transcription_stt` (`ITranscriptionApiClient`), `transcription_audio_capture` (`IAudioCaptureProvider`)
> **Requires:** none

---

## How to Enable

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_transcription` |
| **Default** | ❌ Disabled |
| **Prerequisites** | None |
| **Platform** | Windows only (WASAPI audio capture) |

To enable, add to your core `.env` (`Infrastructure/Environment/.env`) Modules section:

```jsonc
"sharpclaw_transcription": "true"
```

To disable, set to `"false"` or remove the key (missing = disabled).

> **Note:** Exports `transcription_stt` and `transcription_audio_capture` contracts.

**Runtime toggle** (no restart required):

```
module disable sharpclaw_transcription
module enable sharpclaw_transcription
```

See [Module Enablement Guide](Module-Enablement-Guide.md) for full details.

---

## Overview

The Transcription module provides live audio transcription, input audio
device management, and STT (speech-to-text) provider integration. Audio
is captured via WASAPI (Windows only), normalised to mono 16 kHz 16-bit
PCM, and sent to transcription providers (OpenAI Whisper, Groq, or
local Whisper.net).

Tools are dispatched via the SharpClaw module system
(`AgentActionType = ModuleAction`). Tool names are prefixed with `tr_`
when sent to the model. However, transcription jobs use the core
`IsTranscriptionAction` path in `AgentJobService` — they are
intercepted before dispatch reaches the module and routed to
`ILiveTranscriptionOrchestrator.StartTranscriptionAsync`.

---

## Table of Contents

- [Enums](#enums)
- [Tools](#tools)
  - [tr_transcribe_audio_device](#tr_transcribe_audio_device)
  - [tr_transcribe_audio_stream](#tr_transcribe_audio_stream)
  - [tr_transcribe_audio_file](#tr_transcribe_audio_file)
- [Transcription Modes](#transcription-modes)
- [Audio Pipeline](#audio-pipeline)
- [Deduplication Pipeline](#deduplication-pipeline)
- [Language Enforcement](#language-enforcement)
- [Streaming Transports](#streaming-transports)
- [CLI Commands](#cli-commands)
- [Resource Dependencies](#resource-dependencies)
- [Role Permissions](#role-permissions)
- [Exported Contracts](#exported-contracts)
- [Module Manifest](#module-manifest)

---

## Enums

### TranscriptionMode

| Value | Int | Description |
|-------|-----|-------------|
| `SlidingWindow` | 0 | Two-pass sliding window (default). Segments are emitted provisionally, then finalized after commit delay. |
| `StrictWindow` | 2 | Non-overlapping sequential windows. Each window transcribed exactly once. Minimal token cost; perceived latency equals window length. |

---

## Tools

### tr_transcribe_audio_device

Start live transcription from an input audio device.

**Alias:** `tr_transcribe_from_audio_device`

**Tool Schema Parameter:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `resource_id` | string (GUID) | yes | Input audio device resource GUID |

**Job-level Parameters** (set via job submission / channel / context defaults):

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `transcriptionModelId` | string (GUID) | no | Override model (defaults from channel → context) |
| `language` | string | no | BCP-47 language hint (e.g. `"en"`, `"de"`, `"ja"`) |
| `transcriptionMode` | string | no | `"SlidingWindow"` (default) or `"StrictWindow"` |
| `windowSeconds` | integer | no | Audio window per inference tick (5–15, default 10) |
| `stepSeconds` | integer | no | Step between ticks, SlidingWindow only (1–window, default 2) |

**Permission:** Per-resource — requires `audioDeviceAccesses` grant.

**Returns:** Transcription segments (streamed via WebSocket or SSE).

---

### tr_transcribe_audio_stream

Start live transcription from an audio stream.

**Parameters:** Same as `tr_transcribe_audio_device`.

**Permission:** Per-resource — requires `audioDeviceAccesses` grant.

---

### tr_transcribe_audio_file

Transcribe an audio file.

**Parameters:** Same as `tr_transcribe_audio_device`.

**Permission:** Per-resource — requires `audioDeviceAccesses` grant.

---

## Transcription Modes

### SlidingWindow (default)

Two-pass pipeline. Segments go through a lifecycle:

1. **Provisional** — emitted within one inference tick (~2 s).
   `isProvisional: true`.
2. **Finalized** — confirmed after commit delay (2 s). Same `id`,
   updated text/confidence, `isProvisional: false`.
3. **Stale finalization** — provisionals older than 2× commit delay
   are auto-finalized with their existing text to prevent indefinite
   provisional state.

The first inference fires after **1 s** (fast-start) rather than the
normal poll interval so users hear feedback sooner. The two-pass
lifecycle corrects any inaccuracies from short initial audio clips.

### StrictWindow

Non-overlapping sequential windows (default 10 s). Each window
transcribed exactly once — one API call per window. All segments are
final on first emission (`isProvisional` always `false`).

### Pipeline Constants

| Constant | Value | Description |
|----------|-------|-------------|
| `WindowSeconds` | 10 | Default audio window per inference tick |
| `InferenceIntervalSeconds` | 2 | Default step between ticks |
| `BufferCapacitySeconds` | 15 | Ring buffer size / max window clamp |
| `CommitDelaySeconds` | 2.0 | Delay before provisional → finalized |
| `MaxPromptChars` | 250 | Rolling prompt buffer size for Whisper |
| `SampleRate` | 16 000 | Audio sample rate (mono PCM) |

---

## Audio Pipeline

### AudioNormalizer

Ensures all audio is in the optimal format for Whisper / ASR:
mono, 16 kHz sample rate, 16-bit PCM, WAV container. Pipeline:
input stream → decode → stereo→mono → resample→16 kHz → write
16-bit PCM WAV. If the input already matches the target format the
bytes are returned unchanged (fast path). Applied by both
`OpenAiTranscriptionApiClient` and `LocalTranscriptionClient` before
each inference call.

### AudioRingBuffer

Thread-safe (single-writer, multi-reader) ring buffer of float PCM
samples. Pre-allocated to `BufferCapacitySeconds` (15 s) and wraps
around — older samples are silently overwritten. The WASAPI capture
callback writes via `Write(ReadOnlySpan<float>)` with volatile
publish semantics; inference loops read via `GetLastSeconds(int)`.

### AudioVad

Lightweight energy-based Voice Activity Detection. Computes RMS energy
over a span of float PCM samples and compares against a configurable
silence threshold (`DefaultSilenceThreshold = 0.005`). Used as a
pre-filter to avoid sending purely silent audio to the transcription
API. Does not replace Whisper's own `no_speech_prob` detection — both
are used in tandem.

### SharedAudioCaptureManager

Singleton that manages shared WASAPI capture sessions per device.
Multiple transcription jobs targeting the same device share a single
capture task and `AudioRingBuffer` instead of each opening its own.
`Acquire()` starts capture on first subscriber (returns the shared
ring buffer); `ReleaseAsync()` stops capture when the last subscriber
releases. Includes health monitoring via `GetCaptureStatus()`.

### Startup Verification

Before entering the main inference loop, the orchestrator verifies
audio is actually flowing from the device:

- Polls for up to **5 s** (100 ms intervals) waiting for the first
  sample to arrive in the ring buffer.
- Checks `SharedAudioCaptureManager.GetCaptureStatus()` each poll to
  detect early capture failures.
- Throws if no data is received within the timeout.

### Data-flow Watchdog

During the inference loop, a watchdog detects when the audio capture
has stalled or the device has stopped producing samples:

- Tracks `ringBuffer.TotalWritten` between poll ticks.
- After **10 consecutive** ticks with no new data, the job is aborted.
- Each stalled tick also checks `GetCaptureStatus()` for capture task
  faults.

### Step-region Silence Check

In SlidingWindow mode (where `step < window`), most audio was already
processed last tick. When the **new portion** (last `stepSeconds`
seconds) is silent (RMS < 0.005), the full-window inference API call
is skipped entirely. This gate is bypassed on tick 1 since all
captured audio is new.

---

## Deduplication Pipeline

When the STT API returns the full window as a single text blob without
per-word timestamps (e.g. `gpt-4o-transcribe`):

1. **Text diff** — compares current response against previous; uses
   containment check (strict for < 10 words, 10% fuzzy for longer) and
   suffix-prefix overlap to extract new content.
2. **Context tracking** — `previousWindowText` updated carefully to
   prevent context loss.
3. **Sentence splitting** — multiple sentences split at boundaries with
   proportional timestamp distribution.
4. **Fragment merge** — short residuals (≤ 2 words, lowercase) merged
   into most recent provisional segment.
5. **Emitted-text guard** — HashSet tracks all emitted texts; duplicates
   skipped (catches Whisper hallucination replay).
6. **Timestamp guard** — segments with `absEnd ≤ lastSeenEnd` skipped.

---

## Language Enforcement

When `language` is set:

- Whisper prompt is seeded with a natural phrase from an embedded
  `transcription-language-seeds.json` covering 99 Whisper-supported
  languages.
- `LanguageScriptValidator.GetPromptSeed(language)` provides the
  initial seed used at the start of the sliding-window loop.
- `LanguageScriptValidator.GetReinforcedPrompt()` supports 4
  escalation levels (single seed → triple → instruction preamble →
  max saturation block) for retry scenarios. **Currently unused** by
  the orchestrator — only the initial prompt seed is applied.
- `ResponseLanguageMatches()` normalises BCP-47 tags to their base
  subtag for comparison.
- If auto-detected language (`effectiveLanguage`) is null and the API
  response includes a language tag, the orchestrator adopts it for
  subsequent ticks.

---

## Streaming Transports

### WebSocket

```
/jobs/{jobId}/ws
```

Server sends JSON text frames with transcription segment objects.

### SSE

```
/jobs/{jobId}/stream
```

Server-sent events with segments in `data` frames.

Both return `404` if the job is not found or has no active subscription.

---

## CLI Commands

The module registers an `inputaudio` resource command (alias: `ia`):

```
resource inputaudio add <name> [identifier] [description]
resource inputaudio get <id>
resource inputaudio list
resource inputaudio update <id> [name] [identifier]
resource inputaudio delete <id>
resource inputaudio sync            Import system input audio devices
```

---

## Resource Dependencies

| Resource Type | Used by |
|---------------|---------|
| Input Audio Devices | All transcription tools |

Input audio devices can be synced via
`POST /resources/inputaudios/sync` or `resource inputaudio sync`.

A default input audio device ("Default", identifier `"default"`) is
seeded on module startup.

---

## Role Permissions

### Per-resource arrays

| Array | Resource Type | Tools |
|-------|---------------|-------|
| `audioDeviceAccesses` | InputAudioDevices | All transcription tools |

---

## Exported Contracts

| Contract Name | Interface | Description |
|---------------|-----------|-------------|
| `transcription_stt` | `ITranscriptionApiClient` | Speech-to-text transcription via provider APIs |
| `transcription_audio_capture` | `IAudioCaptureProvider` | Audio capture from input devices |

---

## Audio / Concurrency Contract

- One job = one orchestrator task (`RunSlidingWindowLoopAsync`).
- Audio flows through a shared `AudioRingBuffer` (float ring buffer,
  single-writer / multi-reader) managed by `SharedAudioCaptureManager`.
- Multiple jobs on the same device share one WASAPI capture task and
  ring buffer via ref-counting.
- No `Task.Run` per chunk — the capture callback writes directly to
  the ring buffer.
- `CancellationTokenSource` per job; `StopAllAsync()` cancels all on
  module shutdown.
- Job failure path: status set to `Failed` with error log; capture
  session released in `finally`.
- OpenAI client uses 3 retries with exponential backoff on 429
  (initial backoff 2 s). Permanent `insufficient_quota` errors are
  not retried.
- Non-retryable 4xx errors in the inference loop abort the job
  immediately. Other errors accumulate up to 5 consecutive failures
  before aborting.

---

## Module Manifest

```json
{
  "id": "sharpclaw_transcription",
  "displayName": "Transcription",
  "version": "1.0.0",
  "toolPrefix": "tr",
  "entryAssembly": "SharpClaw.Modules.Transcription",
  "minHostVersion": "1.0.0",
  "platforms": ["windows"],
  "executionTimeoutSeconds": 120,
  "exports": [
    {
      "contractName": "transcription_stt",
      "serviceType": "SharpClaw.Modules.Transcription.Clients.ITranscriptionApiClient"
    },
    {
      "contractName": "transcription_audio_capture",
      "serviceType": "SharpClaw.Modules.Transcription.Clients.IAudioCaptureProvider"
    }
  ],
  "requires": []
}
```
