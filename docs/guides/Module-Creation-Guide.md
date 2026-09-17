# Build a SharpClaw Module

SharpClaw discovers optional behavior from packages at run time. The base Runtime does not reference an implementation package. A .NET package contains one manifest, one entry assembly, private dependencies, and one `ISharpClawModule` implementation.

The [module enablement guide](../modules/Module-Enablement-Guide.md) explains installation and activation. This guide explains the authoring model.

## Project Setup

Create a .NET 10 class library and reference `SharpClaw.ModuleSDK` with an exact version. The SDK brings the matching neutral contracts into the build. Do not reference a Runtime, Gateway, Agent Orchestration, or persistence implementation project.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="SharpClaw.ModuleSDK" Version="[0.5.0-beta.24]" />
  </ItemGroup>
</Project>
```

## Minimal Module

`ConfigureServices` is the single registration entry. It receives a normal `IServiceCollection` that also records SharpClaw contributions. The compiler validates this complete graph before lifecycle start.

```csharp
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.ModuleSDK;

namespace Example.Documents;

public sealed class DocumentsModule : ISharpClawModule
{
    public ModuleIdentity Identity { get; } = new(
        "documents",
        "Documents",
        "documents");

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<DocumentReader>();
    }
}
```

`StartAsync` and `StopAsync` are optional lifecycle methods. Use them only for package-owned resources. Request-scoped work belongs in scoped handlers, not in the module instance.

## Package Manifest

`package.json` is authoritative package metadata. Property names are case-sensitive. `PackageManifestLoader` applies the same maximum depth and validation rules as both production hosts.

```json
{
  "id": "documents",
  "displayName": "Documents",
  "version": "1.0.0",
  "toolPrefix": "documents",
  "runtime": "dotnet",
  "hostMode": "sidecar",
  "entryAssembly": "Example.Documents.dll",
  "entryType": "Example.Documents.DocumentsModule",
  "minHostVersion": "0.5.0",
  "defaultEnabled": false
}
```

The manifest identity must match `ModuleIdentity`. Use `sidecar` for the normal out-of-process boundary. Use `in-process` only when the operator accepts direct process trust.

## Capability Map

| Capability | Registration | Execution interface |
| --- | --- | --- |
| Tool | `AddTool<THandler>` | `IToolHandler` |
| Typed action | `AddAction(...).UseTerminal<TTerminal>` | `IHostActionEntryTerminal<TAction,TResult>` |
| Action restriction or observation | `OnAction`, `OnActionCategory`, `OnAnyAction` | Typed or untyped action interceptor |
| Event publication and handling | `AddEvent` and event hook extensions | Typed event contracts |
| HTTP route | `AddHttpEndpoint<THandler>` | `IHttpEndpointHandler` |
| WebSocket route | `AddWebSocketEndpoint<THandler>` | `IWebSocketEndpointHandler` |
| CLI command | `AddCliCommand<THandler>` | `ICliHandler` |
| Shared service | `ExportContract<T>` and `RequireContract<T>` | Shared public contract type |
| Storage | `AddStorage` | `IScopedStorageGateway` or `ScopedDocumentStore<T>` |
| Conversation identity | `UseConversationResolver<T>` | `IConversationResolver` |
| Chat profile | `UseChatProfileResolver<T>` | `IChatProfileResolver` |
| Chat context | `AddChatContext<T>` | `IChatContextContributor` |

## Dependency Injection and Scope

Register stateful request behavior as scoped. The host creates a bounded execution scope for each action, event, tool, endpoint, CLI command, and Job operation. The scope remains alive until captured work ends, even when the caller receives an uncertain result.

Do not resolve scoped services from the module constructor or the root provider. Do not keep an `IServiceProvider` for later use. Constructor injection on each handler is the normal path.

## Tools

A tool has one descriptor and one scoped `IToolHandler`. The host creates `ToolInvocation` from validated caller authority. The invocation contains the exact tool name, arguments, conversation identity when present, and host action context.

```csharp
public static class DocumentTools
{
    public static ToolDescriptor Read { get; } = new(
        "read",
        "Reads one document by identifier.",
        ToolSchemas.EmptyObject,
        ContainsSensitiveData: true);
}

