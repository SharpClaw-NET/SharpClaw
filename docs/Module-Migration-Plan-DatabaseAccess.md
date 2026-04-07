# Module Migration Plan: Database Access → Database Access Module

> **Module ID:** `sharpclaw.database-access`  
> **Display Name:** Database Access  
> **Tool Prefix:** `db`  
> **Scope:** Internal database access, external database access, database registration  
> **Status:** Assessment / Pre-implementation  
> **Date:** 2025-07

---

## 1. Executive Summary

This plan covers migrating the database access subsystem into a first-class
SharpClaw module. Today, database access is the **least implemented** of all
core action types — the entity models and permission plumbing exist, but there
are **no execution handlers** and **no dedicated services**. The three enum
values (`RegisterDatabase`, `AccessInternalDatabases`, `AccessExternalDatabase`)
fall through to the default case in `DispatchExecutionAsync`, which returns a
generic success message via `TryDispatchByActionKeyAsync` or a static string.

### Key Characteristics

| Aspect | Database Access |
|--------|----------------|
| Code location | Entity models in Infrastructure; permission wiring in AgentActionService/AgentJobService; tool definitions in ChatService. **No execution logic anywhere.** |
| Dedicated services | None — no `InternalDatabaseService`, `ExternalDatabaseService`, or similar |
| REST endpoints | None — no CRUD handlers in `ResourceHandlers.cs` for databases |
| CLI commands | None — no `resource internaldb` or `resource externaldb` in `CliDispatcher` |
| Execution handlers | None — `DispatchExecutionAsync` has no cases for any DB action |
| ChatService tool defs | 3 tools: `register_database` (stub), `access_internal_databases` (stub), `access_external_database` (schema-complete) |
| Entity models | `InternalDatabaseDB`, `ExternalDatabaseDB`, `InternalDatabaseAccessDB`, `ExternalDatabaseAccessDB` — all present and EF-configured |
| Lines to extract | ~0 execution lines; ~30 lines permission wiring; ~50 lines helper method entries |

This makes the database access migration **the simplest possible extraction** —
it is essentially greenfield module development that also cleans up stale core
plumbing. The module will be the first to actually **implement** these tools.

### Decision: Module vs. Core

Database access is inherently an optional, plugin-style capability:

- Not all deployments need database connectivity (some are chat-only or
  desktop-automation-focused).
- Different database engines require different drivers/NuGet packages
  (Npgsql, MySqlConnector, MongoDB.Driver, StackExchange.Redis, etc.) —
  keeping these in Core would bloat the dependency tree for everyone.
- The module architecture naturally supports adding new database types via
  additional modules or extending the existing one.

---

## 2. Readiness Assessment

### 2.1 Infrastructure Status — ✅ Ready

| Component | Status | Notes |
|---|---|---|
| `ISharpClawModule` contract | ✅ Complete | Full interface with lifecycle hooks |
| `ModuleRegistry` | ✅ Complete | Alias support, `TryResolve`, topological sort |
| Automatic enum → module dispatch | ✅ Complete | `PascalToSnakeCase` derivation pipeline |
| `AgentJobService.DispatchModuleExecutionAsync` | ✅ Complete | Timeout, restricted scope |
| `ChatService` module fallback | ✅ Complete | `TryResolve` → module tool schema |
| Entity models + EF config | ✅ Complete | `InternalDatabaseDB`, `ExternalDatabaseDB`, access tables, `SharpClawDbContext` config |
| Permission wiring | ✅ Complete | `AgentActionService` has `RegisterDatabaseAsync`, `AccessInternalDatabaseAsync`, `AccessExternalDatabaseAsync` |
| `PermissionSetDB` fields | ✅ Complete | `CanRegisterDatabases`, `InternalDatabaseAccesses`, `ExternalDatabaseAccesses`, defaults |
| `DefaultResourceSetDB` fields | ✅ Complete | `InternalDatabaseResourceId`, `ExternalDatabaseResourceId` |
| Build | ✅ Passing | No compilation errors |

