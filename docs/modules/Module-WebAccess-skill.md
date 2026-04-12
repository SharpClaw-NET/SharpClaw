SharpClaw Module: Web Access — Agent Skill Reference

Module ID: sharpclaw_web_access
Display Name: Web Access
Tool Prefix: wa
Version: 1.0.0
Platforms: All
Exports: none
Requires: none

────────────────────────────────────────
ENABLING
────────────────────────────────────────
.env key: Modules:sharpclaw_web_access
Default: disabled
Prerequisites: none
Platform: All

To enable, add to your core .env (Infrastructure/Environment/.env) Modules section:
  "sharpclaw_web_access": "true"

To disable, set to "false" or remove the key (missing = disabled).

Runtime toggle (no restart required):
  module disable sharpclaw_web_access
  module enable sharpclaw_web_access

See docs/Module-Enablement-Guide.md for full details.

────────────────────────────────────────
OVERVIEW
────────────────────────────────────────
Localhost access (headless browser + direct HTTP), external website access
with SSRF protection, and multi-provider search engine queries. All
platforms — standard .NET HTTP and System.Diagnostics.Process.

Tools are dispatched via the module system (AgentActionType = ModuleAction).
Tool names are prefixed with "wa_" when sent to the model.

────────────────────────────────────────
ENUMS
────────────────────────────────────────
SearchEngineType: Google (0), Bing (1), DuckDuckGo (2), Brave (3),
  SearXNG (4), Tavily (5), Serper (6), Kagi (7), YouDotCom (8),
  Mojeek (9), Yandex (10), Baidu (11), Custom (99).

────────────────────────────────────────
TOOLS (4)
────────────────────────────────────────

wa_access_localhost_browser (alias: access_localhost_in_browser)
  Headless GET localhost. html=DOM (default), screenshot=PNG (vision).
  localhost/127.0.0.1 only.
  Params: url (string, required), mode (string, optional — "html"|"screenshot")
  Permission: global (AccessLocalhostInBrowser)

wa_access_localhost_cli
  HTTP GET localhost. Returns status+headers+body.
  localhost/127.0.0.1 only.
  Params: url (string, required)
  Permission: global (AccessLocalhostCli)

wa_access_website
  Fetch a registered external website. cli=HTTP GET (default),
  html=headless DOM, screenshot=PNG. Downloads blocked; binary rejected;
  redirects pinned to registered origin.
  Params: targetId (website GUID, required),
          path (string, optional — appended to base URL),
          mode (string, optional — "cli"|"html"|"screenshot")
  Permission: per-resource (Website)

wa_query_search_engine
  Query a registered search engine. Params vary by type.
  Common: targetId (search engine GUID, required),
          query (string, required), count (int, optional),
          offset (int, optional), language/region/safeSearch (optional).
  Google: dateRestrict, siteRestrict, fileType, exactTerms, excludeTerms,
          searchType, sortBy.
  Bing: siteRestrict.
  SearXNG: category.
  Tavily: topic, searchType (basic|advanced).
  Permission: per-resource (SearchEngine)

────────────────────────────────────────
CLI
────────────────────────────────────────
resource website list/get/add/update/delete    (aliases: site, ws)
resource searchengine list/get/add/update/delete  (aliases: search, se)

────────────────────────────────────────
RESOURCE DEPENDENCIES
────────────────────────────────────────
- Websites — for access_website
- SearchEngines — for query_search_engine

────────────────────────────────────────
ROLE PERMISSIONS (relevant)
────────────────────────────────────────
Global flags: canAccessLocalhostInBrowser, canAccessLocalhostCli
Per-resource: websiteAccesses (Websites),
  searchEngineAccesses (SearchEngines)
