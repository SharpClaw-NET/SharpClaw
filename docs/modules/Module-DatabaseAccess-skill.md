SharpClaw Module: Database Access — Agent Skill Reference

Module ID: sharpclaw_database_access
Display Name: Database Access
Tool Prefix: db
Version: 1.0.0
Platforms: Windows, Linux, macOS
Exports: none
Requires: none

────────────────────────────────────────
ENABLING
────────────────────────────────────────
.env key: Modules:sharpclaw_database_access
Default: disabled
Prerequisites: none
Platform: Windows, Linux, macOS

To enable, add to your core .env (Infrastructure/Environment/.env) Modules section:
  "sharpclaw_database_access": "true"

To disable, set to "false" or remove the key (missing = disabled).

Runtime toggle (no restart required):
  module disable sharpclaw_database_access
  module enable sharpclaw_database_access

See docs/Module-Enablement-Guide.md for full details.

────────────────────────────────────────
OVERVIEW
────────────────────────────────────────
Register and query internal/external databases. Supports PostgreSQL,
MySQL, MariaDB, SQLite, MSSQL, CockroachDB for live queries. Other
types (MongoDB, Redis, CosmosDB, Oracle, Firebird, Custom) can be
registered but are not yet query-capable. External connection strings
are AES-GCM encrypted at rest.

Tools are dispatched via the module system (AgentActionType = ModuleAction).
Tool names are prefixed with "db_" when sent to the model.

────────────────────────────────────────
ENUMS
────────────────────────────────────────
DatabaseType: MySQL (0), PostgreSQL (1), SQLite (2), MSSQL (3),
  MongoDB (4), Redis (5), CosmosDB (6), MariaDB (7), Oracle (8),
  CockroachDB (9), Firebird (10), Custom (99).

Query-capable: PostgreSQL, MySQL, MariaDB, SQLite, MSSQL, CockroachDB.

────────────────────────────────────────
TOOLS (3)
────────────────────────────────────────

db_register_database
  Register a new internal or external database resource.
  Params: name (string, required), databaseType (string, required),
          location (string, required — connection string or file path),
          isExternal (bool, optional — default false),
          description (string, optional)
  Permission: global (RegisterDatabases)

db_access_internal_databases
  Query an internal (SharpClaw-managed) database.
  Params: targetId (internal database GUID, required),
          query (string, required), timeout (int, optional — 1-120, default 30)
  Permission: per-resource (InternalDatabase)
  Returns: query results as formatted table (max 64 KB).

db_access_external_database
  Execute a query against a registered external database.
  Query language must match the database type.
  Params: targetId (external database GUID, required),
          query (string, required), timeout (int, optional — 1-120, default 30)
  Permission: per-resource (ExternalDatabase)
  Returns: query results as formatted table (max 64 KB).

────────────────────────────────────────
CLI
────────────────────────────────────────
db list-internal                List registered internal databases
db list-external                List registered external databases

resource internaldb add <name> <dbType> <path> [description]
resource internaldb get <id>
resource internaldb list
resource internaldb update <id> [name] [dbType] [path] [desc]
resource internaldb delete <id>

resource externaldb add <name> <dbType> <connectionString> [description]
resource externaldb get <id>
resource externaldb list
resource externaldb update <id> [name] [dbType] [connStr] [desc]
resource externaldb delete <id>

Aliases: database-access

────────────────────────────────────────
RESOURCE DEPENDENCIES
────────────────────────────────────────
- InternalDatabases — for access_internal_databases
- ExternalDatabases — for access_external_database

────────────────────────────────────────
ROLE PERMISSIONS (relevant)
────────────────────────────────────────
Global flags: canRegisterDatabases
Per-resource: localInfoStoreAccesses (InternalDatabases),
  externalInfoStoreAccesses (ExternalDatabases)
