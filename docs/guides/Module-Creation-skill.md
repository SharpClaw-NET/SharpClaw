# SharpClaw Module Authoring Reference

Use this reference after reading `Module-Creation-Guide.md`. It names the current public API and the required authority boundaries.

## Source Shape

| Item | Required value |
| --- | --- |
| Entry interface | `ISharpClawModule` |
| Identity | `ModuleIdentity` |
| Registration entry | `ConfigureServices(IServiceCollection)` |
| Lifecycle start | `StartAsync(ServiceStartContext, CancellationToken)` |
| Lifecycle stop | `StopAsync(CancellationToken)` |
| Manifest parser | `PackageManifestLoader` |
| Graph compiler | `SharpClawModuleCompiler` |

## Contribution API

| Behavior | Registration | Handler |
| --- | --- | --- |
| Tool | `AddTool<T>` | `IToolHandler` |
| Action | `AddAction(...).UseTerminal<T>` | `IHostActionEntryTerminal<TAction,TResult>` |
| Action hook | `OnAction`, `OnActionCategory`, `OnAnyAction` | Action interceptor |
| Event | `AddEvent` and event hook extensions | Event interceptor or listener |
| HTTP | `AddHttpEndpoint<T>` | `IHttpEndpointHandler` |
| WebSocket | `AddWebSocketEndpoint<T>` | `IWebSocketEndpointHandler` |
| CLI | `AddCliCommand<T>` | `ICliHandler` |
| Contract | `ExportContract<T>`, `RequireContract<T>` | Shared public service type |
| Storage | `AddStorage` | `IScopedStorageGateway` |
| Chat | Chat resolver and contributor extensions | Neutral chat interfaces |

## Authorization API

| Role | API | Allowed result |
| --- | --- | --- |
| Provider | `IAuthorizationPolicy` and `AddAuthorizationPolicy<T>` | Allow or deny |
| Consumer | `RequireAuthorization` and `HostAuthorizationEntry` | Uses active provider |
| Restriction | `IAuthorizationRestriction` and `AddAuthorizationRestriction<T>` | Preserve or deny |

The contract name is `sharpclaw.authorization`. The service type is `SharpClaw.Contracts.Kernel.AuthorizationContract`. A restriction requests `Inspect`, `Wrap`, and `Observe` for `authorization.evaluate`.

## Authority Rules

Caller authority comes only from host-issued contexts. Do not put a principal in an action payload. Complete authorization before protected work. Pass the dispatcher cancellation token to each nested operation.

Cross-package calls use `IHostActionEntry` from an active action, chat, tool, endpoint, or CLI context. Do not create a second root request.

## Scope Rules

Register request behavior as scoped. Resolve it only during execution. Do not capture the root provider. Do not keep a scoped handler after its execution ends.

## Testing

`SharpClawModuleTestBuilder.AddRegistration(module, manifestPath)` loads the real manifest and host mode. `ActionEntry` runs one registered terminal through the production Core dispatcher. `ApproveSensitiveContributions` grants one exact package identity.

```csharp
await using var host = new SharpClawModuleTestBuilder()
    .AddRegistration(package, manifestPath)
    .ApproveSensitiveContributions(package.Identity.Id)
    .UseExecutionContext(caller, features)
    .Build();
```

Test allowance, denial, cancellation, disposal, malformed input, route collisions, contract identity, and pre-write rejection. Use both hosting modes when the package supports both modes.

## Prohibited Design

Do not reference Runtime implementation projects. Do not access a host database context. Do not add a compatibility shim, local authority, private protocol, retry route, second dispatcher, second session, or alternate storage path.
