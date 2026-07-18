# SharpClaw logging

SharpClaw operational logging is one process-owned pipeline in each executable process. Runtime Host, Gateway, and Uno each create exactly one `SharpClawLogRuntime`, which owns one Serilog logger, one Microsoft `ILoggerFactory` provider registration, one normalization and dispatch sink, one bounded dispatcher channel, and one boot identifier. Application and library code uses injected `ILogger<T>` with structured templates and scopes. Serilog is the implementation behind that API; application code does not write directly to a durable writer.

## Process and module streams

Every process boot receives one GUID. Process-owned events are written once to `process/{application}/{bootId}`. A module-owned event is written once to `module/{moduleId}/{bootId}`. The dispatcher selects the stream from host-owned asynchronous ownership context, not from a logger category or a module-supplied property. Console output is rendered from the normalized record after the durable append decision. It is presentation only, so enabling `Logging:ConsoleEnabled` cannot add a durable record or capture the rendered line back into the stream.

An in-process Runtime module receives a host-owned `SharpClawModuleLoggerFactory` wrapper. The wrapper preserves the natural `ILogger<T>` category while applying the trusted module ID, module assembly version, host kind, and shared process boot ID at the point of emission. Gateway endpoint filters apply the equivalent trusted scope around the complete module request. An authenticated Runtime sidecar capability request and every bounded stdout or stderr line use the same module-scoped host logger. A module cannot redirect an event by supplying reserved ownership properties.

## Configuration

The only logging configuration section is `Logging`. The old `Logging:Serilog` section is rejected at startup with a replacement message instead of being read as an alias. There is no provider selector, `Logging:Enabled` switch, or file-enabled fallback. To retain only terminal events, set `Logging:MinimumLevel` to `Fatal`.

The environment form is shown below. The queue is bounded, the flush interval is finite, and console rendering is disabled by default in service deployments.

```text
Logging__MinimumLevel=Information
Logging__Overrides__Microsoft=Warning
Logging__Overrides__Microsoft.AspNetCore=Warning
Logging__Overrides__Microsoft.EntityFrameworkCore=Warning
Logging__Overrides__Uno=Warning
Logging__ConsoleEnabled=false
Logging__RequestLoggingEnabled=true
Logging__QueueCapacity=4096
Logging__FlushIntervalMilliseconds=1000
```

Configuration is parsed once before the host is composed. Invalid levels, booleans, queue capacities, and flush intervals stop startup with a precise configuration error. The Runtime Host, Gateway, and Uno templates use this same key shape.

## Normalization, redaction, and bounds

Normalization and redaction happen once before the durable stream and before console rendering. Rendered messages are limited to 64 KiB of UTF-8, message templates to 8 KiB, exception text to 96 KiB, each property name to 128 bytes, each property value to 4 KiB, the scalar property count to 32, and the normalized record fields to 192 KiB. Truncation records the original byte size in a bounded metadata property when space permits. Oversize operational content is not converted into an artifact.

Authorization values, API keys, access and refresh tokens, passwords, cookies, client secrets, connection strings, encryption keys, prompts, model responses, request bodies, response bodies, and secret-bearing properties are not retained. HTTP diagnostics record only method, path without query values, status, content length, elapsed time, and safe correlation metadata. They do not read request or response bodies. Exceptions are part of the same normalized event and therefore create one record rather than a normal event plus a second exception record.

Sidecar stdout and stderr are streamed through fixed 64 KiB tails. A non-zero sidecar exit can report those tails, but it cannot retain an unbounded `StringBuilder`. Logging failures set runtime health state without recursively logging the failure or blocking business operations indefinitely.

## Queue, durability, and shutdown

There is one bounded channel per process runtime. Trace, Debug, and Information events may be dropped when the channel is full. Warning, Error, and Critical/Fatal events wait only for a bounded capacity interval. Accepted Error and Fatal events use durable storage mode. Drops are counted outside the logger and later summarized as one `RecordsDropped` event in the affected process or module stream without recursion.

Shutdown stops intake, cancels periodic flush intake, drains the channel, flushes all known operational streams, and seals them. The process boot stream is known from construction even when it contains no records, and module streams are sealed after their first accepted event. Operational flushing is not part of the execution database transaction.

## Execution diagnostics

Job logs, task logs, task output, and artifacts remain explicit `ExecutionDiagnosticStore` semantics. They are not sent through Serilog and are not mirrored into operational process or module streams. Terminal task persistence keeps its existing ordering: apply state, prepare compact state and audit metadata, seal task log and task output streams, then call `SaveChanges`. The operational dispatcher may flush independently and cannot weaken that transaction.

## Retrieval and retention

Operational reads use the authenticated cursor facade and `SharpClawLogReader`. Cursor, page byte, record count, and scan byte limits remain enforced by the durable segment store. Process and module boot enumeration retains expiry watermarks and does not infer ownership from namespace text. The additive record fields use the existing decoder, so bodies written before unified logging remain readable without a schema migration.

The durable store remains provider-neutral and encrypted through the existing instance key derivation. Logging adds no `DbContext`, EF provider, relational table, migration, package, or provider-selection branch. The existing process and module retention policies continue to apply to their respective boot streams.
