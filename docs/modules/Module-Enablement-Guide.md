# Module Enablement Guide

SharpClaw modules are runtime feature packages discovered by the Core API at
startup. A module can add tools, REST endpoints, CLI commands, resource types,
task triggers, provider implementations, or editor integrations. The bundled
modules are restored from NuGet package payloads; external modules can be added
separately through the `ExternalModules` section in the Core env file.

The Core env file is `SharpClaw.Runtime/INF/Environment/.env`.
It uses JSON with comments and is read into `IConfiguration` during startup.
In development mode, `.dev.env` is loaded after `.env`, so the development file
can turn on modules without changing the base template. A module is enabled
only when its module id is explicitly set to `"true"` in `Modules`. A missing
key or a value of `"false"` keeps that module disabled.

Module-owned configuration uses the same generic env loader. If an enabled
module reads `IConfiguration["MyModule:EndpointUrl"]`, users can add a
top-level `"MyModule"` section to Core `.env` without any change to
`LocalEnvironment`, `ModuleLoader`, or the host startup code. The module owns
the section name, default values, validation, and documentation. The host only
loads the JSON tree and makes it available through DI.

For example, a third-party module can document this shape:

```jsonc
"MyModule": {
  "EndpointUrl": "https://example.internal/api",
  "RetrySeconds": "15"
}
```

That section is independent from the enablement entry. Users still enable the
module under `Modules`, then add any module-specific section the module's own
documentation describes. Bundled modules may place their defaults in the
checked-in `.env.template` files for discoverability, but third-party modules
do not need a SharpClaw source change just to introduce configuration keys.
Changes to Core `.env` take effect after the Core process restarts.

For example, this enables agent orchestration while keeping the VS Code editor
bridge disabled:

```jsonc
"Modules": {
  "sharpclaw_agent_orchestration": "true",
  "sharpclaw_vscode_editor": "false"
}
```

Runtime management uses the same module ids. `module list` shows discovered
modules and their load state. `module get sharpclaw_agent_orchestration` shows
one module in detail. `module enable sharpclaw_vscode_editor` enables a module
without a restart, and `module disable sharpclaw_vscode_editor` turns it off
again. Routes that were already mapped stay mapped, but disabled module routes
should return an unavailable response rather than executing module behavior.

The base template keeps feature and editor modules off and enables provider
modules. That gives a clean install provider support without exposing extra
automation or editor surfaces by accident. The development template turns every
bundled module on so contributors exercise the complete bundled surface during
local work.

## Current Bundled Modules

The current bundled module set contains agent orchestration, editor common,
metrics, module development, five provider modules, and two editor modules.
Those modules are package-owned. SharpClaw keeps only the TestHarness module
source in this repository for explicit test infrastructure. Older module
surfaces that are not present in the package set are not part of the bundled
product unless an external module supplies them.

`sharpclaw_agent_orchestration` is the Agent Orchestration module. It owns
agent lifecycle and orchestration tools such as sub-agent creation, agent
management, task editing, and skill access. It is disabled in the base template
and enabled in the development template.

`sharpclaw_editor_common` is the shared editor infrastructure module. It
exports the `editor_bridge` and `editor_session` contracts used by editor
integrations. It is disabled in the base template and enabled in development.
Enable it before enabling an editor module when you need editor bridge support.

`sharpclaw_metrics` owns the `MetricThreshold` task trigger and built-in metric
providers. It is disabled in the base template and enabled in development. If a
task trigger depends on metric thresholds but never fires, this is the first
module to check.

`sharpclaw_module_dev` is the Module Development Kit. It provides module
authoring, building, hot-loading, and introspection tools. It has an optional
`window_management` dependency, so it can still load when that contract is not
available, but features backed by that contract will be unavailable.

