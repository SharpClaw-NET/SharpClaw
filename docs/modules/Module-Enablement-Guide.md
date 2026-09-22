# Module Enablement Guide

SharpClaw modules are runtime feature packages discovered by the Runtime at
startup. A module can add tools, REST endpoints, CLI commands, resource types,
provider implementations, persistence providers, or editor integrations. The
bundled modules are restored from NuGet package payloads. `ExternalRegistrations`
adds roots to the same package-manifest loader; it is not a second module system.

The deployed Runtime Host assembly's `Environment/.env` uses
canonical dotenv. In development mode, `.dev.env` is loaded after `.env`, so
the development file can turn on modules without changing the base template.
File keys use `__`, while `IConfiguration` uses `:`. A package manifest supplies
the default enabled state. `Packages__<registration_id>` may explicitly override
that state with `"true"` or `"false"`.

Module-owned configuration uses the same package-backed env loader. If an
enabled module reads `IConfiguration["MyModule:EndpointUrl"]`, users can add
`MyModule__EndpointUrl` to the Runtime `.env` without any change to
`LocalEnvironment`, `ModuleLoader`, or host startup code. The module owns the
section name, default values, validation, and documentation. The host only
loads the dotenv document and makes it available through DI.

For example, a third-party module can document this shape:

```dotenv
MyModule__EndpointUrl="https://example.internal/api"
MyModule__RetrySeconds="15"
```

That section is independent from the enablement entry. Users override the
module under `Packages` when necessary, then add any module-specific section
the module's own documentation describes. Bundled modules may place their defaults in the
checked-in `.env.template` files for discoverability, but third-party modules
do not need a SharpClaw source change just to introduce configuration keys.
Changes to Runtime `.env` take effect after the Runtime process restarts.

For example, this enables one package while keeping the VS Code editor bridge
disabled:

```dotenv
Packages__example_package="true"
Packages__sharpclaw_vscode_editor="false"
```

Package enablement is compiled into the Runtime graph during startup. Changing a
package override or external registration root takes effect after a Runtime
restart; the current production host does not claim live graph replacement.

Bundled manifests currently provide the enablement defaults, and both Runtime
templates leave those defaults intact. Add explicit package overrides when an
installation needs a smaller surface; the development template changes logging
and other development settings without creating a second enablement model.

## Current Bundled Modules

The current bundled module set contains editor common, metrics, module
development, five model-provider modules, four persistence-provider modules,
and two editor modules.
Those modules are package-owned. SharpClaw keeps only the TestHarness module
source in this repository for explicit test infrastructure. Older module
surfaces that are not present in the package set are not part of the bundled
product unless an external module supplies them.

`sharpclaw_editor_common` is the shared editor infrastructure module. It
exports the `editor_bridge` and `editor_session` contracts used by editor
integrations. Disable it only when no enabled editor integration requires its
contracts.

`sharpclaw_metrics` owns built-in metric
providers. If a
metric threshold depends on metric thresholds but never fires, this is the first
module to check.

`sharpclaw_module_dev` is the Module Development Kit. It provides module
authoring, building, packaging, and introspection tools. It has an optional
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
through the OpenAI shim, and Google Vertex AI through the OpenAI shim. Their
manifests currently default to enabled.

`sharpclaw_persistence_jsoncoldstore`, `sharpclaw_persistence_postgresql`,
`sharpclaw_persistence_sqlserver`, and `sharpclaw_persistence_sqlite` contribute
the official storage provider keys. Installations select one with
`Database__Provider`; third-party persistence packages use the same module and
provider contracts.

`sharpclaw_vs2026_editor` adds the Visual Studio 2026 editor integration via
the editor bridge and is Windows-focused. `sharpclaw_vscode_editor` adds the VS
Code editor integration for code editing, navigation, and workspace management.
Use package overrides to disable either integration on installations that do
not expose that editor surface.

## Package Overrides

The base template declares only the package-envelope limit. Package enablement
normally comes from each manifest; the remaining lines below are examples of
explicit overrides an installation may add.

```dotenv
Packages__MaxEnvelopeSizeBytes="1048576"
Packages__sharpclaw_editor_common="false"
Packages__sharpclaw_metrics="false"
Packages__sharpclaw_module_dev="false"
Packages__sharpclaw_providers_anthropic="true"
Packages__sharpclaw_providers_google="true"
Packages__sharpclaw_providers_llamasharp="true"
Packages__sharpclaw_providers_ollama="true"
Packages__sharpclaw_providers_openai_compat="true"
Packages__sharpclaw_vs2026_editor="false"
Packages__sharpclaw_vscode_editor="false"
```

If local development behaves differently from a base install, compare `.env`
and `.dev.env` first; the later development file overrides the base document.

## External Modules

Add an absolute directory containing one or more `package.json` contribution
manifests under `ExternalRegistrations`. `Enabled` defaults to true. Enabled
roots and bundled contributions enter the same loader, graph compiler, identity
checks, dependency rules, and startup failure boundary. Missing paths and broken
packages fail closed.

```dotenv
ExternalRegistrations__0__Path="C:/modules/Custom.Module/bin/Debug/net10.0"
ExternalRegistrations__0__Enabled="true"
```

Keep paths absolute and keep disabled entries in place when a package should
remain documented but must not load on the current machine.

## Troubleshooting

If a module does not load, first check the exact package id and its manifest
default, then check any `Packages__<id>` override in the Runtime env file. A typo such as
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

Metric threshold triggers require `sharpclaw_metrics`. Triggers or tools from
non-bundled modules are available only when an external module supplies them.