### 2.2 Missing Pieces — Work Required

| Item | Severity | Description |
|---|---|---|
| **Module project scaffolding** | Required | Create `DefaultModules/DatabaseAccess/` with `.csproj`, `module.json`, module class |
| **Execution logic** | Required | Implement actual database query execution — this is 100% new code |
| **Connection string management** | Required | Decrypt `ExternalDatabaseDB.EncryptedConnectionString`, build ADO.NET/driver connections |
| **Driver packages** | Required | NuGet references for target database engines (Npgsql, MySqlConnector, etc.) |
| **Resource CRUD service** | Required | Create service for managing `InternalDatabaseDB` / `ExternalDatabaseDB` entities |
| **REST handlers** | Required | Add CRUD endpoints under `/resources/internaldatabases` and `/resources/externaldatabases` |
| **CLI commands** | Required | Add `resource internaldb` and `resource externaldb` to `CliDispatcher` |
| **Core cleanup** | Required | Mark enum values `[Obsolete]`, remove stale ChatService tool defs, remove core helper method entries |

---

## 3. Architecture Decisions

### 3.1 What Stays in Core

The following remain in core because they are part of the permission and entity
layer shared by all modules and the core permission evaluation pipeline:

| Component | Reason |
|---|---|
| `InternalDatabaseDB` / `ExternalDatabaseDB` | EF entity models — DbContext owns all entity config |
| `InternalDatabaseAccessDB` / `ExternalDatabaseAccessDB` | Permission grant entities used by `PermissionSetDB` |
| `DatabaseType` enum | Shared contract enum; modules reference it |
| `DatabaseAccessLevel` enum | Shared contract enum for access control |
| `PermissionSetDB` fields (`CanRegisterDatabases`, `InternalDatabaseAccesses`, etc.) | Core permission model — accessed by `AgentActionService` |
| `DefaultResourceSetDB` fields (`InternalDatabaseResourceId`, `ExternalDatabaseResourceId`) | Default resource resolution is a core job service concern |
| `AgentActionService.RegisterDatabaseAsync` / `AccessInternalDatabaseAsync` / `AccessExternalDatabaseAsync` | Permission check methods — used by `AgentJobService.DispatchPermissionCheckAsync` |
| `ResourceHandlers.LookupByAccessType` (`internalDatabaseAccesses` / `externalDatabaseAccesses` cases) | Universal resource lookup used by the permission editor UI |
| `DefaultResourceSetService.ApplyKey` (`internaldb` / `externaldb` cases) | Default resource key setter used by CLI and API |

### 3.2 What Moves to the Module

| Component | Destination | Notes |
|---|---|---|
| Tool definitions (3 tools) | `module.json` + `ModuleToolDefinition` list | `register_database`, `access_internal_databases`, `access_external_database` |
| `BuildAccessExternalDatabaseSchema` | Module `GetTools()` schema | Currently in `ChatService.cs` |
| Execution logic (NEW) | `DatabaseAccessModule.ExecuteToolAsync` | Does not exist today — to be implemented |
| Resource CRUD service (NEW) | Core `DatabaseResourceService` (or module-provided handlers) | CRUD for entity management |
| Database driver orchestration (NEW) | Module internal | Connection factory, query execution, result formatting |

### 3.3 Automatic Dispatch — Already Works

The automatic dispatch architecture (§3.5 of Module-Migration-Plan.md) already
handles this migration transparently:

1. `PascalToSnakeCase("RegisterDatabase")` → `register_database`
2. `PascalToSnakeCase("AccessInternalDatabases")` → `access_internal_databases`
3. `PascalToSnakeCase("AccessExternalDatabase")` → `access_external_database`

When the module registers tools with these names (or aliases), the existing
`TryDispatchByActionKeyAsync` will route jobs to the module. No changes to
`DispatchExecutionAsync` are needed — these actions already fall to the
default case which calls `TryDispatchByActionKeyAsync`.

### 3.4 Resource CRUD Strategy

Unlike other migrated modules (mk8.shell, CU, OA) which had existing service
classes to extract, database resources have **no CRUD service at all**. Two
options:

