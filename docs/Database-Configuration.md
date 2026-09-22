# Database Configuration Guide

## Scope

The Runtime Host selects one installed persistence module before startup. The default provider key is `JSONColdStore`.

The base model contains only ProviderDB, ModelDB, RegistrationStateDB, ConfigurationEntryDB, ScopedStorageRecordDB, and ScopedStorageIndexEntryDB.

Agents, Skills, Memory, Context, Threads, Channels, history, and Permission data belong to their owning modules. The base model does not keep inactive tables for those domains.

## Provider Selection

Set `Database__Provider` in the deployed Runtime `Environment/.env` file. The official module keys are `JSONColdStore`, `PostgreSQL`, `SQLServer`, and `SQLite`; storage is an open module contract, so third-party packages can contribute additional keys without a host change.

Use `JSONColdStore` for the default local installation. It does not need a connection string. `JsonFile` remains a compatibility alias.

Use `PostgreSQL`, `SQLServer`, or `SQLite` with the matching `ConnectionStrings__<Provider>` key. `Postgres` and `SqlServer` remain compatibility aliases. The selected relational database must already contain the schema from the migration assembly carried by that module.

The Runtime does not run a migration endpoint or apply migrations during normal startup. Schema deployment is an explicit administrative operation.

Changing `Database__Provider` currently takes effect when the Runtime builds its next service graph. A provider replacement is also a data move, so safely eliminating the process restart requires an administrative cutover that drains all work, migrates and verifies the target store, builds a complete replacement Runtime generation, and atomically publishes it. Mutating the provider singleton or connection string inside the active graph is not supported because existing scopes and EF internal service providers would continue using the old store.

## Quick Start

Use the deployed Runtime `Environment/.env` file.

Set `Database__Provider=JSONColdStore` for the default provider.

Start the Runtime and wait for `GET /readyz` to return HTTP `200`.

The Runtime creates the default JSONColdStore database during readiness validation. It checks relational connectivity without changing the relational schema.

## Common Configuration

Use canonical dotenv syntax. Use `__` between configuration sections and keys.

```dotenv
Database__Provider=JSONColdStore
Database__EnableDetailedErrors=true
Database__EnableSensitiveDataLogging=false
Database__JsonFile__Compression=Brotli
Database__JsonFile__StartupMode=MetadataOnly
Database__JsonFile__FullScanPolicy=AllowSilentScans
Database__JsonFile__FsyncOnWrite=true
Encryption__EncryptDatabase=true
```

The normalized `IConfiguration` names use colons. For example, `Database:Provider` identifies the same key inside application code.

Keep `Database__EnableSensitiveDataLogging=false` outside controlled local diagnostics. Sensitive logging can expose entity values in process output.

## JSONColdStore

`SharpClaw.Persistence.JSONColdStore` stores the base model through JSONColdStore. The Runtime uses `SharpClawDbContext` and the module's normal EF Core path.

JSONColdStore settings use the `Database__JsonFile__` prefix. The current options include compression, startup mode, full-scan policy, checksums, event logging, snapshots, flush retries, transaction replay retries, and read retries.

Use `MetadataOnly` startup when the provider must validate its catalog without loading every record. Use the provider's documented scan policy when an operation needs a full scan.

The Runtime passes the instance data directory to the JSONColdStore provider. Do not add a second file store or direct JSON persistence path in the application.

`Encryption__EncryptDatabase=true` enables database record encryption through the selected JSONColdStore options. It does not control active environment document protection.

## Relational Providers

Install `SharpClaw.Persistence.PostgreSQL`, then set `Database__Provider=PostgreSQL` and `ConnectionStrings__PostgreSQL`.

Install `SharpClaw.Persistence.SQLServer`, then set `Database__Provider=SQLServer` and `ConnectionStrings__SQLServer`.

Install `SharpClaw.Persistence.SQLite`, then set `Database__Provider=SQLite` and `ConnectionStrings__SQLite`.

Relational command timeouts use `Database__Relational__CommandTimeoutSeconds` or the provider-specific command timeout key. PostgreSQL and SQL Server also support their configured retry settings.

The Runtime checks `CanConnectAsync` before it publishes readiness. A connection failure stops startup and does not switch to JSONColdStore.

The base Runtime model remains the six generic entity types named in this guide. Module-owned EF models use `IOwnedDbContextFactory` and remain in their owning packages.

## Migration Authoring

Official migration and model-snapshot files live in [SharpClaw.Persistence](https://github.com/SharpClaw-NET/SharpClaw.Persistence) and are generated only by that repository's pinned EF Core console tool. Never create or edit those files by hand. Restore the tool in that repository, then run the command for every relational provider whenever `SharpClawDbContext` changes:

```shell
dotnet tool restore
dotnet ef migrations add <MigrationName> --project SharpClaw.Persistence.PostgreSQL --startup-project SharpClaw.Persistence.PostgreSQL --context SharpClawDbContext --output-dir Migrations
dotnet ef migrations add <MigrationName> --project SharpClaw.Persistence.SQLServer --startup-project SharpClaw.Persistence.SQLServer --context SharpClawDbContext --output-dir Migrations
dotnet ef migrations add <MigrationName> --project SharpClaw.Persistence.SQLite --startup-project SharpClaw.Persistence.SQLite --context SharpClawDbContext --output-dir Migrations
```

If an unpublished scaffold is wrong, remove it with `dotnet ef migrations remove` against the same project and scaffold it again. Never alter a published migration; add a new corrective migration through the same console command. Before committing, run `dotnet ef migrations has-pending-model-changes` for all three projects and require a successful no-change result.

## Module Storage

Modules declare storage through `IKernelBuilder.Storage` and `IStorageContractBuilder`.

The host validates `ScopedStorageContractDescriptor` declarations and exposes them through `IScopedStorageGateway`.

Module records use the module identifier, storage name, record key, and declared indexes. String index values are limited to 128 characters so the complete composite index remains valid across every supported relational provider. A module cannot access another module's storage without an explicit contract.

Do not access `SharpClawDbContext` from a module. Do not add a module entity to the base context when the module storage contract can provide the required boundary.

## Active Environment Protection

The active Runtime `Environment/.env` document is protected by the Supprocom.Secrets installation-key boundary. The package owns encryption, recovery, locking, permissions, and canonical dotenv serialization.

The installation key comes from the valid `SHARPCLAW_ENCRYPTION_KEY` override or the Runtime instance key file. An invalid configured override fails startup.

The `.env.template` and `.dev.env.template` files remain plaintext templates. They are not active protected documents.

`Encryption__EncryptProviderKeys` controls provider API-key protection. The in-document `Encryption__Key` value is an application or provider encryption override and can differ from the installation key.

## Readiness And Recovery

The Runtime validates database readiness before it publishes its discovery entry. A failed validation keeps the Runtime unavailable.

For JSONColdStore, the Runtime requests first-run creation through the provider. For relational providers, the Runtime checks connectivity and expects an authorized schema.

The Runtime does not hide provider errors by selecting another provider. Correct the selected provider or its configuration before restart.

## Data Ownership

SharpClaw.Core owns canonical Jobs and Events contracts. The Runtime composes those contracts with the selected provider and module storage gateway.

ProviderDB and ModelDB store generic provider and model selection data. The four module tables store module state, configuration, records, and index entries.

Feature modules own their domain data and persistence rules. The base application does not retain feature-specific repositories, tables, inactive mappings, compatibility readers, or alternate stores.