public sealed class ReadDocumentTool(DocumentReader reader) : IToolHandler
{
    public async ValueTask<ToolResult> InvokeAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = await reader.ReadAsync(invocation.Arguments, cancellationToken);
        return ToolResult.Text(text);
    }
}
```

Register the handler through `services.AddTool<ReadDocumentTool>(DocumentTools.Read)`. Do not switch on tool names inside one global handler. Each descriptor has one selected handler.

## Typed Actions

Use a typed action when behavior needs interception, exact authority, retry policy, safe points, or a stable terminal. The descriptor owns these controls. `UseTerminal<TTerminal>` binds one stable terminal identifier to the descriptor.

```csharp
public void ConfigureServices(IServiceCollection services)
{
    services.AddAction(DocumentActions.Read)
        .UseTerminal<ReadDocumentTerminal>(DocumentTerminals.Read);
}
```

The terminal implements `IHostActionEntryTerminal<TAction,TResult>`. It receives the authenticated `ActionContext<TAction>` and the dispatcher cancellation token. Do not read caller authority from the action payload.

## Authorization Provider

Use the neutral authorization boundary when a package replaces the active permission system. Implement `IAuthorizationPolicy`, then call `AddAuthorizationPolicy<TPolicy>`. The policy receives the validated request and authenticated action context.

```csharp
public sealed class DocumentAuthorizationPolicy : IAuthorizationPolicy
{
    public ValueTask<AuthorizationDecision> EvaluateAsync(
        ActionContext<AuthorizationRequest> context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var allowed = context.Caller.Roles?.Contains("document-reader") == true;
        return ValueTask.FromResult(allowed
            ? AuthorizationDecision.Allow("document_reader")
            : AuthorizationDecision.Deny(
                "document_denied",
                "The caller cannot read this document."));
    }
}

public void ConfigureServices(IServiceCollection services) =>
    services.AddAuthorizationPolicy<DocumentAuthorizationPolicy>();
```

The provider manifest must export `sharpclaw.authorization` with service type `SharpClaw.Contracts.Kernel.AuthorizationContract`. The host permits one active provider and rejects a blank or changed service type.

## Authorization Restriction

Use `IAuthorizationRestriction` when a package complements another policy. A restriction can preserve or deny access. It cannot grant access, replace caller identity, replace the policy result, or issue authority.

```csharp
public sealed class TenantRestriction : IAuthorizationRestriction
{
    public ValueTask<AuthorizationRestriction> EvaluateAsync(
        AuthorizationRestrictionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(context.Features.Contains("tenant.allowed")
            ? AuthorizationRestriction.Preserve()
            : AuthorizationRestriction.Deny(
                "tenant_denied",
                "The caller cannot access this tenant."));
    }
}

public void ConfigureServices(IServiceCollection services) =>
    services.AddAuthorizationRestriction<TenantRestriction>(
        "tenant",
        HookPriority.High);
```

The restriction manifest must require the same service type. It must request only `Inspect`, `Wrap`, and `Observe` for `authorization.evaluate`. Sensitive approval remains an operator decision.

## Authorization Consumer

Call `RequireAuthorization` in a package that uses the active policy. Inject `HostAuthorizationEntry`, then evaluate before protected work. Use an active action or chat context so nested work keeps the host-issued parent authority.

```csharp
public sealed class DocumentExecutor(HostAuthorizationEntry authorization)
{
    public async ValueTask ExecuteAsync(
        ActionContext<ReadDocumentAction> context,
        CancellationToken cancellationToken)
    {
        var decision = await authorization.EvaluateAsync(
            context,
            new AuthorizationRequest(
                "documents.read",
                new AuthorizationResource(
                    "document",
                    context.Action.DocumentId.ToString("D"))),
            cancellationToken);

        if (!decision.Allowed)
            throw new UnauthorizedAccessException(decision.Message);

        await ReadProtectedDocumentAsync(context.Action.DocumentId, cancellationToken);
    }
}
```

Agent Orchestration maps its resource model to this neutral contract. Its [authorization guide](https://github.com/SharpClaw-NET/SharpClaw.AgentOrchestration/blob/main/docs/permission-modules.md) explains replacement and restriction behavior.

## Shared Contracts

`ExportContract<T>` publishes one service boundary. `RequireContract<T>` consumes it. Put the public service type in a shared package so the provider and consumer load identical bytes.

```csharp
services.AddScoped<IDocumentClock, DocumentClock>();
services.ExportContract<IDocumentClock>("documents.clock");
```

The provider manifest uses `exports`. The consumer manifest uses `requires`. Each typed entry must include the exact `Type.FullName`; a contract name alone does not grant service authority.

## Storage

Declare each storage operation and index through `ScopedStorageContractDescriptor`. Use `IScopedStorageGateway` directly for complete control, or wrap it with `ScopedDocumentStore<T>`. The host remains the only storage authority.

```csharp
services.AddStorage(new ScopedStorageContractDescriptor(
    "documents",
    "metadata",
    [
        new ScopedStorageOperationDescriptor(ScopedStorageOperations.Get),
        new ScopedStorageOperationDescriptor(ScopedStorageOperations.Put),
    ]));