**Option A: Core Service (Recommended)**

Create `DatabaseResourceService` in Core, following the pattern of
`ContainerService`, `AudioDeviceService`, etc. Add REST handlers in
`ResourceHandlers.cs` and CLI commands in `CliDispatcher.cs`. This keeps
resource management consistent with all other resource types.

**Option B: Module-Provided Handlers**

Register resource type commands via `ModuleRegistry.TryResolveResourceTypeCommand`.
This is more modular but breaks the pattern established by all other resource
types which have core CRUD.

**Recommendation:** Option A. The entity models are in Infrastructure,
the DbContext owns the tables, and the permission lookup UI already queries
them. A core CRUD service is the natural fit.

---

## 4. Inventory

### 4.1 Enum Values

| Enum | Value | Type | Current State |
|---|---|---|---|
| `RegisterDatabase` | 2 | Global flag | Active, no execution handler (falls to default) |
| `AccessInternalDatabases` | 9 | Per-resource | Active, no execution handler (falls to default) |
| `AccessExternalDatabase` | 10 | Per-resource | Active, no execution handler (falls to default) |

### 4.2 ChatService Tool Definitions (to be removed from core)

| Tool Name | Description | Schema | Line |
|---|---|---|---|
| `register_database` | "Register a new database resource. [Stub.]" | `globalSchema` | 2099 |
| `access_internal_databases` | "Query an internal (SharpClaw-managed) database. [Stub.]" | `resourceOnly` | 2110 |
| `access_external_database` | Full description with query language guidance | `accessExternalDatabaseSchema` | 2111-2116 |

### 4.3 ChatService Schema Builders (to be removed from core)

| Method | Lines | Notes |
|---|---|---|
| `BuildAccessExternalDatabaseSchema()` | ~24 lines | Properties: `targetId`, `query`, `timeout` |

### 4.4 AgentActionService Permission Methods (STAYS in core)

| Method | Type | Lines |
|---|---|---|
| `RegisterDatabaseAsync` | Global flag | 44-49 |
| `AccessInternalDatabaseAsync` | Per-resource | 162-168 |
| `AccessExternalDatabaseAsync` | Per-resource | 170-176 |

### 4.5 AgentJobService Entries (to be cleaned after module works)

| Location | Method/Section | Entry | Notes |
|---|---|---|---|
| `DispatchPermissionCheckAsync` | switch | `RegisterDatabase`, `AccessInternalDatabases`, `AccessExternalDatabase` | Stays — routes to AgentActionService |
| `IsPerResourceAction` | core list | `AccessInternalDatabases`, `AccessExternalDatabase` | Stays while core permission checks remain |
| `ExtractFromDefaultResourceSet` | switch | `AccessInternalDatabases`, `AccessExternalDatabase` | Stays — default resource resolution |
| `ExtractDefaultResourceId` | switch | `AccessInternalDatabases`, `AccessExternalDatabase` | Stays — default resource resolution |
| `HasMatchingGrant` | switch | `RegisterDatabase`, `AccessInternalDatabases`, `AccessExternalDatabase` | Stays — channel pre-authorization |
| `ResolveDefaultResourceIdAsync` | includes | `.Include(p => p.DefaultInternalDatabaseAccess)`, `.Include(p => p.DefaultExternalDatabaseAccess)` | Stays — used by default resolution |

**Note:** Unlike CU/OA/shell migrations, the AgentJobService entries for
database access are all **permission/resource-resolution plumbing**, not
execution code. They should remain in core because they serve the permission
system, not the tool execution.

### 4.6 Entity Models (STAY in Infrastructure)

