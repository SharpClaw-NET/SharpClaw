namespace SharpClaw.Modules.ModuleDev.Services;

internal sealed class SharpClawSdkReferenceService
{
    private static readonly IReadOnlyDictionary<string, string> Topics =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["agent_workflow"] = """
                SharpClaw SDK agent workflow.

                An agent can build without an IDE by treating ModuleDev as the
                workbench. Start with mdk_get_sdk_reference for the runtime and
                capability you need. Use mdk_scaffold_module when you want a new
                workspace, or mdk_apply_module_files when you already know the
                file contents and want to write several files in one operation.
                The module workflow writes the files, detects the runtime from
                module.json, builds .NET modules by default, verifies
                JavaScript and Python modules before load, optionally invokes
                test tools, and writes a system-role conversation steering
                message for the next turn. Verification fails before hot-load,
                so the next turn gets syntax, dependency, or declared verify
                diagnostics instead of a vague sidecar startup failure.

                A task follows the same pattern with mdk_apply_task_source. The
                tool validates the raw task source first. If validation fails it
                saves nothing and steers the next turn toward the diagnostics.
                If validation passes it creates a new task or updates the task
                named by task_id, then steers the next turn with the saved task
                id and active status.

                Example module workflow:

                ```json
                {
                  "module_id": "agent_notes",
                  "files": [
                    {
                      "relative_path": "module.py",
                      "content": "from sharpclaw_module_host import create_sharpclaw_host\n"
                    }
                  ],
                  "verify": true,
                  "load": true,
                  "conversation": {
                    "channel_id": "00000000-0000-0000-0000-000000000000",
                    "thread_id": "11111111-1111-1111-1111-111111111111"
                  }
                }
                ```
                """,

            ["dotnet"] = """
                SharpClaw .NET module SDK.

                The .NET SDK surface is SharpClaw.Contracts. A module implements
                ISharpClawCoreModule and returns descriptors for tools, inline
                tools, contracts, resources, flags, header tags, endpoints, and
                CLI commands. Keep module code behind SharpClaw.Contracts and
                SharpClaw.Utils references. Do not reference
                SharpClaw.Application.Core, SharpClaw.Application.Infrastructure,
                or a host DbContext from a module. Host-owned features such as
                task authoring, lifecycle, module storage, and conversation
                steering are injected as Contracts interfaces.

                Minimal tool flow:

                ```csharp
                public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() =>
                [
                    new("echo", "Return text.", EmptySchema(),
                        new ModuleToolPermission(false, null, null))
                ];

                public Task<string> ExecuteToolAsync(
                    string toolName,
                    JsonElement parameters,
                    AgentJobContext job,
                    IServiceProvider sp,
                    CancellationToken ct) =>
                    toolName == "echo"
                        ? Task.FromResult(parameters.GetProperty("text").GetString() ?? "")
                        : throw new NotSupportedException(toolName);
                ```
                """,

            ["javascript"] = """
                SharpClaw JavaScript module SDK.

                JavaScript modules use @sharpclaw/module-host. The SDK starts a
                local HTTP control server, answers the SharpClaw handshake and
                discovery routes, exposes endpoint and tool descriptors, and
                supplies a hostCapabilities client for host-owned operations.
                The module never opens the SharpClaw database directly. Use
                hostCapabilities.invokeStorage for declared document stores,
                hostCapabilities.addConversationSteering for steering, and
                hostCapabilities.invokeModuleTool when an agent workflow needs
                to test another loaded module tool.

                ModuleDev verifies JavaScript modules with node --check against
                the manifest entrypoint before hot-load. If package.json
                declares scripts.sharpclawVerify and run_declared_verify is not
                false, ModuleDev then runs npm run sharpclawVerify. Dependency
                installation is not implicit. Set install_dependencies only
                when the workflow should run npm ci or npm install inside the
                module workspace before verification.

                Minimal module:

                ```javascript
                import { createSharpClawHost, json } from '@sharpclaw/module-host';

                const host = createSharpClawHost({
                  moduleId: 'agent_notes',
                  toolPrefix: 'an',
                  endpoints: [{
                    method: 'GET',
                    routePattern: '/modules/agent_notes/ping',
                    handler: async () => json({ ok: true })
                  }],
                  storageContracts: []
                });

                await host.start();
                ```
                """,