services.AddScoped(provider => new ScopedDocumentStore<DocumentMetadata>(
    provider.GetRequiredService<IScopedStorageGateway>(),
    "documents",
    "metadata",
    "documents"));
```

Do not access a host database context. Do not create a parallel storage path. Validate authorization before every protected write.

## HTTP and WebSocket Routes

Declare routes with `EndpointRouteDescriptor`. The host checks route collisions before mapping. It supplies `HostEndpointRouteRequest` and `IHostActionEntry` after request admission.

```csharp
services.AddHttpEndpoint<DocumentStatusEndpoint>(new EndpointRouteDescriptor(
    "documents.status",
    "/documents/{id}",
    "GET",
    HostEndpointTransport.Http));
```

An HTTP handler implements `IHttpEndpointHandler`. A WebSocket handler implements `IWebSocketEndpointHandler`. Both paths receive host-authenticated authority and the request cancellation token.

## CLI Commands

Declare one `CliCommandDescriptor` and one scoped `ICliHandler`. `CliInvocation` contains the command, arguments, invocation identity, and host action context.

```csharp
services.AddCliCommand<DocumentCli>(new CliCommandDescriptor(
    "documents.inspect",
    ["documents-i"],
    "Inspects document package state.",
    inputSchema,
    resultSchema));
```

Use `IHostActionEntry` from the invocation context for nested protected work. Do not create another root action.

## Chat Contributions

Conversation identity, history storage, profile selection, and context assembly are optional contributions. The base kernel does not infer these features. Use the neutral chat contracts when a package supplies them.

| Need | API |
| --- | --- |
| Resolve a conversation | `UseConversationResolver<TResolver>` |
| Select a chat profile | `UseChatProfileResolver<TResolver>` |
| Store conversation state | `UseConversationStore<TStore>` |
| Add bounded prompt context | `AddChatContext<TContributor>` |

## Test the Real Graph

Reference `SharpClaw.ModuleSDK.Testing` with an exact version. Pass the real manifest path so the test uses its host mode and effect requests. Sensitive contributions require explicit exact approvals.

```csharp
await using var host = new SharpClawModuleTestBuilder()
    .AddRegistration(new DocumentAuthorizationModule(), manifestPath)
    .AddRegistration(new TenantRestrictionModule(), restrictionManifestPath)
    .ApproveSensitiveContributions("document_authorization")
    .ApproveSensitiveContributions("tenant_restriction")
    .UseExecutionContext(caller, features)
    .Build();

var outcome = await host.ActionEntry(
        AuthorizationProtocol.Evaluate,
        authorizationRequest)
    .RunAsync(cancellationToken);
```

`ActionEntry` uses the registered terminal and production Core dispatcher. Each terminal execution gets a new asynchronous service scope. Tests can prove allowance, denial, cancellation, disposal, and pre-write rejection without a fake dispatcher.

## Packaging

Build the package entry assembly and private dependencies into one directory. Put `package.json` at that directory root. Keep shared SharpClaw contract assemblies aligned with the frozen bill of materials.

```text
documents/
  package.json
  Example.Documents.dll
  Example.Documents.deps.json
  private-dependency.dll
```

Do not embed a different SharpClaw contract assembly. Do not use open dependency ranges. Do not include a fallback execution path.

## Failure Reference

| Failure | Meaning |
| --- | --- |
| Manifest mismatch | `ModuleIdentity` and `package.json` disagree. |
| Missing manifest request | Code requested a hook effect that the manifest did not request. |
| Unsupported effect | The descriptor, manifest, or host grant does not permit the effect. |
| Contract mismatch | The provider and consumer service types or schema versions differ. |
| Route collision | Two enabled routes match the same method and route pattern. |
| Missing sensitive approval | The operator did not approve the exact package and schema. |
| Scoped service failure | Code resolved a scoped handler from the root provider. |

Fix the declaration that caused the error. Do not bypass compiler validation or add a second execution path.
