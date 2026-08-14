SharpClaw Module Creation — Agent Skill Reference

Full human-readable guide: guides/Module-Creation-Guide.md

────────────────────────────────────────
WHAT A MODULE IS
────────────────────────────────────────
A manifest-backed module running in a sidecar process.
C# modules still implement ISharpClawModule, but the parent host discovers
module.json and talks to the module through the sidecar protocol.
Enabled/disabled by `Modules__<module_id>` in the deployed Runtime Host's
`Environment/.env`.
Can be toggled at runtime without restarting the Core API process:
module enable/disable <id>

────────────────────────────────────────
REQUIRED INTERFACE MEMBERS
────────────────────────────────────────
string Id                        unique lowercase_underscore identifier
string DisplayName               human-readable name
string ToolPrefix                short prefix for tool names (must be unique)
void ConfigureServices(IServiceCollection)
IReadOnlyList<ModuleToolDefinition> GetToolDefinitions()
void MapEndpoints(IEndpointRouteBuilder)

Optional (have default no-op implementations):
  Task InitializeAsync(IServiceProvider, CancellationToken)
  Task ShutdownAsync()
  Task SeedDataAsync(IServiceProvider, CancellationToken)
  IReadOnlyList<ModuleInlineToolDefinition> GetInlineToolDefinitions()
  IReadOnlyList<ModuleContractExport> ExportedContracts
  IReadOnlyList<ModuleContractRequirement> RequiredContracts

────────────────────────────────────────
LIFECYCLE ORDER
────────────────────────────────────────
ConfigureServices → (container built) → InitializeAsync → SeedDataAsync
  → MapEndpoints → [runtime] → ShutdownAsync

InitializeAsync throws → module disabled, exported contracts poisoned,
  dependent modules cascade-fail.
SeedDataAsync runs once only (.seeded marker file guards repeat calls).

────────────────────────────────────────
TOOL REGISTRATION
────────────────────────────────────────
Job-pipeline tools (full job lifecycle, auditable):
  Return ModuleToolDefinition records from GetToolDefinitions().
  Handle in ExecuteToolAsync(toolName, parameters, job, scopedServices, ct).
  Tool exposed to model as: {prefix}_{name}
  Aliases: IReadOnlyList<string>? on the record for legacy names.

Inline tools (stateless, no job record, runs in chat loop):
  Return ModuleInlineToolDefinition records from GetInlineToolDefinitions().
  Handle in ExecuteInlineToolAsync(toolName, parameters, context, scopedServices, ct).

ModuleToolDefinition constructor:
  Name, Description, ParametersSchema (JsonElement), Permission, TimeoutSeconds?,
  Aliases?

ModuleToolPermission:
  IsPerResource (bool)
  Check: Func<Guid, Guid?, ActionCaller, CancellationToken, Task<AgentActionResult>>?
  DelegateTo: string?  (name of existing AgentActionService method; validated at startup)

Return values:
  ExecuteToolAsync returns the string persisted as the job result.
  ExecuteInlineToolAsync returns the string inserted into the chat loop.
  Throw NotImplementedException for tool names the module does not handle.

Job cost tracking:
  Modules that spend tokens outside the core chat pipeline should resolve
  SharpClaw.Contracts.Modules.IAgentJobCostTracker from the scopedServices
  argument passed to ExecuteToolAsync and call RecordTokensAsync(job.JobId,
  promptTokens, completionTokens, ct). Calls are additive, so OCR, media, or
  private model pipelines can report usage after every chunk and the
  host will expose the accumulated total through the current Job result.
  External modules receive this contract through the host bridge, so they do
  not update host database tables directly.

────────────────────────────────────────
CONTRACTS
────────────────────────────────────────
Export — register interface in DI + declare in ExportedContracts:
  new ModuleContractExport(contractName, typeof(IMyInterface), description?)
  contractName: lowercase_underscore, max 60 chars, unique across all modules

Require — declare in RequiredContracts:
  new ModuleContractRequirement(contractName, IsOptional: false)
  IsOptional: true → module loads, feature degrades if provider absent

Initialization order is sorted by contract dependency graph automatically.

────────────────────────────────────────
CLI COMMANDS
────────────────────────────────────────
Implement ICliCommandProvider. Register in ConfigureServices.
Members: Verbs (IReadOnlyList<string>), HandleAsync(string[], ct) → CliResult

────────────────────────────────────────
ENABLING A NEW MODULE
────────────────────────────────────────
Add to the deployed Runtime Host's Environment/.env:
  Modules__my_module="true"

Runtime (no restart): module enable my_module
Verify: module get my_module  →  status should be "enabled"
If status is "failed", check application log under [Module:my_module] for exception.

────────────────────────────────────────
TROUBLESHOOTING
────────────────────────────────────────
Not in module list         → class not implementing interface or project not compiled
Status: failed             → InitializeAsync threw; check log [Module:{id}]
Tool not reaching handler  → permission check denied; verify ModuleToolPermission
Inline tool produces nothing → ExecuteInlineToolAsync returned NotHandled or threw
Contract not satisfied     → provider module disabled or failed; check module list
SeedDataAsync not running  → .seeded marker exists; delete it to force re-seed
                             check binding Kind matches a declared TriggerKey;
                             check OS permissions for the underlying hook
