SharpClaw Module: Module Development Kit — Agent Skill Reference

Module ID: sharpclaw_module_dev
Display Name: Module Development Kit
Tool Prefix: mdk
Version: 1.0.0
Platforms: All (process inspection is Windows-enhanced)
Exports: none
Requires: window_management (optional)

────────────────────────────────────────
OVERVIEW
────────────────────────────────────────
Autonomous module authoring, building, hot-loading, testing, and
iteration. Also provides development environment introspection: live
process inspection, PE exports, COM type libraries, SDK/runtime info.

All workspace I/O is sandboxed to external-modules/{module_id}/.
Only .cs and .json files may be written. Path traversal, absolute paths,
reserved device names, and non-allowlisted extensions are rejected.

Tools are dispatched via the module system (AgentActionType = ModuleAction).
Tool names are prefixed with "mdk_" when sent to the model.

────────────────────────────────────────
TOOLS (11)
────────────────────────────────────────

mdk_scaffold_module
  Generate a full module project skeleton from a specification.
  Creates .csproj, module class, module.json, and optional tool stubs.
  Params: module_id (string, required), display_name (string, required),
          tool_prefix (string, required), description (string, optional),
          tools (array of {name, description?, parameters_hint?}, optional)
  Permission: global (CanScaffoldModules)

mdk_write_file
  Write or overwrite a file in a module workspace.
  Only .cs and .json extensions allowed.
  Params: module_id (string, required), relative_path (string, required),
          content (string, required)
  Permission: global (CanWriteModuleFiles)

mdk_read_file
  Read a file from a module workspace with optional truncation.
  Params: module_id (string, required), relative_path (string, required),
          max_lines (int, optional, default 500)
  Permission: global (CanWriteModuleFiles)

mdk_list_files
  List the file tree of a module workspace with optional glob filter.
  Params: module_id (string, required),
          include_pattern (string, optional, default **/* )
  Permission: global (CanWriteModuleFiles)

mdk_build_module
  Compile a module via dotnet build. Returns structured diagnostics.
  Params: module_id (string, required),
          configuration (string, optional, default "Debug")
  Permission: global (CanBuildModules)
  Timeout: 120s

mdk_load_module
  Hot-load a compiled module. Auto-reloads if already loaded.
  Params: module_id (string, required)
  Permission: global (CanLoadModules)

mdk_unload_module
  Unload a module from the running host (drain + ALC unload).
  Params: module_id (string, required)
  Permission: global (CanLoadModules)

mdk_test_tool
  Invoke any tool from any loaded module by name for testing.
  Params: tool_name (string, required), parameters (object, required),
          timeout_seconds (int, optional, default 30)
  Permission: global (CanTestModuleTools)

mdk_inspect_process
  Read-only process introspection: DLLs, PE exports, window classes,
  threads. Windows uses Toolhelp32/PE; Linux uses /proc.
  Params: target (string, required — name, PID, or title),
          include (string[], optional — modules/exports/window_classes/threads),
          export_filter (string, optional — regex)
  Permission: global (CanInspectProcesses)
  Timeout: 60s

mdk_discover_com_interfaces
  Deep-dive COM type library: interfaces, coclasses, methods, params.
  Windows only.
  Params: typelib_path (string, required),
          interface_filter (string, optional — regex),
          include_inherited (bool, optional, default false)
  Permission: global (CanInspectProcesses)
  Timeout: 60s

mdk_enumerate_dev_environment
  Report SDKs, runtimes, global tools, contracts assembly, host version,
  loaded modules, available contracts, external-modules path.
  Params: none
  Permission: global (CanScaffoldModules)

────────────────────────────────────────
INLINE TOOLS (2)
────────────────────────────────────────

mdk_describe_module_system
  Concise reference card of the ISharpClawModule contract: interface,
  tool definitions, permissions, contracts, manifest, hot-loading.
  No parameters.

mdk_list_loaded_modules
  Current loaded modules with IDs, prefixes, tool counts, inline tool
  counts, exported contract names.
  No parameters.

────────────────────────────────────────
CLI
────────────────────────────────────────
mdk scaffold <id> <name> <prefix> [desc]   Scaffold a module
mdk build <id> [--release]                 Build a module
mdk load <id>                              Hot-load a module
mdk unload <id>                            Unload a module
mdk reload <id>                            Reload a module
mdk inspect <process>                      Inspect a process
mdk env                                    Show dev environment
mdk list                                   List module workspaces

Aliases: module-dev

────────────────────────────────────────
ROLE PERMISSIONS
────────────────────────────────────────
Global flags only (no per-resource permissions):
  CanScaffoldModules   — scaffold_module, enumerate_dev_environment
  CanWriteModuleFiles  — write_file, read_file, list_files
  CanBuildModules      — build_module
  CanLoadModules       — load_module, unload_module
  CanTestModuleTools   — test_tool
  CanInspectProcesses  — inspect_process, discover_com_interfaces

────────────────────────────────────────
REQUIRED CONTRACTS
────────────────────────────────────────
window_management → IWindowManager (optional)
  Used for window-title → PID resolution in process inspection.
  Falls back to Process.GetProcessesByName when unavailable.

No contracts are exported.

────────────────────────────────────────
SECURITY MODEL
────────────────────────────────────────
Workspace sandbox:
  - File I/O scoped to external-modules/{module_id}/
  - Module ID regex: ^[a-z][a-z0-9_]{0,39}$
  - Path validation: no traversal, no absolute, no null bytes,
    no reserved Windows device names
  - Canonical path must remain inside the module directory

Extension allowlist:
  - Only .cs and .json may be written
  - Binaries produced exclusively via mdk_build_module (dotnet build)

Build isolation:
  - Compilation runs as a separate process with 120s timeout
  - Host process is never affected by compilation errors

Hot-load isolation:
  - Collectible AssemblyLoadContext per external module
  - Clean unload without host restart

────────────────────────────────────────
TYPICAL WORKFLOW
────────────────────────────────────────
1. mdk_enumerate_dev_environment — check SDKs, contracts version
2. mdk_describe_module_system — review the module contract
3. mdk_scaffold_module — generate project skeleton with tool stubs
4. mdk_write_file (repeat) — implement tool logic in .cs files
5. mdk_build_module — compile and review diagnostics
6. mdk_write_file — fix errors based on diagnostics
7. mdk_build_module — iterate until clean
8. mdk_load_module — hot-load into the running host
9. mdk_test_tool — verify tools work
10. mdk_list_loaded_modules — confirm module is registered
