# Creating a SharpClaw Module

> **Enablement reference:** [modules/Module-Enablement-Guide.md](../modules/Module-Enablement-Guide.md)
> **Agent skill:** [Module-Creation-skill.md](Module-Creation-skill.md)

This guide walks through creating, testing, and shipping a SharpClaw module from
scratch â€” from the minimal skeleton to registering tools, exporting contracts, owning
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
- [Contributing to the task pipeline](#contributing-to-the-task-pipeline)
  - [Module methods (`ITaskStepDescriptorProvider`)](#module-methods-itaskstepdescriptorprovider)
  - [Parser mappings and event handlers (`ITaskParserModuleExtension`)](#parser-mappings-and-event-handlers-itaskparsermoduleextension)
  - [Trigger attributes (`ITaskTriggerAttributeHandler`)](#trigger-attributes-itasktriggerattributehandler)
  - [Trigger sources (`ITaskTriggerSource`)](#trigger-sources-itasktriggersource)
- [Enabling your module](#enabling-your-module)
- [Ideas for what to build](#ideas-for-what-to-build)
- [Debugging and troubleshooting](#debugging-and-troubleshooting)

---

## What a module is

A module is a manifest-backed package that runs as a sidecar process. A C#
module still implements `ISharpClawModule`, but the parent host discovers the
`module.json` manifest and talks to the module through the sidecar protocol
instead of composing the module assembly into the API process. Whether it runs
is controlled by a single line in the core `.env` file, and it can be toggled
on and off at runtime without restarting the Core API process.

`ConfigureServices` still matters for C# modules. Those registrations build the
module sidecar's own service provider, so module internals resolve normally
without giving the module direct access to the parent host container.

Modules can contribute any combination of:

- **Agent tools** â€” exposed to models via the job pipeline or the inline chat loop
- **REST endpoints** â€” standard minimal-API routes, mounted at startup
- **CLI commands** â€” additional verbs in the SharpClaw CLI
- **Service contracts** â€” typed DI interfaces exported to other modules
- **Task pipeline contributions** â€” module methods callable from C# task scripts, parser mappings, trigger attributes, and runtime trigger sources
- **Seed data** â€” one-time database rows or config inserted on first install

Modules can also own configuration under their own Core `.env` section. The
env loader is generic: it reads the JSON-with-comments `.env` and `.dev.env`
files into `IConfiguration`, so the loader does not need a code change when a
new module introduces a section such as `"MyModule"`. Your module owns the
section name, the key names, the defaults, and the code that reads them.
Adding the section to the shipped Core `.env.template` is only needed when the
SharpClaw repo itself wants to advertise a bundled module's default settings.
Third-party modules should document the snippet that users add to their own
Core `.env`.

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
`SharpClaw.Runtime.INF`. If it needs core services like agents or
channels, reference `SharpClaw.Runtime.BLL`.

Keep module source in its own repository or in an external module workspace.
The SharpClaw repository keeps only the TestHarness module as test
infrastructure; production modules are consumed from packages or loaded as
external modules. A small module repository still uses a root project folder
and a nearby manifest:

```
MyModule/
    MyModule.csproj
    MyModule.cs          â† implements ISharpClawModule
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
> loops. This also poisons any contract your module exports â€” dependents will
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

## Module configuration from env

Core `.env` files are JSON-with-comments files loaded into the standard
`IConfiguration` tree before modules are configured. There is no central
registry of module env sections and no env-loader switch statement that must be
updated for each module. If your module needs settings, choose a stable section
name that belongs to the module, document the keys, and read them from DI in the
service that uses them.

For example, a module with id `my_module` can ask users to add this section next
to the existing top-level sections in `SharpClaw.Runtime.INF`
`/Environment/.env`:

```jsonc
"MyModule": {
  "EndpointUrl": "https://example.internal/api",
  "RetrySeconds": "15"
}
```

The module code can then consume those values through normal constructor
injection. The section does not need to appear in `LocalEnvironment`, and the
Core loader does not need to know the module exists.

```csharp
using Microsoft.Extensions.Configuration;

public sealed class MyService(IConfiguration configuration)
{
    private readonly string? _endpointUrl =
        configuration["MyModule:EndpointUrl"];

    private readonly int _retrySeconds =
        configuration.GetValue("MyModule:RetrySeconds", 15);
}
```

Keep defaults in module-owned code so the module still has predictable behavior
when the section is absent. Use a unique, readable section name rather than a
generic name such as `"Settings"` or `"Options"`. If a setting is sensitive,
prefer the Core `.env` because it is the server-side env file and supports the
same encrypted-at-rest path as the rest of Core configuration.

Bundled modules may add their documented defaults to the checked-in
`.env.template` files for discoverability. Third-party modules should not need a
SharpClaw source change for configuration; they should ship documentation that
shows the JSON section to paste into Core `.env`, plus the module enablement
entry under `Modules`.

---

## Adding agent tools

### Job-pipeline tools

Job-pipeline tools go through the full `AgentJobService` lifecycle â€” they create a
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
public async Task<string> ExecuteToolAsync(
    string toolName,
    JsonElement parameters,
    AgentJobContext job,
    IServiceProvider scopedServices,
    CancellationToken ct)
{
    if (toolName is "do_something")
    {
        var target = parameters.GetProperty("target").GetString()!;
        // ... do the work ...
        return $"Done with {target}.";
    }

    throw new NotImplementedException($"Tool '{toolName}' is not handled.");
}
```

### Reporting token usage from module tools

Core automatically records the normal chat provider usage that it performs
itself, but modules often run their own model calls. An OCR module might call a
vision model for every page, a media module might call a model for every chunk,
and a workflow module might call a private model behind its own client. Those
calls still belong to the `AgentJobDB` row that started the module work, so the
module should report them through `IAgentJobCostTracker` instead of trying to
update core tables directly.

Resolve `IAgentJobCostTracker` from the `scopedServices` argument passed to
`ExecuteToolAsync` and call `RecordTokensAsync` with the current
`AgentJobContext.JobId`. The method is additive, so a long media-processing
loop can report usage after each chunk and the final `AgentJobResponse.jobCost`
will show the accumulated prompt, completion, and total tokens. External modules get the
same contract forwarded into their isolated module container, and bundled modules
can resolve it from the same restricted service scope as other host bridges.

```csharp
public async Task<string> ExecuteToolAsync(
    string toolName,
    JsonElement parameters,
    AgentJobContext job,
    IServiceProvider scopedServices,
    CancellationToken ct)
{
    var costTracker = scopedServices.GetRequiredService<IAgentJobCostTracker>();

    var result = await myModelClient.RunAsync(parameters, ct);

    await costTracker.RecordTokensAsync(
        job.JobId,
        result.Usage.PromptTokens,
        result.Usage.CompletionTokens,
        ct);

    return result.Text;
}
```

If a provider returns only a single token total, keep the convention consistent
inside your module and document it near the call site. For example, a client
that receives only a billed token total can report zero prompt tokens and the
billed total as completion tokens, while a client that knows both input and
output token counts should record those two values separately. The core
contract only accepts non-negative token counts; it does not try to infer image,
media, or private-provider usage from module-owned HTTP requests.

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

**Custom check** â€” supply a `Func` that calls whatever service you need:

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

**Delegate to existing** â€” reuse a built-in permission category by name:

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
There is no required path prefix â€” use whatever makes sense, but `/modules/{id}/`
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
    var db = services.GetRequiredService<MyModuleDbContext>();

    if (!await db.KnownTargets.AnyAsync(ct))
    {
        db.KnownTargets.Add(new KnownTarget
        {
            Name      = "Default Target",
            IsDefault = true
        });
        await db.SaveChangesAsync(ct);
    }
}
```

Guard with an existence check so re-seeding manually doesn't produce duplicates.

---

## Contributing to the task pipeline

Tasks have a fixed Core-owned C# language surface and a module-owned operation
surface. Modules do not contribute parser-owned step tables for declarations,
assignment, control flow, return, logging, delay, structured response parsing,
or cancellation waits. They contribute real callable operations, event-handler
names, trigger attributes, and runtime trigger sources. There are four small
interfaces in
`SharpClaw.Contracts.Tasks` that work together:

| Interface | What it contributes |
|-----------|---------------------|
| `ITaskStepDescriptorProvider` | Registration records for module methods callable from task scripts, such as `Chat(...)` or `HttpGet(...)`. |
| `ITaskParserModuleExtension` | Method mappings, event-handler names (`OnTimer`), and per-method parser hints. |
| `ITaskTriggerAttributeHandler` | One trigger attribute (e.g. `[Schedule]`, `[OnWebhook]`). |
| `ITaskTriggerSource` | Runtime watcher that fires bound trigger keys. |

Most modules need only one or two of these. A pure tool module needs none.

### Module methods (`ITaskStepDescriptorProvider`)

Use this when your module wants the parser to recognise a method call inside a
task body and dispatch it through the central `TaskStepRegistry`. The current
contract calls the registration record a `TaskStepDescriptor`; that is runtime
plumbing, not a separate programming model for task authors. Authors write a
normal C#-style method call such as `DoThing(value)`.

```csharp
public sealed class MyStepProvider : ITaskStepDescriptorProvider
{
    public string ModuleId => "my_module";

    public IReadOnlyList<TaskStepDescriptor> Descriptors { get; } =
    [
        new TaskStepDescriptor
        {
            MethodName           = "DoThing",
            StepKey              = "my_module.do_thing",
            OwnerId              = "my_module",
            FirstArgIsExpression = true,
        },
    ];
}
```

Register it in `ConfigureServices`:

```csharp
services.AddSingleton<ITaskStepDescriptorProvider, MyStepProvider>();
```

The `OwnerId` on every descriptor must match `ModuleId`. Method names and
runtime dispatch keys are unique across all modules; duplicates fail at startup.

### Parser mappings and event handlers (`ITaskParserModuleExtension`)

Use this when your module wants to:

Map a method name to a runtime dispatch key with extra parser hints, or map an
event-handler name such as `OnTimer` or `OnMetricThreshold` to a module-owned
trigger key. Do not contribute ordinary C# statement semantics here; Core owns
those language constructs.

```csharp
public sealed class MyParserExtension : ITaskParserModuleExtension
{
    public IReadOnlyDictionary<string, (string StepKey, string ModuleId)> StepKeyMappings { get; } =
        new Dictionary<string, (string, string)>
        {
            ["DoThing"] = ("my_module.do_thing", "my_module"),
        };

    public IReadOnlyDictionary<string, (string TriggerKey, string ModuleId)> EventTriggerMappings { get; } =
        new Dictionary<string, (string, string)>
        {
            ["OnMyEvent"] = ("my_module.on_my_event", "my_module"),
        };

    public IReadOnlySet<string> SingleArgExpressionMethods { get; } =
        new HashSet<string> { "DoThing" };
}
```

Register it in `ConfigureServices`:

```csharp
services.AddSingleton<ITaskParserModuleExtension, MyParserExtension>();
```

### Trigger attributes (`ITaskTriggerAttributeHandler`)

Task scripts are parsed but not compiled, so trigger attributes (`[Schedule]`,
`[OnWebhook]`, `[OnHotkey]`, etc.) are recognised by name. Each attribute name
is owned by exactly one module via a registered handler. The parser routes
matching occurrences to the handler and uses the returned
`TaskTriggerDefinition` directly.

```csharp
public sealed class OnMyEventHandler : ITaskTriggerAttributeHandler
{
    public TaskTriggerDefinition? Handle(TaskTriggerAttributeContext context)
    {
        var p = new Dictionary<string, string?>(StringComparer.Ordinal);
        var name = context.GetStringArg(0);
        if (!string.IsNullOrEmpty(name))
            p["my_module.event_name"] = name;
        return new TaskTriggerDefinition
        {
            TriggerKey = "my_module.on_my_event",
            Parameters = p,
        };
    }
}
```

Expose handlers from your `ITaskParserModuleExtension`:

```csharp
public IReadOnlyDictionary<string, ITaskTriggerAttributeHandler> TriggerAttributeHandlers { get; } =
    new Dictionary<string, ITaskTriggerAttributeHandler>(StringComparer.Ordinal)
    {
        ["OnMyEvent"] = new OnMyEventHandler(),
    };
```

The parser also accepts the `OnMyEventAttribute` long form for the same
handler. Returning `null` declines the attribute.

> Two modules cannot claim the same attribute name. Conflicts are surfaced at
> startup with both claimants and their assemblies.

### Trigger sources (`ITaskTriggerSource`)

Use this for the runtime side of a trigger: the OS hook, the timer loop, the
webhook listener, the metric watcher. The host routes `TaskTriggerBindingDB`
rows whose `Kind` matches one of your `TriggerKeys` to your source.

```csharp
public sealed class MyTriggerSource : ITaskTriggerSource
{
    public IReadOnlyList<string> TriggerKeys =>
        ["my_module.on_my_event"];

    public Task StartAsync(IReadOnlyList<ITaskTriggerSourceContext> contexts, CancellationToken ct)
    {
        // Wire up listeners for each context. Must be idempotent.
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // Release listeners.
        return Task.CompletedTask;
    }
}
```

Register it in `ConfigureServices`:

```csharp
services.AddSingleton<ITaskTriggerSource, MyTriggerSource>();
```

Sources that need to persist their own bookkeeping (cron rows, on-disk
shortcuts, etc.) can override `OwnsBindingPersistence`, `SyncBindingsAsync`,
and `RemoveBindingsAsync` instead of relying on the default
`TaskTriggerBindingDB` upsert. See `ITaskTriggerSource` xmldoc for details.

Users will see your module listed in `GET /tasks/trigger-sources` and
`task trigger-sources`. Document the trigger keys you own in your module's own
doc â€” when the module is disabled, tasks bound to those keys are flagged by
`task preflight`.

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

- **Notification module** â€” watch for task completions or agent job failures and push
  a system notification, email, or webhook.
- **File watcher module** â€” own an `OnFileChanged` trigger source; let tasks react to
  file system events without polling.
- **Hardware sensor module** â€” export an `ISensorReader` contract; other modules or
  tasks can consume live temperature, battery, or GPU data.
- **Calendar integration** â€” own an `OnCalendarEvent` trigger attribute and
  source; fire tasks at meeting start/end without a cron job.
- **Data pipeline module** â€” expose a `transform_data` tool that agents can call to
  reshape JSON payloads between steps.
- **Local LLM router** â€” export an `ILocalModelProvider` contract; point the model
  service at a local Ollama or llama.cpp instance for offline capability.
- **Browser automation** â€” use Playwright or Selenium under the hood; export
  `browser_navigate` and `browser_extract` tools.

---

## Debugging and troubleshooting

**Module doesn't appear in `module list`**
The class doesn't implement `ISharpClawModule`, or the project isn't compiled into
the solution. Check references and rebuild.

**Module status is `failed` after enable**
`InitializeAsync` threw. The full exception is in the application log under the
`[Module:{id}]` category. Fix the error, then `module enable my_module` again â€” no
restart needed.

**Tool never reaches `ExecuteToolAsync`**
The permission check is returning denied. Add a log line at the top of
`ExecuteToolAsync` â€” if it never appears, the block is at the pipeline level, not
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
Confirm `StartAsync` was called on your `ITaskTriggerSource` â€” add a log line.
If it was called but events still don't fire, the OS-level hook (e.g. hotkey
registration, process watcher) may have failed silently. Check platform
prerequisites and permission levels. Confirm the binding row's `Kind` matches
one of your declared `TriggerKeys`.