| Entity | File | Fields |
|---|---|---|
| `InternalDatabaseDB` | `Infrastructure/Models/Resources/InternalDatabaseDB.cs` | `Name`, `DatabaseType`, `Path`, `Description`, `SkillId`, `Permissions` |
| `ExternalDatabaseDB` | `Infrastructure/Models/Resources/ExternalDatabaseDB.cs` | `Name`, `DatabaseType`, `EncryptedConnectionString`, `Description`, `SkillId`, `Permissions` |
| `InternalDatabaseAccessDB` | `Infrastructure/Models/Access/InternalDatabaseAccessDB.cs` | `AccessLevel`, `Clearance`, `PermissionSetId`, `InternalDatabaseId` |
| `ExternalDatabaseAccessDB` | `Infrastructure/Models/Access/ExternalDatabaseAccessDB.cs` | `AccessLevel`, `Clearance`, `PermissionSetId`, `ExternalDatabaseId` |

### 4.7 PermissionSetDB Fields (STAY in Infrastructure)

- `CanRegisterDatabases` / `RegisterDatabasesClearance` (global flag)
- `InternalDatabaseAccesses` (per-resource collection)
- `ExternalDatabaseAccesses` (per-resource collection)
- `DefaultInternalDatabaseAccessId` / `DefaultInternalDatabaseAccess`
- `DefaultExternalDatabaseAccessId` / `DefaultExternalDatabaseAccess`

### 4.8 DefaultResourceSetDB Fields (STAY in Infrastructure)

- `InternalDatabaseResourceId`
- `ExternalDatabaseResourceId`

### 4.9 DbContext Configuration (STAYS)

- `SharpClawDbContext`: `InternalDatabases`, `ExternalDatabases`, `InternalDatabaseAccesses`, `ExternalDatabaseAccesses` DbSets
- `OnModelCreating`: Entity config for both DB types and their access tables (lines 490-529)

---

## 5. Dependency Contract

The Database Access module has **no inter-module dependencies**. It is a
leaf module with no contracts to export or require.

| Direction | Contract | Notes |
|---|---|---|
| Requires from Core | `SharpClawDbContext` (via `IServiceProvider`) | To read `ExternalDatabaseDB` entities for connection info |
| Requires from Core | `ApiKeyEncryptor` (via `IServiceProvider`) | To decrypt `EncryptedConnectionString` |
| Exports | None | No other module depends on database access |

**Service Access Pattern:** The module will resolve `SharpClawDbContext` and
`ApiKeyEncryptor` from the `ModuleServiceScope`'s `IServiceProvider`. Both
are allowed through the restricted scope (they are not in the blocked list).

---

## 6. Migration Phases

### Phase 0: Resource CRUD Service + REST + CLI (Pre-Module)

Create the missing resource management layer in core. This can be done
independently of the module and provides immediate value.

**Deliverables:**
1. `DatabaseResourceService` in `SharpClaw.Application.Core/Services/`
   - CRUD for `InternalDatabaseDB`: Create, GetById, List, Update, Delete
   - CRUD for `ExternalDatabaseDB`: Create, GetById, List, Update, Delete
   - Connection string encryption/decryption via `ApiKeyEncryptor`
2. REST handlers in `ResourceHandlers.cs`:
   - `POST/GET/GET{id}/PUT/DELETE /resources/internaldatabases`
   - `POST/GET/GET{id}/PUT/DELETE /resources/externaldatabases`
3. CLI commands in `CliDispatcher.cs`:
   - `resource internaldb add|get|list|update|delete`
   - `resource externaldb add|get|list|update|delete`
4. DTOs in `SharpClaw.Contracts`:
   - `CreateInternalDatabaseRequest`, `UpdateInternalDatabaseRequest`, `InternalDatabaseResponse`
   - `CreateExternalDatabaseRequest`, `UpdateExternalDatabaseRequest`, `ExternalDatabaseResponse`

**Verification:** Build passes. REST endpoints return correct data. CLI
commands manage database resources.

### Phase 1: Module Scaffolding

Create the module project structure.

**Deliverables:**
1. `DefaultModules/DatabaseAccess/DatabaseAccess.csproj`
   - References: `SharpClaw.Contracts`, database driver NuGet packages
