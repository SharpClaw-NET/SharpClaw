# SharpClaw Kernel Architecture Specification

## Status And Authority

This document is the owner-authoritative SharpClaw product specification.
It replaces earlier simple-kernel proposals and roadmap interpretations.

`2026-07-31-modular-direct-chat-kernel-proposal.md` is not owner-approved.
It is non-authoritative and must not control implementation or review.

No earlier proposal can change this specification.
An owner-approved amendment must identify this document and state each changed requirement.

The current implementation does not yet satisfy this specification.
Passing tests against a different architecture does not establish conformance.

## Product Objective

SharpClaw is a hypermoddable and hypertunable LLM kernel application.
The kernel provides a small execution base that modules can extend and tune.

The kernel must not contain optional product domains.
Optional behavior must come from an enabled feature module.

The module graph must not add a hidden fallback path.
An enabled module failure must fail closed when its declared behavior is required.

## Default Installation

A default installation lets the user select a provider and model.
The user sends one message, and the selected model returns one reply.

The default installation has no conversation history.
Each message is independent unless an enabled module supplies history and context behavior.

The default installation has no agent, thread, channel, context, permission, skill, or memory behavior.
It must not create hidden substitutes for these absent domains.

The default installation can load provider modules and tool modules.
The model receives tools from the enabled tool modules.

The default database contains only kernel-owned and integral subsystem data.
It contains no inactive schema for Context, Permissions, Agents, Skills, or Memory.

## Kernel Responsibilities

The kernel owns provider and model selection, one-shot model invocation, streaming, tool registration, and tool invocation.
It also owns Jobs, module loading, module composition, and the universal action and event substrate.

Jobs and Events are integral kernel functions.
They are not optional feature modules.

An internal registrar can use the module graph as an implementation mechanism.
This mechanism does not make Jobs or Events removable feature modules.

The kernel must remain permission-neutral.
It must not contain a permission engine, permission policy, clearance rule, role rule, approval rule, or permission fallback.

## Jobs

Jobs is a canonical kernel subsystem.
It provides durable and interruptible execution for work that does not finish in the current request.

An enabled tool can create a job through the canonical Jobs contract.
The user can also submit a job directly when the user supplies all required job data.

The Jobs path must expose submission, validation, dispatch, progress, completion, failure, cancellation, recovery, and persistence boundaries.
These boundaries must use the same action and event substrate as the remaining kernel.

Jobs must not require Agents, Threads, Channels, Contexts, Permissions, Skills, or Memory.
Feature modules can attach their data through declared contracts.

## Universal Action And Event Substrate

Every public kernel operation must use one registered action path.
Every public Jobs operation must use that same action path.

Modules can intercept permitted actions and events through exact, category, and wildcard registrations.
The compiled graph must reject missing, duplicate, incompatible, or unauthorized declarations.

Events are a canonical kernel substrate.
Modules can publish and consume declared events without creating a second event system.

## Provider And Tool Modules

Provider modules supply provider-specific transport and model behavior.
Tool modules supply model-visible tools and their handlers.

The kernel must not contain a provider-specific or tool-specific fallback implementation.
Disabling a provider or tool module removes its supplied behavior.

A tool can complete in the current request or create a Job.
The owning tool contract selects the applicable behavior.

## Agent Orchestration Repository Ownership

The Agent Orchestration repository owns the extracted legacy feature implementations.
These implementations must use the current SharpClaw module contracts.

The repository must supply the Context module, Two Tier Permission module, and Agents module.
Additional supporting modules can exist when they preserve a clear ownership boundary.

The base SharpClaw kernel must not carry a private copy of these module implementations.
The base repository can contain only neutral contracts required for module composition.

Each feature module owns its database elements and persistence behavior.
It must use the existing SharpClaw module persistence contracts described below.

## Context Module

The Context module owns Threads, Channels, Contexts, conversation history, and context assembly.
It restores the complete former thread, channel, and context behavior.

When the Context module is absent, the kernel stores no conversation history.
It does not create a hidden thread, channel, context, or conversation record.

When the Context module is enabled, it supplies history and context through declared kernel extension points.
It must not replace the kernel with a second chat path.

## Two Tier Permission Module

The Two Tier Permission module owns the complete former Permissions behavior.
It must reside outside the kernel and outside the base Core package.

The first tier evaluates the acting subject capability and role clearance.
The second tier evaluates Channel and Context preauthorization.

The module must preserve all former clearance states, scope precedence, hard denials, whitelists, delegated checks, and approval rules.
It must also preserve permission administration, resource grants, permission persistence, APIs, and CLI commands.

The module must preserve the former privilege-escalation protections.
A caller cannot grant authority that the caller does not hold.

When the module is absent, the kernel performs no permission evaluation.
The kernel must not use a simplified replacement policy.

When the module is enabled, a startup or runtime failure must fail closed.
The application must not silently continue without the required permission checks.

## Agents Module

The Agents module owns Agents, Skills, and Memory.
It restores the complete former agent, skill, and memory behavior.

The module can use Jobs and Context contracts without moving its domain into the kernel.
Its absence must leave no hidden agent, skill catalog, or memory store.

The module must expose its service and persistence surfaces through declared contributions.
The base client must not contain obsolete agent, skill, or memory navigation behavior.

## Module Database Boundary

The existing SharpClaw module persistence contracts are authoritative.
This specification must not create a second database extension system.

`IKernelBuilder.Storage` and `IStorageContractBuilder` declare host-managed module storage.
They use `ScopedStorageContractDescriptor` for operations, indexes, ownership, and storage limits.

Modules use `IScopedStorageGateway` and the existing typed document helpers to access that storage.
The current revision, fencing, claim, query, and atomic outbox rules remain authoritative.

