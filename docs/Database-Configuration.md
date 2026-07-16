# Database Configuration Guide

> **Applies to:** SharpClaw Core API and CLI
>
> **Default provider:** `JsonFile` (EF Core through JSONColdStore)

SharpClaw supports multiple EF Core database providers. The provider is
selected via a single `.env` key â€” no code changes required.

---

## Table of Contents

- [Supported providers](#supported-providers)
- [Quick start](#quick-start)
- [Provider configuration](#provider-configuration)
  - [JsonFile (default)](#jsonfile-default)
  - [PostgreSQL](#postgresql)
  - [SQL Server](#sql-server)
  - [SQLite](#sqlite)
  - [MySQL / Oracle (stubbed)](#mysql--oracle-stubbed)
- [Migrations](#migrations)
  - [Checking status](#checking-status)
  - [Applying migrations](#applying-migrations)
  - [Creating new migrations](#creating-new-migrations)
  - [Migration gate](#migration-gate)
- [Protected .env](#protected-env)
- [Provider-specific notes](#provider-specific-notes)

---

## Supported providers

| Provider | `Database:Provider` value | Connection string key | Status |
|----------|--------------------------|----------------------|--------|
| JSONColdStore | `JsonFile` | *(none)* | Default |
| PostgreSQL | `Postgres` | `ConnectionStrings:Postgres` | âœ… Supported |
| SQL Server | `SqlServer` | `ConnectionStrings:SqlServer` | âœ… Supported |
| SQLite | `SQLite` | `ConnectionStrings:SQLite` | âœ… Supported |
| MySQL / MariaDB | `MySql` | `ConnectionStrings:MySql` | â³ Stub â€” blocked on Pomelo EFC 10 |
| Oracle | `Oracle` | `ConnectionStrings:Oracle` | â³ Stub â€” blocked on Oracle EFC 10 |

---

## Quick start

1. Open the deployed Runtime Host's `Environment/.env` file.
2. Set `Database__Provider` to your chosen provider.
3. Add the matching connection string under `ConnectionStrings__<Provider>`.
4. Start the API. Check logs for any pending migration warnings.
5. Apply migrations: `POST /admin/db/migrate` or CLI `db migrate`.

---

## Provider configuration

All configuration lives in the Runtime host `.env` file, which uses canonical
dotenv syntax. The same settings can also be supplied as process environment
variables. File and process keys use double underscores, while the
`IConfiguration` API exposes colon-separated paths such as
`Database:JsonFile:Compression`.

Provider behavior belongs in this section, but process placement does not.
The `JsonFile` data directory is resolved from `SHARPCLAW_DATA_DIR` or the
SharpClaw instance root, and API binding remains controlled by
`ASPNETCORE_URLS`. Migration assemblies are also not env knobs: they are
part of the application package layout and stay fixed as
`SharpClaw.Migrations.Postgres`, `SharpClaw.Migrations.SqlServer`, and
`SharpClaw.Migrations.SQLite`.

### EF Core logging and diagnostics

The Core API routes EF Core logs through the standard application logging
pipeline, which now means Serilog when Serilog is enabled for the Core
process.

The following `.env` keys control EF Core diagnostics:

| Key | Default | Description |
|-----|---------|-------------|
| `Database:EnableDetailedErrors` | `true` | Enables EF Core detailed error messages. This is generally safe and useful even outside development. |
| `Database:EnableSensitiveDataLogging` | `false` | Includes parameter values and entity data in EF Core logs. This can expose secrets or personal data in logs, so it should remain off unless you are doing local debugging. |
| `Logging:Serilog:EntityFrameworkCoreMinimumLevel` | `Warning` | Controls how noisy EF Core logging is once it reaches Serilog. Lower it to `Information` or `Debug` when investigating query and change-tracking behavior. |

`Database:Relational:CommandTimeoutSeconds` sets a shared relational command
timeout for PostgreSQL, SQL Server, and SQLite. Provider-specific
`Database:Postgres:CommandTimeoutSeconds`,
`Database:SqlServer:CommandTimeoutSeconds`, and
`Database:SQLite:CommandTimeoutSeconds` override that shared value. PostgreSQL
and SQL Server also expose provider-level retry through
`EnableRetryOnFailure`, `MaxRetryCount`, and `MaxRetryDelaySeconds` under
their provider section. SQLite does not expose provider retry because the EF
SQLite provider has no matching retry strategy option in this registration
path.

Example:

```dotenv
Database__Provider="Postgres"
Database__EnableDetailedErrors="true"
Database__EnableSensitiveDataLogging="false"
Logging__Serilog__Enabled="true"
Logging__Serilog__EntityFrameworkCoreMinimumLevel="Information"
ConnectionStrings__Postgres="Host=localhost;Database=sharpclaw;Username=sharpclaw;Password=YOUR_PASSWORD"
```

### JsonFile (default)

No connection string is needed. Data is stored through the external
`JSONColdStore` EF Core provider, so SharpClaw code uses the same DbContext
and LINQ flow as the relational providers. Legacy file-format handling, if
needed, belongs in the provider package rather than in SharpClaw.

```dotenv
Database__Provider="JsonFile"
Database__JsonFile__Compression="Brotli"
Database__JsonFile__StartupMode="MetadataOnly"
Database__JsonFile__FullScanPolicy="AllowSilentScans"
Database__JsonFile__FsyncOnWrite="true"
Database__JsonFile__FlushRetryMaxRetries="3"
Database__JsonFile__FlushRetryBaseDelayMilliseconds="200"
Database__JsonFile__TransactionReplayMaxRetries="3"
Database__JsonFile__ReadRetryMaxRetries="3"
Database__JsonFile__ReadRetryBaseDelayMilliseconds="25"
Database__JsonFile__IndexRescanIntervalMinutes="60"
Database__JsonFile__QuarantineMaxAgeDays="30"
Database__JsonFile__EnableChecksums="true"
Database__JsonFile__VerifyChecksumsOnRead="false"
Database__JsonFile__EnableEventLog="false"
Database__JsonFile__EventLogRetentionDays="7"
Database__JsonFile__EnableSnapshots="false"
Database__JsonFile__SnapshotIntervalHours="24"
Database__JsonFile__SnapshotRetentionCount="3"
```

`Compression` accepts `None`, `Auto`, or `Brotli`. `StartupMode` accepts
`MetadataOnly` or `FullHydration`. `FullScanPolicy` accepts
`FailUnlessExplicit`, `AllowExplicitScans`, or `AllowSilentScans`. The old
`Database:AsyncFlush` key is no longer present because the provider publishes
saves synchronously in the current package version.

### PostgreSQL

```dotenv
Database__Provider="Postgres"
Database__Postgres__CommandTimeoutSeconds="30"
Database__Postgres__EnableRetryOnFailure="false"
Database__Postgres__MaxRetryCount="6"
Database__Postgres__MaxRetryDelaySeconds="30"
ConnectionStrings__Postgres="Host=localhost;Database=sharpclaw;Username=sharpclaw;Password=YOUR_PASSWORD"
```

**Package:** `Npgsql.EntityFrameworkCore.PostgreSQL` (10.0.1)

### SQL Server

```dotenv
Database__Provider="SqlServer"
Database__SqlServer__CommandTimeoutSeconds="30"
Database__SqlServer__EnableRetryOnFailure="false"
Database__SqlServer__MaxRetryCount="6"
Database__SqlServer__MaxRetryDelaySeconds="30"
ConnectionStrings__SqlServer="Server=.;Database=SharpClaw;Trusted_Connection=True;TrustServerCertificate=True"
```

**Package:** `Microsoft.EntityFrameworkCore.SqlServer` (10.0.5)

### SQLite

```dotenv
Database__Provider="SQLite"
Database__SQLite__CommandTimeoutSeconds="30"
ConnectionStrings__SQLite="Data Source=sharpclaw.db"
```

**Package:** `Microsoft.EntityFrameworkCore.Sqlite` (10.0.5)

> **Note:** SQLite does not natively support `DateTimeOffset`. SharpClaw
> automatically applies a value converter that stores all `DateTimeOffset`
> properties as Unix milliseconds (`long`). This is transparent to the
> application but means raw database values are epoch-based integers.

### MySQL / Oracle (stubbed)

These providers are defined in the `StorageMode` enum but throw
`NotSupportedException` at startup. They are blocked on their respective
EF Core 10 packages:

- **MySQL/MariaDB:** Waiting on
  [Pomelo.EntityFrameworkCore.MySql](https://github.com/PomeloFoundation/Pomelo.EntityFrameworkCore.MySql)
  10.x release.
- **Oracle:** Waiting on
  [Oracle.EntityFrameworkCore](https://www.nuget.org/packages/Oracle.EntityFrameworkCore)
  10.x release.

---

## Migrations

Migrations are **never automatic**. SharpClaw starts and serves requests
even when migrations are pending â€” it logs a warning at startup. You
must explicitly trigger migrations when ready.

### Checking status

**API:**

```
GET /admin/db/status
```

Returns `state` (`Idle` / `Draining` / `Migrating`), `applied` (list),
and `pending` (list).

**CLI:**

```
db status
```

### Applying migrations

**API:**

```
POST /admin/db/migrate
```

**CLI:**

```
db migrate
```

Both require admin privileges. The migration gate will:

1. Close the gate â€” new requests are held.
2. Drain all in-flight requests to completion.
3. Apply all pending migrations.
4. Reopen the gate â€” requests resume.

Returns `409 Conflict` if a migration is already in progress.

### Creating new migrations

Each relational provider has a dedicated migration assembly:

```
SharpClaw.Migrations/Postgres/
SharpClaw.Migrations/SqlServer/
SharpClaw.Migrations/SQLite/
```

To add a new migration (example for PostgreSQL):

```bash
dotnet ef migrations add MyMigrationName \
  --project SharpClaw.Migrations/Postgres/SharpClaw.Migrations.Postgres.csproj \
  --startup-project SharpClaw.Runtime/Host/SharpClaw.Runtime.Host.csproj
```

Each assembly contains a `IDesignTimeDbContextFactory<SharpClawDbContext>`
that provides the design-time connection string.

### Migration gate

The `MigrationGate` is an async-safe pause mechanism that prevents data
corruption during migrations:

- **Normal operation:** Requests pass through the gate with zero overhead
  (the gate `Task` is already completed).
- **During migration:** The gate closes, all in-flight requests drain,
  migrations run, then the gate reopens.
- **Middleware:** `MigrationGateMiddleware` wraps every request in the
  gate automatically.

The gate uses `SemaphoreSlim` + `TaskCompletionSource` (not
`ReaderWriterLockSlim`) to avoid threadpool starvation in async
middleware.

---

## Protected .env

The active Runtime Host `.env` is protected at rest by the
`Supprocom.Secrets` installation-bound file-protection path. The installation
key is selected from the configured installation override or the instance
secret file, and the package owns encryption, recovery, locking, and canonical
dotenv serialization. The `.env.template` and `.dev.env.template` files remain
portable plaintext templates and are never encrypted in place.

`Encryption__EncryptDatabase` controls JSONColdStore database encryption; it
does not decide whether the `.env` document is protected. The in-document
`Encryption__Key` is the application/provider encryption override and may differ
from the installation key used for the protected environment file.

See [Encryption & key management](Core-API-documentation.md#encryption--key-management)
for key resolution and validation details.

---

## Provider-specific notes

| Provider | Issue | Mitigation |
|----------|-------|------------|
| SQLite | No native `DateTimeOffset` support | Auto-applied `ValueConverter`: stored as Unix milliseconds |
| MySQL/MariaDB | InnoDB 767-byte key length limit | Will limit indexed string columns to `MaxLength(255)` â€” deferred |
| Oracle | 30-char identifier limit (pre-21c) | Will use short table names or require 21c+ â€” deferred |
| JSONColdStore | Provider-backed JSON storage | SharpClaw uses provider configuration only; query and persistence behavior belongs in the provider |
| Postgres / SQL Server | Full relational support | No special handling needed |