2. `DefaultModules/DatabaseAccess/module.json`:
   ```json
   {
     "id": "sharpclaw.database-access",
     "name": "Database Access",
     "version": "1.0.0",
     "description": "Query internal and external databases.",
     "entryAssembly": "DatabaseAccess.dll",
     "tools": [
       {
         "name": "register_database",
         "description": "Register a new internal or external database resource.",
         "aliases": ["register_database"]
       },
       {
         "name": "access_internal_databases",
         "description": "Query an internal (SharpClaw-managed) database.",
         "aliases": ["access_internal_databases"]
       },
       {
         "name": "access_external_database",
         "description": "Execute a query against a registered external database.",
         "aliases": ["access_external_database"],
         "timeoutSeconds": 120
       }
     ],
     "permissions": [
       {
         "tool": "register_database",
         "delegateTo": "RegisterDatabase"
       },
       {
         "tool": "access_internal_databases",
         "delegateTo": "AccessInternalDatabases",
         "isPerResource": true
       },
       {
         "tool": "access_external_database",
         "delegateTo": "AccessExternalDatabase",
         "isPerResource": true
       }
     ]
   }
   ```
3. `DefaultModules/DatabaseAccess/DatabaseAccessModule.cs`:
   - Implements `ISharpClawModule`
   - `GetTools()` returns tool definitions with JSON schemas
   - `ExecuteToolAsync` dispatches to internal handlers

**Verification:** Module loads at startup. Tools appear in `GetEffectiveTools()`.

### Phase 2: Execution Implementation

Implement the actual database query execution logic inside the module.

**Deliverables:**

1. **`register_database` tool:**
   - Resolves `DatabaseResourceService` from service scope
   - Creates `InternalDatabaseDB` or `ExternalDatabaseDB` based on params
   - Encrypts connection string for external databases
   - Returns the created resource ID and name

2. **`access_internal_databases` tool:**
   - Resolves `SharpClawDbContext` to load `InternalDatabaseDB`
   - Reads the internal database path/connection info
   - Executes the query using appropriate driver
   - Returns formatted results (tabular for SQL, JSON for document DBs)

3. **`access_external_database` tool:**
   - Resolves `SharpClawDbContext` to load `ExternalDatabaseDB`
   - Resolves `ApiKeyEncryptor` to decrypt connection string
   - Builds connection using `DatabaseType`-specific driver
   - Executes query with timeout (from tool params or 30s default)
   - Returns formatted results with row count
   - **Safety:** Read-only mode by default; `DatabaseAccessLevel.FullAccess`
     required for write operations

4. **Database driver factory:**
   - Maps `DatabaseType` → driver
   - Initially support: PostgreSQL, MySQL, SQLite, MSSQL
   - Other types (MongoDB, Redis, CosmosDB) can be added incrementally
   - Each driver implements a common `IDatabaseQueryExecutor` interface

5. **Result formatting:**
   - SQL results → markdown table (capped at configurable row limit)
   - Document DB results → JSON
   - Redis results → plain text

**Verification:** End-to-end: register an external database, then query it
via the `access_external_database` tool. Verify results are formatted and
returned to the chat.

### Phase 3: Core Cleanup

Remove stale tool definitions from ChatService and mark enum values as obsolete.

**Deliverables:**

1. **ChatService — Remove tool definitions** (3 entries from `AllTools`):
   - `register_database` (line 2099-2101)
   - `access_internal_databases` (line 2110)
   - `access_external_database` (lines 2111-2116)

2. **ChatService — Remove schema builder**:
   - `BuildAccessExternalDatabaseSchema()` (~24 lines)

3. **AgentActionType.cs — Mark obsolete**:
   ```csharp
   [Obsolete("Dispatched to Database Access module.")]
   RegisterDatabase = 2,
   [Obsolete("Dispatched to Database Access module.")]
   AccessInternalDatabases = 9,
   [Obsolete("Dispatched to Database Access module.")]
   AccessExternalDatabase = 10,
   ```

