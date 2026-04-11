# SharpClaw Module: Database Access

> **Module ID:** `sharpclaw_database_access`
> **Display Name:** Database Access
> **Version:** 1.0.0
> **Tool Prefix:** `db`
> **Platforms:** Windows, Linux, macOS
> **Exports:** none
> **Requires:** none

---

## Overview

The Database Access module provides registration and querying of internal
(SharpClaw-managed) and external databases. Supports PostgreSQL, MySQL,
MariaDB, SQLite, MSSQL, and CockroachDB for query execution. Additional
types (MongoDB, Redis, CosmosDB, Oracle, Firebird, Custom) can be
registered but are not yet supported for live queries.

External database connection strings are AES-GCM encrypted at rest.

Tools are dispatched via the SharpClaw module system
(`AgentActionType = ModuleAction`). Tool names are prefixed with `db_`
when sent to the model — for example, `register_database` becomes
`db_register_database`.

---

## Table of Contents

- [Enums](#enums)
- [Tools](#tools)
  - [db_register_database](#db_register_database)
  - [db_access_internal_databases](#db_access_internal_databases)
  - [db_access_external_database](#db_access_external_database)
- [CLI Commands](#cli-commands)
- [Resource Dependencies](#resource-dependencies)
- [Role Permissions](#role-permissions)
- [Module Manifest](#module-manifest)

---

## Enums

### DatabaseType

| Value | Int | Description |
|-------|-----|-------------|
| `MySQL` | 0 | MySQL database |
| `PostgreSQL` | 1 | PostgreSQL database |
| `SQLite` | 2 | SQLite file database |
| `MSSQL` | 3 | Microsoft SQL Server |
| `MongoDB` | 4 | MongoDB (registration only — query not yet supported) |
| `Redis` | 5 | Redis (registration only — query not yet supported) |
| `CosmosDB` | 6 | Azure Cosmos DB (registration only) |
| `MariaDB` | 7 | MariaDB database |
| `Oracle` | 8 | Oracle (registration only) |
| `CockroachDB` | 9 | CockroachDB (PostgreSQL-compatible) |
| `Firebird` | 10 | Firebird (registration only) |
| `Custom` | 99 | Custom / user-supplied driver configuration |

**Query-capable types:** PostgreSQL, MySQL, MariaDB, SQLite, MSSQL,
CockroachDB. Other types can be registered but `access_external_database`
will throw `NotSupportedException`.

---

## Tools

### db_register_database

Register a new internal or external database resource.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | yes | Display name |
| `databaseType` | string | yes | DatabaseType enum value |
| `location` | string | yes | Connection string (external) or file path (internal) |
| `isExternal` | boolean | no | `true` for external, `false` (default) for internal |
| `description` | string | no | Optional description |

**Permission:** Global — requires `canRegisterDatabases` flag
(delegates to `RegisterDatabaseAsync`).

**Returns:** Registration confirmation with ID and type.

---

### db_access_internal_databases

Query an internal (SharpClaw-managed) database.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | Internal database resource GUID |
| `query` | string | yes | SQL or database-appropriate query |
| `timeout` | integer | no | Query timeout in seconds (1–120, default 30) |

**Permission:** Per-resource — requires `internalDatabaseAccesses` grant.

**Returns:** Query results formatted as a table (max 64 KB).

---

### db_access_external_database

Execute a query against a registered external database. The query
language must match the database type (SQL for relational, MongoDB
query JSON for MongoDB, Redis commands for Redis).

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | External database resource GUID |
| `query` | string | yes | Raw query string |
| `timeout` | integer | no | Query timeout in seconds (1–120, default 30) |

**Permission:** Per-resource — requires `externalDatabaseAccesses` grant.

**Returns:** Query results formatted as a table (max 64 KB).

---

## CLI Commands

The module registers a top-level `db` command (alias: `database-access`)
and two resource commands:

```
db list-internal                List registered internal databases
db list-external                List registered external databases
```

```
resource internaldb add <name> <dbType> <path> [description]
resource internaldb get <id>
resource internaldb list
resource internaldb update <id> [name] [dbType] [path] [desc]
resource internaldb delete <id>
```

```
resource externaldb add <name> <dbType> <connectionString> [description]
resource externaldb get <id>
resource externaldb list
resource externaldb update <id> [name] [dbType] [connStr] [desc]
resource externaldb delete <id>
```

---

## Resource Dependencies

| Resource Type | Used by |
|---------------|---------|
| Internal Databases | `db_access_internal_databases` |
| External Databases | `db_access_external_database` |

---

## Role Permissions

### Global flags

| Flag | Tools |
|------|-------|
| `canRegisterDatabases` | `db_register_database` |

### Per-resource arrays

| Array | Resource Type | Tools |
|-------|---------------|-------|
| `localInfoStoreAccesses` | InternalDatabases | `db_access_internal_databases` |
| `externalInfoStoreAccesses` | ExternalDatabases | `db_access_external_database` |

---

## Module Manifest

```json
{
  "id": "sharpclaw_database_access",
  "displayName": "Database Access",
  "version": "1.0.0",
  "toolPrefix": "db",
  "entryAssembly": "SharpClaw.Modules.DatabaseAccess",
  "minHostVersion": "1.0.0",
  "platforms": ["windows", "linux", "macos"],
  "exports": [],
  "requires": []
}
```