            ["python"] = """
                SharpClaw Python module SDK.

                Python modules use sharpclaw-module-host. The SDK hosts the
                control server, normalizes endpoint and tool descriptors, and
                supplies HostCapabilitiesClient. Use create_document_store for
                host-owned indexed storage, add_conversation_steering for
                next-turn feedback, and invoke_module_tool for test calls across
                the sidecar boundary.

                ModuleDev verifies Python modules with python -m py_compile
                against the manifest entrypoint before hot-load. If pyproject.toml
                contains [tool.sharpclaw] verify-command = "python -m module"
                and run_declared_verify is not false, ModuleDev runs that command
                without a shell. Dependency installation is not implicit. Set
                install_dependencies only when the workflow should create a
                module-local .sharpclaw-venv and install requirements.txt or the
                pyproject package before verification.

                Minimal module:

                ```python
                from sharpclaw_module_host import create_sharpclaw_host, json_response

                async def ping(_context):
                    return json_response({"ok": True})

                host = create_sharpclaw_host(
                    module_id="agent_notes",
                    tool_prefix="an",
                    endpoints=[{
                        "method": "GET",
                        "route_pattern": "/modules/agent_notes/ping",
                        "handler": ping,
                    }],
                    storage_contracts=[],
                )

                if __name__ == "__main__":
                    host.serve()
                ```
                """,

            ["storage"] = """
                SharpClaw module storage SDK.

                Storage is host-owned. Modules declare storage contracts in
                discovery and call the host capability server for get, upsert,
                batchUpsert, delete, batchDelete, list, query, and claim.
                Query and claim operate on declared indexes rather than leaking
                EF Core or LINQ execution into sidecars. Use query builders for
                simple index filters and use claim when a job-like record must
                be atomically selected and patched.

                JavaScript example:

                ```javascript
                const jobs = createDocumentStore(context.hostCapabilities, 'jobs');
                const due = await jobs.claim()
                  .whereIndex('status').equalTo('pending')
                  .whereIndex('nextRunAt').lessThanOrEqual(new Date().toISOString())
                  .patch({ status: 'running' }, { status: 'running' })
                  .take(1)
                  .toListAsync();
                ```
                """,

            ["conversation_steering"] = """
                SharpClaw conversation steering SDK.

                Conversation steering is a host capability that writes a
                persisted system-role chat message into a channel or thread.
                The next model turn sees it through the normal thread history
                path. Use it after build failures, successful hot-loads, test
                results, task validation failures, and any other result that
                should guide the agent's next message. The host validates the
                channel and thread relationship, stores source/category metadata,
                and publishes thread activity when the target is threaded.

                JavaScript:

                ```javascript
                await context.hostCapabilities.addConversationSteering({
                  channelId,
                  threadId,
                  summary: 'Module hot-loaded. Next call mdk_test_tool.',
                  source: 'module_dev',
                  category: 'hot_load'
                });
                ```

                Python:

                ```python
                context.host_capabilities.add_conversation_steering(
                    channel_id,
                    "Build failed with CS1002 in Module.cs.",
                    thread_id=thread_id,
                    source="module_dev",
                    category="module_build",
                )
                ```
                """,

            ["tasks"] = """
                SharpClaw task SDK.

                Task source is authored as raw C# text and stored through the
                host ITaskAuthoring contract. Validation parses the source and
                returns diagnostics without saving. Creation and update validate
                before persistence and resynchronize trigger bindings when the
                source changes. ModuleDev exposes this as mdk_validate_task,
                mdk_create_task, mdk_update_task, and the higher-level
                mdk_apply_task_source workflow.

                Example workflow input:

                ```json
                {
                  "source_text": "task source here",
                  "conversation": {
                    "channel_id": "00000000-0000-0000-0000-000000000000",
                    "thread_id": "11111111-1111-1111-1111-111111111111"
                  }
                }
                ```
                """,

            ["manifest"] = """
                SharpClaw module manifest SDK.

                Each external module workspace has module.json. DotNet modules
                use runtime dotnet, entryAssembly, and moduleType. JavaScript
                modules use runtime node and entrypoint module.mjs. Python
                modules use runtime python and entrypoint module.py. The host
                uses id, displayName, version, toolPrefix, enabled,
                hostMode, exports, and requires during discovery and load.

                Keep id and toolPrefix stable once a module is loaded. Changing
                either value means the host will treat the module as a different
                contribution surface.
                """,
        };

    public IReadOnlyList<string> TopicNames => Topics.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();

    public string GetReference(string? topic)
    {
        if (string.IsNullOrWhiteSpace(topic) || topic.Equals("all", StringComparison.OrdinalIgnoreCase))
            return string.Join(
                $"{Environment.NewLine}{Environment.NewLine}",
                TopicNames.Select(name => Topics[name]));

        return Topics.TryGetValue(topic.Trim(), out var text)
            ? text
            : throw new ArgumentException(
                $"Unknown SDK reference topic '{topic}'. Available topics: {string.Join(", ", TopicNames)}.");
    }
}