4. **AgentJobService — No changes needed:**
   - Permission dispatch entries → stay (they call AgentActionService)
   - `IsPerResourceAction` entries → stay (core permission check)
   - `ExtractFromDefaultResourceSet` entries → stay (default resolution)
   - `ExtractDefaultResourceId` entries → stay (default resolution)
   - `HasMatchingGrant` entries → stay (channel pre-auth)
   - `ResolveDefaultResourceIdAsync` includes → stay (default resolution)
   
   All AgentJobService references are permission/resource plumbing, not
   execution code. They remain active and correct.

**Verification:** Build passes. Module tools load and execute. Old enum
values generate compiler warnings. `ChatService.AllTools` no longer contains
DB tool definitions — they come from `moduleRegistry.GetAllToolDefinitions()`.

---

## 7. Database Driver Strategy

### 7.1 Initial Support Matrix

| DatabaseType | Driver Package | Priority |
|---|---|---|
| PostgreSQL | `Npgsql` | P0 — Used by SharpClaw itself |
| MySQL / MariaDB | `MySqlConnector` | P1 |
| SQLite | `Microsoft.Data.Sqlite` | P1 |
| MSSQL | `Microsoft.Data.SqlClient` | P1 |
| MongoDB | `MongoDB.Driver` | P2 |
| Redis | `StackExchange.Redis` | P2 |
| CosmosDB | `Microsoft.Azure.Cosmos` | P3 |
| Oracle | `Oracle.ManagedDataAccess.Core` | P3 |
| CockroachDB | `Npgsql` (wire-compatible) | P2 |
| Firebird | `FirebirdSql.Data.FirebirdClient` | P3 |
| Custom | User-configured connection string | P3 |

### 7.2 Safety Guards

- **Read-only by default:** Queries are executed within a read-only
  transaction unless `DatabaseAccessLevel.FullAccess` is granted.
- **Timeout enforcement:** Per-query timeout (default 30s, max 120s).
- **Result size cap:** Truncate results exceeding 64 KB to prevent
  memory exhaustion and token overflow.
- **Connection pooling:** Reuse connections within a module execution
  scope; dispose on completion.
- **No DDL by default:** `DROP`, `ALTER`, `TRUNCATE`, `CREATE` blocked
  for `ReadOnly` access level.

---

## 8. Risks & Mitigations

| Risk | Severity | Mitigation |
|---|---|---|
| Driver bloat — many NuGet packages | Medium | Lazy-load drivers; only reference packages for supported types. Consider a plugin-per-driver sub-module architecture later. |
| Connection string leaks | High | Always decrypt in-memory only; never log connection strings; use `ExceptionSanitizer` for all error messages. |
| SQL injection | High | Parameterized queries where possible. For raw-query tools, rely on `DatabaseAccessLevel` gating + audit logging. Document risk to users. |
| Long-running queries | Medium | Enforce `CancellationToken` + timeout. Kill connection on timeout. |
| Internal database "path" field is vague | Low | Define clear semantics: SQLite = file path, others = connection string. Document in `InternalDatabaseDB` entity comments. |
| No existing tests | Medium | Write integration tests with SQLite in-memory for the query executor. |

---

## 9. Testing Strategy

### 9.1 Unit Tests

| Test | Scope |
|---|---|
| `DatabaseResourceServiceTests` | CRUD operations on InternalDatabaseDB / ExternalDatabaseDB |
| `DatabaseAccessModuleTests` | Tool registration, schema generation, `GetTools()` output |
| `QueryExecutorTests` | Per-driver query execution with SQLite in-memory |
| `ResultFormatterTests` | SQL → markdown table, JSON formatting, truncation |

### 9.2 Integration Tests

| Test | Scope |
|---|---|
| End-to-end: register + query | Submit job → module executes → results returned |
| Permission flow | AwaitingApproval for restricted access level |
| Default resource resolution | Channel/context default DB used when no targetId specified |
| Timeout enforcement | Query exceeding timeout is cancelled |
| Read-only guard | Write query rejected for ReadOnly access level |

### 9.3 Regression Tests