`sharpclaw_providers_anthropic` registers Anthropic provider support.
`sharpclaw_providers_google` registers Google native provider support.
`sharpclaw_providers_llamasharp` registers local GGUF inference through
LLamaSharp and owns local model file state, local model download and load
lifecycle, `/models/local` endpoints, and the `localmodel` CLI verb.
`sharpclaw_providers_ollama` registers Ollama provider support.
`sharpclaw_providers_openai_compat` registers OpenAI-protocol providers,
including OpenAI, DeepSeek, OpenRouter, ZAI, Vercel AI Gateway, xAI, Groq,
Cerebras, Mistral, GitHub Copilot, Minimax, Eden AI, Custom, Google Gemini
through the OpenAI shim, and Google Vertex AI through the OpenAI shim. These
provider modules are enabled in both the base template and the development
template.

`sharpclaw_vs2026_editor` adds the Visual Studio 2026 editor integration via
the editor bridge. It is a Windows-focused editor module and is disabled in the
base template. `sharpclaw_vscode_editor` adds the VS Code editor integration
for code editing, navigation, and workspace management, and it is also disabled
in the base template.

## Base Template Modules

This is the current `Modules` section from the Core `.env.template`. The
operational settings at the top of the section control module host behavior.
The module ids after them are the bundled modules that exist in the current
source tree.

```jsonc
"Modules": {
  "CrashOnExternalModuleLoadFailure": "true",
  "EventDispatchTimeoutSeconds": "5",
  "HealthCheckIntervalSeconds": "60",
  "HealthCheckFailureThreshold": "3",
  "HealthCheckTimeoutSeconds": "10",
  "MaxEnvelopeSizeBytes": "1048576",
  "UnloadVerifyMaxAttempts": "10",
  "UnloadVerifyDelayMs": "100",
  "sharpclaw_agent_orchestration": "false",
  "sharpclaw_editor_common": "false",
  "sharpclaw_metrics": "false",
  "sharpclaw_module_dev": "false",
  "sharpclaw_providers_anthropic": "true",
  "sharpclaw_providers_google": "true",
  "sharpclaw_providers_llamasharp": "true",
  "sharpclaw_providers_ollama": "true",
  "sharpclaw_providers_openai_compat": "true",
  "sharpclaw_vs2026_editor": "false",
  "sharpclaw_vscode_editor": "false"
}
```

The development template uses the same operational settings and sets every
bundled module id to `"true"`. If local development behaves differently from a
base install, compare `.env` and `.dev.env` first; the later development file
usually explains the difference.

## External Modules

External modules are configured separately from bundled modules. Add an
absolute path to a directory that contains `module.json` under
`ExternalModules`. The `Enabled` value defaults to true when it is omitted. By
default, startup fails if an enabled external module path cannot be loaded;
set `Modules:CrashOnExternalModuleLoadFailure` to `"false"` only when you want
startup to continue while investigating a broken local module path.

```jsonc
"ExternalModules": [
  {
    "Path": "C:\\modules\\Custom.Module\\bin\\Debug\\net10.0",
    "Enabled": true
  }
]
```

When an external module is loaded through the runtime loader, SharpClaw can add
the path back into the Core env file so it persists across restarts. Keep those
paths absolute and keep disabled entries in place when you want a module to be
documented but not loaded on the current machine.

## Troubleshooting

If a module does not load, first check the exact module id in the Core env file
and make sure the value is the string `"true"`. A typo such as
`sharpclaw_vs_code_editor` will not match `sharpclaw_vscode_editor`, so the
module remains disabled even though the env file looks close at a glance. Next
check the platform. `sharpclaw_vs2026_editor` is only useful on Windows, while
the provider modules are intended to run on their supported desktop/server
platforms.

If a dependent feature is missing, check exported contracts. For example,
editor integrations depend on the shared editor bridge behavior from
`sharpclaw_editor_common`. If the common editor module is disabled or fails to
initialize, the editor-specific module may be present but unable to provide a
working bridge.

If task triggers fail to fire, match the trigger type to the owning module.
Metric threshold triggers require `sharpclaw_metrics`. Triggers or tools from
non-bundled modules are available only when an external module supplies them.
