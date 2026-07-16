# SharpClaw Module: Metrics

> **Module ID:** `sharpclaw_metrics`
> **Display Name:** Metrics
> **Version:** 1.0.0
> **Tool Prefix:** `metric`
> **Platforms:** All
> **Exports:** none
> **Requires:** none

---

## How to Enable

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_metrics` |
| **Default** | ❌ Disabled (base `.env`) — ✅ Enabled in `.dev.env` |
| **Prerequisites** | None |
| **Platform** | All |

To enable, add `Modules__sharpclaw_metrics="true"` to the deployed Runtime
Host's `Environment/.env`:

```dotenv
Modules__sharpclaw_metrics="true"
```

To disable, set to `"false"` or remove the key (missing = disabled).

**Runtime toggle** (no restart required):

```
module disable sharpclaw_metrics
module enable sharpclaw_metrics
```

See [Module-Enablement-Guide.md](Module-Enablement-Guide.md) for full
details.

---

## Overview

The Metrics module owns the `MetricThreshold` task trigger and the
built-in `ITaskMetricProvider` implementations. It has **no
LLM-callable tools** — it contributes purely to the task pipeline.

The module contributes the `MetricThreshold` trigger and the metric-provider
runtime surface that trigger uses. Task authors declare the trigger with normal
C# task attributes; the module does not add grammar constructs or teach the
task language new control-flow syntax.

The built-in providers consume `IHostQueueMetrics` (forwarded from the
host) so the module stays free of any direct database dependency.

---

## Task Triggers

| Trigger | Source | Description |
|---------|--------|-------------|
| `MetricThreshold` | `MetricTriggerSource` | Polls a registered `ITaskMetricProvider` and fires when its observed value crosses the configured threshold. |

When this module is disabled, `MetricThreshold` triggers are flagged by
`task preflight` and excluded from `task trigger-sources`.

---

## Built-in Metric Providers

| Provider | What it reports |
|----------|-----------------|
| `PendingJobCountMetricProvider`           | Current count of pending agent jobs in the host queue. |
| `PendingTaskCountMetricProvider`          | Current count of pending tasks awaiting orchestration. |
| `SchedulerPendingJobCountMetricProvider`  | Current count of jobs the scheduler has queued but not yet dispatched. |

Additional metric providers can be registered by other modules through
`ITaskMetricProvider`.

---

## CLI Commands

This module ships no dedicated CLI commands. Tasks that use
`MetricThreshold` are managed through the standard `task` command
surface.

---

## Tool Definitions

`GetToolDefinitions()` returns an empty list. Calling
`ExecuteToolAsync(...)` raises an `InvalidOperationException` —
attempts to dispatch a job-pipeline tool through this module are a
programming error.

---

## Module Manifest

```text
Id           = sharpclaw_metrics
DisplayName  = Metrics
ToolPrefix   = metric
Task trigger parsing = package-owned MetricThreshold support
Exported contracts = none
Required contracts = none (consumes IHostQueueMetrics from the host)
```
