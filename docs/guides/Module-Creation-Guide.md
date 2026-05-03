# Creating a SharpClaw Module

> **Enablement reference:** [modules/Module-Enablement-Guide.md](../modules/Module-Enablement-Guide.md)
> **Agent skill:** [Module-Creation-skill.md](Module-Creation-skill.md)

This guide walks through creating, testing, and shipping a SharpClaw module from
scratch — from the minimal skeleton to registering tools, exporting contracts, owning
task triggers, and troubleshooting at runtime.

---

## Table of contents

- [What a module is](#what-a-module-is)
- [Project setup](#project-setup)
- [Minimal skeleton](#minimal-skeleton)
- [Lifecycle methods in depth](#lifecycle-methods-in-depth)
- [Adding agent tools](#adding-agent-tools)
  - [Job-pipeline tools](#job-pipeline-tools)
  - [Inline tools](#inline-tools)
  - [Permission checks](#permission-checks)
- [Adding REST endpoints](#adding-rest-endpoints)
- [Adding CLI commands](#adding-cli-commands)
- [Exporting and consuming contracts](#exporting-and-consuming-contracts)
- [Seed data](#seed-data)
- [Owning task trigger sources](#owning-task-trigger-sources)
- [Enabling your module](#enabling-your-module)
- [Ideas for what to build](#ideas-for-what-to-build)
- [Debugging and troubleshooting](#debugging-and-troubleshooting)

---

## What a module is

A module is a C# class that implements `ISharpClawModule` and is compiled into the
solution. The `ModuleLoader` discovers it at startup automatically — no registration
list to maintain. Whether it runs is controlled by a single line in the core `.env`
file, and it can be toggled on and off at runtime without restarting the process.

Modules can contribute any combination of:

- **Agent tools** — exposed to models via the job pipeline or the inline chat loop
- **REST endpoints** — standard minimal-API routes, mounted at startup
- **CLI commands** — additional verbs in the SharpClaw CLI
- **Service contracts** — typed DI interfaces exported to other modules
- **Task trigger sources** — runtime integrations that fire tasks on external events
- **Seed data** — one-time database rows or config inserted on first install

---

## Project setup

Add a new class library project to the solution. Reference `SharpClaw.Contracts` so
you have access to `ISharpClawModule` and all the supporting types.

```xml
<ItemGroup>
  <ProjectReference Include="..\SharpClaw.Contracts\SharpClaw.Contracts.csproj" />
</ItemGroup>
```

If your module needs to write to the database, also reference
`SharpClaw.Application.Infrastructure`. If it needs core services like agents or
channels, reference `SharpClaw.Application.Core`.

Place the project under `DefaultModules/` by convention:

```
DefaultModules/
  MyModule/
    MyModule.csproj
    MyModule.cs          ← implements ISharpClawModule
    Tools/
      MyToolHandler.cs
```

---

## Minimal skeleton

```csharp
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;

public sealed class MyModule : ISharpClawModule
{
    public string Id          => "my_module";
    public string DisplayName => "My Module";
    public string ToolPrefix  => "my";

    public void ConfigureServices(IServiceCollection services)
    {
        // Register anything this module needs from DI.
    }

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        // Register REST routes here.
    }
}
```

That is a complete, loadable module. `InitializeAsync`, `ShutdownAsync`,
`SeedDataAsync`, `ExportedContracts`, `RequiredContracts`, and
`GetInlineToolDefinitions` all have default no-op implementations on the interface,
so you only override what you need.

> **Tool prefix** must be unique across all loaded modules. Prefix collisions are
> caught at startup with a clear error message.

---

## Lifecycle methods in depth

### `ConfigureServices`

Called once before the DI container is built. Registers services into the shared
`IServiceCollection`. Anything registered here is available to the rest of the
application and to your own handlers via constructor injection.

```csharp
public void ConfigureServices(IServiceCollection services)
{
    services.AddSingleton<MyBackgroundWorker>();
    services.AddScoped<IMyService, MyService>();
}
```

### `InitializeAsync`

Called once after the container is built but before HTTP requests are served. Safe
to resolve services here.

```csharp
public async Task InitializeAsync(IServiceProvider services, CancellationToken ct)
{
    var worker = services.GetRequiredService<MyBackgroundWorker>();
    await worker.StartAsync(ct);
}
```

> **If this method throws, the module is disabled for the current session.** The
> error is logged and the module's state is set to `enabled=false` to prevent boot
> loops. This also poisons any contract your module exports — dependents will
> cascade-fail. Make sure initialization failures are specific and descriptive.

### `ShutdownAsync`

Called during graceful shutdown for every module that successfully initialized.

```csharp
public async Task ShutdownAsync()
{
    await _worker.StopAsync();
}
```

### `SeedDataAsync`

Called once, the first time the module loads on a fresh install. The `.seeded`
marker file prevents it running again on subsequent starts.

```csharp
public async Task SeedDataAsync(IServiceProvider services, CancellationToken ct)
{
    var db = services.GetRequiredService<AppDbContext>();
    db.MyEntities.Add(new MyEntity { Name = "Default" });
    await db.SaveChangesAsync(ct);
}
```

---

## Adding agent tools

### Job-pipeline tools

Job-pipeline tools go through the full `AgentJobService` lifecycle — they create a
job record, support approval flows, and appear in job history. Use these for
anything with side effects, latency, or that the user might want to audit.

Return them from `GetToolDefinitions()`:

```csharp
public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() =>
[
    new(
        Name:             "do_something",
        Description:      "Does something useful. Provide 'target' as the thing to act on.",
        ParametersSchema: JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "target": { "type": "string", "description": "What to act on." }
              },
              "required": ["target"]
            }
            """).RootElement,
        Permission: new ModuleToolPermission(
            IsPerResource: false,
            Check: (agentId, resourceId, caller, ct) =>
                _permissionService.CheckGlobalFlagAsync(agentId, "CanDoSomething", ct)
        )
    )
];
```

The tool is sent to the model as `my_do_something` (prefix + name). To also accept
it under a legacy name, add `Aliases: ["do_something"]` to the definition.

Implement the handler by overriding `ExecuteToolAsync`:

```csharp
public async Task<ModuleToolResult> ExecuteToolAsync(
    string toolName,
    JsonElement arguments,
    Guid agentId,
    IServiceProvider services,
    CancellationToken ct)
{
    if (toolName is "do_something")
    {
        var target = arguments.GetProperty("target").GetString()!;
        // ... do the work ...
        return ModuleToolResult.Success("Done.");
    }

    return ModuleToolResult.NotHandled();
}
```

### Inline tools

Inline tools execute inside the streaming chat loop without creating a job record.
Use them for fast, stateless operations: waiting, reading context, listing things.

```csharp
public IReadOnlyList<ModuleInlineToolDefinition> GetInlineToolDefinitions() =>
[
    new(
        Name:             "ping",
        Description:      "Returns 'pong'. Useful to verify the module is active.",
        ParametersSchema: JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement
    )
];
```

Implement via `ExecuteInlineToolAsync`:

```csharp
public Task<ModuleInlineToolResult> ExecuteInlineToolAsync(
    string toolName,
    JsonElement arguments,
    Guid agentId,
    IServiceProvider services,
    CancellationToken ct)
{
    if (toolName is "ping")
        return Task.FromResult(ModuleInlineToolResult.Success("pong"));

    return Task.FromResult(ModuleInlineToolResult.NotHandled());
}
```

### Permission checks

`ModuleToolPermission` has two modes:

**Custom check** — supply a `Func` that calls whatever service you need:

```csharp
Permission: new ModuleToolPermission(
    IsPerResource: true,
    Check: async (agentId, resourceId, caller, ct) =>
    {
        var svc = caller.Services.GetRequiredService<IMyPermissionService>();
        return await svc.CheckAccessAsync(agentId, resourceId!.Value, ct);
    }
)
```

**Delegate to existing** — reuse a built-in permission category by name:

```csharp
Permission: new ModuleToolPermission(
    IsPerResource: false,
    Check: null,
    DelegateTo: "AccessSafeShellAsync"
)
```

The `DelegateTo` name is validated at registration time, so a typo fails fast at
startup.

---

## Adding REST endpoints

Use `MapEndpoints(IEndpointRouteBuilder app)` with the standard minimal-API pattern.
There is no required path prefix — use whatever makes sense, but `/modules/{id}/`
is the convention for module-scoped routes.

```csharp
public void MapEndpoints(IEndpointRouteBuilder app)
{
    app.MapGet("/my-module/status", async (IMyService svc, CancellationToken ct) =>
    {
        var status = await svc.GetStatusAsync(ct);
        return Results.Ok(status);
    })
    .RequireAuthorization();

    app.MapPost("/my-module/run", async (RunRequest req, IMyService svc, CancellationToken ct) =>
    {
        await svc.RunAsync(req.Target, ct);
        return Results.NoContent();
    })
    .RequireAuthorization();
}
```

---

## Adding CLI commands

Implement `ICliCommandProvider` alongside `ISharpClawModule`. The `CliDispatcher`
discovers all `ICliCommandProvider` implementations and routes input to them.

```csharp
public sealed class MyModuleCliCommands : ICliCommandProvider
{
    private readonly IMyService _svc;

    public MyModuleCliCommands(IMyService svc) => _svc = svc;

    public IReadOnlyList<string> Verbs => ["mymod"];

    public async Task<CliResult> HandleAsync(string[] args, CancellationToken ct)
    {
        return args switch
        {
            ["mymod", "status"] => CliResult.Print(await _svc.GetStatusAsync(ct)),
            ["mymod", "run", var target] => await RunAsync(target, ct),
            _ => CliResult.Unknown()
        };
    }
}
```

Register it in `ConfigureServices`:

```csharp
services.AddSingleton<ICliCommandProvider, MyModuleCliCommands>();
```

---

## Exporting and consuming contracts

If another module (or the core application) should be able to use a service your
module provides, export it as a contract. This makes the dependency explicit and
lets the module loader enforce initialization order.

**Exporting:**

```csharp
public IReadOnlyList<ModuleContractExport> ExportedContracts =>
[
    new("my_data_source", typeof(IMyDataSource), "Provides live data from my hardware.")
];
```

Register the implementation in `ConfigureServices`:

```csharp
services.AddSingleton<IMyDataSource, MyDataSource>();
```

**Requiring (consuming):**

```csharp
public IReadOnlyList<ModuleContractRequirement> RequiredContracts =>
[
    new("my_data_source", IsOptional: false)
];
```

Resolve it in `InitializeAsync` or in your service constructors:

```csharp
var dataSource = services.GetRequiredService<IMyDataSource>();
```

If `IsOptional: true`, the module loads even when the contract provider is absent.
Gate any code that needs it:

```csharp
var dataSource = services.GetService<IMyDataSource>();
if (dataSource is not null)
{
    // feature available
}
```

> **Contract names** use lowercase with underscores, start with a letter, max 60
> characters (e.g. `desktop_capture`, `window_management`). Only one module may
> export a given name at a time.

---

## Seed data

`SeedDataAsync` is the right place for default database rows, default config keys,
and one-time resource creation. It will never run twice on the same install unless
you manually delete the `.seeded` marker.

```csharp
public async Task SeedDataAsync(IServiceProvider services, CancellationToken ct)
{
    var db = services.GetRequiredService<AppDbContext>();

    if (!await db.InputAudioDevices.AnyAsync(ct))
    {
        db.InputAudioDevices.Add(new InputAudioDevice
        {
            Name      = "Default Microphone",
            IsDefault = true
        });
        await db.SaveChangesAsync(ct);
    }
}
```

Guard with an existence check so re-seeding manually doesn't produce duplicates.

---

## Owning task trigger sources

A module can fire tasks in response to external events by implementing
`ITaskTriggerSource`. The task system routes activation for the declared
module-owned trigger keys to your module.

```csharp
public sealed class MyTriggerSource : ITaskTriggerSource
{
    public IReadOnlyList<string> TriggerKeys =>
        ["my_module.on_hotkey", "my_module.on_process_started"];

    public Task EnableTriggerAsync(TaskDefinition def, CancellationToken ct)
    {
        // Start listening for the event defined in def.Triggers.
        return Task.CompletedTask;
    }

    public Task DisableTriggerAsync(Guid taskId, CancellationToken ct)
    {
        // Stop listening.
        return Task.CompletedTask;
    }
}
```

Register it in `ConfigureServices`:

```csharp
services.AddSingleton<ITaskTriggerSourceProvider, MyTriggerSource>();
```

Users will see your module listed in `GET /tasks/trigger-sources` and
`task trigger-sources`. Tasks that declare a trigger kind you own will depend on
your module being enabled — document this prominently in your module's own doc.

---

## Enabling your module

1. Add your module ID to the `Modules` section of
   `Infrastructure/Environment/.env`:

   ```jsonc
   "Modules": {
     "my_module": "true"
   }
   ```

2. Restart the application, or use the CLI to enable it at runtime:

   ```
   module enable my_module
   ```

3. Verify it loaded:

   ```
   module get my_module
   ```

   A `status: enabled` response confirms successful initialization. If status shows
   `failed`, check the application logs for the `InitializeAsync` exception.

---

## Ideas for what to build

- **Notification module** — watch for task completions or agent job failures and push
  a system notification, email, or webhook.
- **File watcher module** — own an `OnFileChanged` trigger source; let tasks react to
  file system events without polling.
- **Hardware sensor module** — export an `ISensorReader` contract; other modules or
  tasks can consume live temperature, battery, or GPU data.
- **Calendar integration** — own a `OnCalendarEvent` trigger; fire tasks at meeting
  start/end without a cron job.
- **Data pipeline module** — expose a `transform_data` tool that agents can call to
  reshape JSON payloads between steps.
- **Local LLM router** — export an `ILocalModelProvider` contract; point the model
  service at a local Ollama or llama.cpp instance for offline capability.
- **Browser automation** — use Playwright or Selenium under the hood; export
  `browser_navigate` and `browser_extract` tools.

---

## Debugging and troubleshooting

**Module doesn't appear in `module list`**
The class doesn't implement `ISharpClawModule`, or the project isn't compiled into
the solution. Check references and rebuild.

**Module status is `failed` after enable**
`InitializeAsync` threw. The full exception is in the application log under the
`[Module:{id}]` category. Fix the error, then `module enable my_module` again — no
restart needed.

**Tool never reaches `ExecuteToolAsync`**
The permission check is returning denied. Add a log line at the top of
`ExecuteToolAsync` — if it never appears, the block is at the pipeline level, not
in your code. Check the `ModuleToolPermission` configuration for that tool.

**Inline tool fires but produces no model output**
`ExecuteInlineToolAsync` returned `NotHandled()` or threw silently. Add a
`Debug.WriteLine` at the top of the method (category `SharpClaw.CLI`) and check
the VS debug output.

**Contract requirement not satisfied at startup**
The module that exports the contract you require either isn't enabled or failed to
initialize. Run `module list` to check, enable it, then retry. If it's optional,
gate the dependent code path with a null check.

**`SeedDataAsync` isn't running**
The `.seeded` marker already exists. Delete it from the module's data directory and
restart to force a re-seed.

**Trigger never fires**
Confirm `EnableTriggerAsync` was called — add a log line. If it was called but
events still don't fire, the OS-level hook (e.g. hotkey registration, process watcher)
may have failed silently. Check platform prerequisites and permission levels.