`IOwnedDbContextFactory` is the existing boundary for a module-owned Entity Framework model.
The current module `DbContext` discovery and registration path binds each model to its module owner.

A module-owned `DbContext` and its entity types must remain in the owning module package.
The host configures the selected database provider through `IOwnedDbContextFactory`.

Feature modules must use these existing persistence contracts.
They must not access `SharpClawDbContext` directly or add another storage gateway, schema registry, or persistence host.

If exact legacy parity needs a missing operation, the package owner must extend the applicable existing contract.
It must not add a local bridge, direct database path, or parallel repository system.

The module owns its repositories, queries, data validation, and persistence rules.
Core must not contain a repository or query for a module-owned domain.

The existing host must reject duplicate storage ownership and unregistered module contexts.
A module cannot access another module's storage without an explicit contract from that owner.

Module removal must stop all services and queries that use the module data.
Any required data disposition support must extend the existing module lifecycle and persistence contracts.

The Context module must own all Thread, Channel, Context, history, and context-assembly data.
The Two Tier Permission module must own all permission, clearance, grant, and approval data.

The Agents module must own all Agent, Skill, and Memory data.
Jobs must continue to own canonical job, progress, recovery, and completion data in the kernel.

## User Interface Boundary

Module-defined user-interface contributions are outside the current specification.
This stage must not use or expand the existing UI contribution contracts.

The base application must remove obsolete feature-specific pages, navigation entries, view models, and UI service registrations.
It must not keep inactive UI elements for Context, Permissions, Agents, Skills, or Memory.

Future module UI behavior requires a separate owner-approved amendment to this specification.
Database support does not authorize a module to inject UI elements.

## Legacy Behavior Parity

Extraction must preserve the former behavior exactly unless an owner-approved specification changes it.
Moving code into a module does not authorize behavior removal or simplification.

The Context module must prove parity for Threads, Channels, Contexts, history, and context assembly.
The Two Tier Permission module must prove parity for both permission tiers and all clearance outcomes.

The Agents module must prove parity for Agents, Skills, Memory, and their existing interactions.
Cross-module tests must prove the former integrated workflows through the new module graph.

The parity suite must test success, denial, cancellation, failure, persistence, restart, and recovery behavior.
Source exclusion without a working replacement module is not extraction.

Current parity applies to service, API, CLI, database, and execution behavior.
User-interface parity remains deferred until an owner-approved UI extension specification exists.

## Hypertunability

Each owning component must expose its supported tuning values through typed configuration contracts.
The kernel must not hard-code tuning policy for an optional feature module.

Provider, tool, Jobs, Context, Permissions, and Agents tuning must remain with their owning component.
Configuration validation must reject unknown, incompatible, or unsafe values before activation.

Tuning must not create an unregistered execution path.
All tuned behavior must continue through the compiled action and event graph.

## Composition Rules

The module graph is the only feature composition authority.
The host must not construct an optional feature implementation outside that graph.

The graph must validate module contracts before it publishes an active snapshot.
Missing required dependencies must block activation.

Disabling a feature module removes its behavior, database access, and active services.
It must not enable a kernel fallback that imitates the removed feature.

The base application must remove obsolete feature-specific database elements and UI elements.
An excluded source file, inactive table, unused mapping, or hidden page is not an acceptable compatibility mechanism.

## Repository And Package Boundary

SharpClaw.Core contains only the neutral kernel and its integral Jobs and Events behavior.
SharpClaw.Contracts contains only neutral kernel, Jobs, Events, provider, tool, and module contracts.

SharpClaw.Contracts contains the existing module persistence interfaces and descriptors.
SharpClaw.Core compiles module storage declarations into the module graph.

The Runtime host and infrastructure implement the existing gateways, context factory, provider binding, registry, and ownership checks.
These components must not contain feature-specific schema declarations, mappings, repositories, queries, or initialization data.

Agent Orchestration packages contain the Context, Two Tier Permission, and Agents modules.
Permission-specific contracts and persistence types must not remain in base Core or base Contracts.

The SharpClaw application composes published packages.
It must not retain excluded legacy source as an inactive substitute for a missing module.

The application must remove obsolete feature-specific UI and database registrations from its startup graph.
Module-owned database declarations become active only with their owning published module.

## Migration Gate

The migration must not remove a working legacy feature before its replacement module passes parity tests.
Each replacement must exist as source, a package, and a production composition path.

The replacement module must pass its focused suite and the integrated legacy parity suite.
The complete application must then pass a clean exact-commit gate.

The migration is incomplete while old feature files remain excluded without a working replacement.
Residual user-interface or database files do not constitute a replacement module.

The migration must identify every former feature-specific database element and assign one disposition.
It must transfer required data to the owning module or remove obsolete data through an approved cleanup operation.

A transfer must preserve the identities, relationships, ordering, timestamps, state, and recovery data required for parity.
The old and new owners must not remain active together.

The migration must also identify and remove every obsolete feature-specific UI element from the base application.
No current acceptance gate requires a replacement module UI.

## Acceptance Boundary

A clean default installation must support stateless model chat with selected providers and enabled tools.
It must also support canonical Jobs without loading Agent Orchestration modules.

The Agent Orchestration modules must restore all former service, API, CLI, persistence, and execution behavior for their domains.
The restored behavior must remain outside the kernel.

Each module must define and use its database elements through the existing module persistence contracts.
The base application must contain no obsolete feature-specific UI or active database ownership.

The Two Tier Permission module must prove both safety tiers through production paths.
The kernel must contain zero permission policy or permission evaluation implementation.

The roadmap is complete only when these boundaries and parity requirements pass together.
Catalog coverage alone does not establish product conformance.