| Test | Scope |
|---|---|
| Existing permission checks | `AgentActionService` methods still work after migration |
| `HasMatchingGrant` | DB access grant matching unchanged |
| Lookup API | `/lookup/internalDatabaseAccesses` and `/lookup/externalDatabaseAccesses` still work |
| DefaultResourceSet | `internaldb` and `externaldb` keys still resolve |

---

## 10. Out of Scope

The following are explicitly excluded from this migration:

| Item | Reason |
|---|---|
| Migrating `InternalDatabaseDB` / `ExternalDatabaseDB` entities out of Infrastructure | Entity models are owned by DbContext; moving them would break EF migrations |
| Migrating `InternalDatabaseAccessDB` / `ExternalDatabaseAccessDB` | Part of the core permission model |
| Removing permission fields from `PermissionSetDB` | Still needed for permission evaluation |
| Removing `DefaultResourceSetDB` fields | Still needed for default resource resolution |
| Removing `AgentActionService` permission methods | Still called by `DispatchPermissionCheckAsync` |
| Removing `AgentJobService` helper method entries | All are permission/resource plumbing, not execution code |
| Database migration tooling (schema management) | Separate future feature |
| NoSQL-specific query builders | Module handles raw queries; schema-aware builders are future work |
| Stored procedure support | Can be added later via query parameter binding |

---

## Appendix A: File Impact Summary

| File | Phase | Change |
|---|---|---|
| `SharpClaw.Application.Core/Services/DatabaseResourceService.cs` | 0 | **NEW** — CRUD service |
| `SharpClaw.Contracts/DTOs/Databases/*.cs` | 0 | **NEW** — Request/response DTOs |
| `SharpClaw.Application.API/Handlers/ResourceHandlers.cs` | 0 | Add database CRUD endpoints |
| `SharpClaw.Application.API/Cli/CliDispatcher.cs` | 0 | Add `resource internaldb` / `resource externaldb` commands |
| `DefaultModules/DatabaseAccess/DatabaseAccess.csproj` | 1 | **NEW** — Module project |
| `DefaultModules/DatabaseAccess/module.json` | 1 | **NEW** — Module manifest |
| `DefaultModules/DatabaseAccess/DatabaseAccessModule.cs` | 1-2 | **NEW** — Module implementation |
| `DefaultModules/DatabaseAccess/QueryExecutors/*.cs` | 2 | **NEW** — Per-driver executors |
| `SharpClaw.Application.Core/Services/ChatService.cs` | 3 | Remove 3 tool entries + 1 schema builder |
| `SharpClaw.Contracts/Enums/AgentActionType.cs` | 3 | Add `[Obsolete]` to 3 enum values |
| `SharpClaw.Application.Core/Services/AgentJobService.cs` | — | **No changes** — all entries are permission plumbing |
| `SharpClaw.Application.Core/Services/AgentActionService.cs` | — | **No changes** — permission methods stay |
| `SharpClaw.Application.Infrastructure/Models/Resources/*DatabaseDB.cs` | — | **No changes** |
| `SharpClaw.Application.Infrastructure/Models/Access/*DatabaseAccessDB.cs` | — | **No changes** |
| `SharpClaw.Application.Infrastructure/Models/Clearance/PermissionSetDB.cs` | — | **No changes** |
| `SharpClaw.Application.Infrastructure/Models/Context/DefaultResourceSetDB.cs` | — | **No changes** |

---

## Appendix B: Comparison with Other Migrations

| Aspect | mk8.shell | CU / OA | Database Access |
|--------|-----------|---------|-----------------|
| Existing execution code | ~14,000 lines | ~2,300 lines | 0 lines |
| Existing services | `Mk8WorkspaceFactory` | Desktop, Spreadsheet, COM | None |
| REST endpoints | None (was CLI-only) | None | None |
| CLI commands | Inline dispatch | None | None |
| Permission wiring | Existed | Existed | Existed |
| Entity models | SystemUser, Container | DisplayDevice, Document | InternalDB, ExternalDB |
| Migration complexity | High (project dependency) | Medium (service extraction) | **Low** (greenfield + cleanup) |
| Primary work | Wrap existing library | Extract inline handlers | **Implement new functionality** |
