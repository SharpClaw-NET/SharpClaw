# Default frontend and module settings

The base frontend has three deliberately minimal black/green pages: Boot, Settings, and stateless debug chat. Boot remains the home page even when Runtime is ready; it offers Install modules, Settings, and Open stateless chat. With no enabled modules the bundled Runtime is not started, and Boot and offline Settings remain usable. Stateless chat is only a diagnostic surface: it sends each message independently, does not supply conversation history, and refuses to send unless the selected provider is registered, its setup is complete, and its model appears in that provider's model catalog. Catalog discovery is metadata, not an inference request; discovery failure or timeout leaves chat disabled with a link back to Settings.

## Installing modules

Boot accepts an absolute local contribution directory, a local `.nupkg` or `.zip`, a NuGet gallery/version link, a GitHub package page, or a GitHub release/asset link. A compatible payload contains one or more `package.json` registrations and their managed entry assemblies and dependencies, not merely a library DLL; use the existing ModuleSDK packaging conventions. Gallery and release links expose explicit version/asset choices. GitHub Packages requires a separately entered `read:packages` token, held only for the current inspection; Runtime credentials are never reused, and authorization is stripped from redirects. Only HTTPS NuGet/GitHub authorities are accepted for remote sources. Arbitrary feeds, generic web pages, and automatic dependency restoration are not supported.

Inspection copies the payload into owned writable scratch, validates bounded archive paths/manifests and selected NuGet identity, and records file hashes without loading assemblies or starting a sidecar. It preserves the entire package, including licence and source-offer files. Install and enable module code is a separate explicit confirmation: only install code you trust, because neither archive validation nor NuGet/GitHub hosting certifies its safety. The confirmed bytes must still match inspection. Existing bundled or installed module identities are never silently replaced. Leaving Boot cancels inspection and retires its scratch. Failed activation is not proof of a safe or compatible module.

Installation targets only this frontend's owned bundled Runtime, never an externally selected Runtime. It stops owned Runtime/Gateway processes, commits a new installation under the frontend's writable `Data/modules/registrations` root, and updates protected Runtime configuration with `ExternalRegistrations:frontend-modules` and per-package enablement. Bundled and installed modules then enter the same production root resolver, loader, dependency checks, compiled graph, and in-process/sidecar hosting paths. Settings can enable or disable both bundled and installed registrations; changes rebuild the Runtime graph rather than hot-loading code or mutating live singletons. A configured persistence module is still required for Runtime readiness. If a module prevents readiness, return to offline Settings and disable it.

## Module-owned Settings pages

Runtime and module enablement are the base settings. Any compatible module can add pages without a frontend CLR plugin, built-in module-name switch, or second loader by declaring this optional versioned extension in its existing `package.json`:

```json
{
  "frontend": {
    "schemaVersion": 1,
    "settings": [
      {
        "id": "preferences",
        "title": "Preferences",
        "readPath": "/example/settings",
        "savePath": "/example/settings"
      }
    ]
  }
}
```

The same module must register both literal GET and POST endpoints through the public ModuleSDK HTTP contracts. Runtime rejects declarations pointing at host routes, another module's routes, templated paths, or WebSocket endpoints. Only enabled, successfully loaded modules appear in the authenticated `/setup/modules` catalog. In-process and sidecar modules use the same catalog and compiled HTTP transport; the client makes requests through the existing client action and authority boundaries. Modules own authorization, persistence, validation, and the effect of a successful save. Disabling a module removes its settings pages from the new graph.

The GET endpoint returns a version-1 document such as the following; text, boolean, choice, and write-only secret fields are rendered using the existing minimal controls:

```json
{
  "schemaVersion": 1,
  "fields": [
    { "key": "label", "label": "Label", "kind": "text", "required": true },
    { "key": "enabled", "label": "Enabled", "kind": "boolean" },
    { "key": "mode", "label": "Mode", "kind": "choice", "choices": ["local", "remote"] },
    { "key": "token", "label": "Access token", "kind": "secret" }
  ],
  "values": { "label": "Example", "enabled": true, "mode": "local" }
}
```

POST receives `{ "values": { ... } }` containing the edited declared fields. Blank secret controls are omitted so existing credentials are retained; returned secrets are never displayed. The module should never return secret values and must enforce its own required-credential policy. The client bounds documents to 32 KiB, depth 8, and 32 fields; a module may declare at most 16 pages, and the catalog is limited to 128 pages. No executable markup, remote web view, scripts, or third-party UI assembly is loaded by this settings contract. Licences, written source offers, and the privacy text remain accessible inside Settings/About, not separate default pages.
