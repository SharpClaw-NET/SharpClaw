# SharpClaw Module: Web Access

> **Module ID:** `sharpclaw_web_access`
> **Display Name:** Web Access
> **Version:** 1.0.0
> **Tool Prefix:** `wa`
> **Platforms:** All
> **Exports:** none
> **Requires:** none

---

## How to Enable

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_web_access` |
| **Default** | ❌ Disabled |
| **Prerequisites** | None |
| **Platform** | All |

To enable, add to your core `.env` (`Infrastructure/Environment/.env`) Modules section:

```jsonc
"sharpclaw_web_access": "true"
```

To disable, set to `"false"` or remove the key (missing = disabled).

**Runtime toggle** (no restart required):

```
module disable sharpclaw_web_access
module enable sharpclaw_web_access
```

See [Module Enablement Guide](Module-Enablement-Guide.md) for full details.

---

## Overview

The Web Access module provides localhost access (headless browser and
direct HTTP), external website access with SSRF protection, and
multi-provider search engine queries. All platforms — uses standard
.NET HTTP and `System.Diagnostics.Process`.

Tools are registered in the `ModuleRegistry` by their canonical names
(and optional aliases). When the model invokes a tool,
`ModuleRegistry.TryResolve` maps the tool name to the owning module
and canonical tool name. Job-pipeline tools are submitted through
`AgentJobService`; inline tools execute directly in the chat loop.
Tool names are **not** dynamically prefixed — modules define the final
names that the model sees (e.g. `access_website`,
`access_localhost_browser`).

---

## Table of Contents

- [Enums](#enums)
- [Tools](#tools)
  - [access_localhost_browser](#access_localhost_browser)
  - [access_localhost_cli](#access_localhost_cli)
  - [access_website](#access_website)
  - [query_search_engine](#query_search_engine)
- [CLI Commands](#cli-commands)
- [Resource Dependencies](#resource-dependencies)
- [Role Permissions](#role-permissions)
- [Module Manifest](#module-manifest)

---

## Enums

### SearchEngineType

| Value | Int | Description |
|-------|-----|-------------|
| `Google` | 0 | Google Custom Search JSON API |
| `Bing` | 1 | Microsoft Bing Web Search API |
| `DuckDuckGo` | 2 | DuckDuckGo Instant Answer API (no key required) |
| `Brave` | 3 | Brave Search API |
| `SearXNG` | 4 | SearXNG federated meta-search (self-hosted) |
| `Tavily` | 5 | Tavily AI-optimised search API |
| `Serper` | 6 | Serper.dev Google SERP scraping API |
| `Kagi` | 7 | Kagi search API |
| `YouDotCom` | 8 | You.com Search API |
| `Mojeek` | 9 | Mojeek independent search API |
| `Yandex` | 10 | Yandex Search API |
| `Baidu` | 11 | Baidu Search API |
| `Custom` | 99 | Generic custom search engine |

---

## Tools

### access_localhost_browser

Access localhost via a headless Chromium browser. Returns DOM HTML
(default) or a PNG screenshot (for vision-capable models).
Restricted to `localhost` / `127.0.0.1` only.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `url` | string | yes | Localhost URL |
| `mode` | string | no | `"html"` (default) or `"screenshot"` |

**Permission:** Global — requires `canAccessLocalhostInBrowser` flag.

**Aliases:** `access_localhost_in_browser`

---

### access_localhost_cli

HTTP GET against localhost; returns status code, headers, and body.
Restricted to `localhost` / `127.0.0.1` only.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `url` | string | yes | Localhost URL |

**Permission:** Global — requires `canAccessLocalhostCli` flag.

---

### access_website

Fetch a registered external website. Supports three modes: `cli`
(direct HTTP GET, default), `html` (headless browser DOM), `screenshot`
(headless browser PNG). Downloads are blocked; binary content types are
rejected; redirects are pinned to the registered origin.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | Website resource GUID |
| `path` | string | no | Path appended to the registered base URL |
| `mode` | string | no | `"cli"` (default), `"html"`, or `"screenshot"` |

**Permission:** Per-resource — requires `websiteAccesses` grant.

---

### query_search_engine

Query a registered search engine. Parameters vary by engine type.

**Common parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | Search engine resource GUID |
| `query` | string | yes | Search query |
| `count` | integer | no | Number of results |
| `offset` | integer | no | Result offset |
| `language` | string | no | Language code |
| `region` | string | no | Region code |
| `safeSearch` | string | no | Safe search level |

**Engine-specific parameters:**

| Engine | Additional parameters |
|--------|----------------------|
| Google | `dateRestrict`, `siteRestrict`, `fileType`, `exactTerms`, `excludeTerms`, `searchType`, `sortBy` |
| Bing | `siteRestrict` |
| SearXNG | `category` |
| Tavily | `topic`, `searchType` (`basic` / `advanced`) |

**Permission:** Per-resource — requires `searchEngineAccesses` grant.

---

## CLI Commands

The module registers two resource commands:

```
resource website list                          List all websites
resource website get <id>                      Show a website
resource website add <name> <url> [desc]       Add a website
resource website update <id> [name=X] [url=X]  Update a website
resource website delete <id>                   Delete a website
```

Aliases: `site`, `ws`

```
resource searchengine list                              List all search engines
resource searchengine get <id>                          Show a search engine
resource searchengine add <name> <type> <endpoint>      Add a search engine
resource searchengine update <id> [name=X] [type=X]     Update a search engine
resource searchengine delete <id>                       Delete a search engine
```

Aliases: `search`, `se`

---

## Resource Dependencies

| Resource Type | Used by |
|---------------|---------|
| Websites | `access_website` |
| Search Engines | `query_search_engine` |

---

## Role Permissions

### Global flags

| Flag | Tools |
|------|-------|
| `canAccessLocalhostInBrowser` | `access_localhost_browser` |
| `canAccessLocalhostCli` | `access_localhost_cli` |

### Per-resource arrays

| Array | Resource Type | Tools |
|-------|---------------|-------|
| `websiteAccesses` | Websites | `access_website` |
| `searchEngineAccesses` | SearchEngines | `query_search_engine` |

---

## Module Manifest

```json
{
  "id": "sharpclaw_web_access",
  "displayName": "Web Access",
  "version": "1.0.0",
  "toolPrefix": "wa",
  "entryAssembly": "SharpClaw.Modules.WebAccess",
  "minHostVersion": "1.0.0",
  "platforms": null,
  "executionTimeoutSeconds": 60,
  "exports": [],
  "requires": []
}
```
