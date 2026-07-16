SharpClaw Module: Metrics — Agent Skill Reference

Module ID: sharpclaw_metrics
Display Name: Metrics
Tool Prefix: metric
Version: 1.0.0
Platforms: all
Exports: none
Requires: none

────────────────────────────────────────
ENABLING
────────────────────────────────────────
.env key: Modules:sharpclaw_metrics
Default: disabled in base .env, enabled in .dev.env
Prerequisites: none
Platform: all

To enable, add this assignment to the deployed Runtime Host's Environment/.env:
  Modules__sharpclaw_metrics="true"

To disable, set to "false" or remove the key (missing = disabled).

Runtime toggle (no restart required):
  module disable sharpclaw_metrics
  module enable sharpclaw_metrics

See docs/modules/Module-Enablement-Guide.md for full details.

────────────────────────────────────────
OVERVIEW
────────────────────────────────────────
Task-pipeline-only module. No LLM-callable tools.

Owns:
  - MetricThreshold task trigger (MetricTriggerSource).
  - Built-in ITaskMetricProvider implementations.
  - Package-owned MetricThreshold task trigger parsing.

Built-in providers consume IHostQueueMetrics from the host, so the
module has no direct database dependency.

────────────────────────────────────────
TRIGGERS
────────────────────────────────────────
MetricThreshold — fires when a registered ITaskMetricProvider's
                  current value crosses the configured threshold.

If this module is disabled, MetricThreshold triggers are flagged by
task preflight and removed from task trigger-sources.

────────────────────────────────────────
BUILT-IN METRIC PROVIDERS
────────────────────────────────────────
PendingJobCountMetricProvider           — pending agent jobs in host queue.
PendingTaskCountMetricProvider          — pending tasks awaiting orchestration.
SchedulerPendingJobCountMetricProvider  — scheduler queued but not dispatched.

Other modules may register additional ITaskMetricProvider services.

────────────────────────────────────────
TOOLS
────────────────────────────────────────
None. ExecuteToolAsync(...) throws InvalidOperationException.

────────────────────────────────────────
CLI
────────────────────────────────────────
No dedicated commands. Use the standard task command surface.
