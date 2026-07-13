# Test Harness Modules

The Test Harness modules are SharpClaw test-infrastructure modules with the ids
`sharpclaw_test_harness_out_of_process` and
`sharpclaw_test_harness_in_process`. They deliberately remain in the main
SharpClaw repository as top-level fixture projects so host, module, provider,
gateway, and task tests can exercise deterministic modules without reaching
into an external module repository. They are not production features and should
not be enabled in production templates. The production environment template
keeps both disabled. The development template enables the out-of-process
harness so local and CI test runs can exercise SharpClaw without real provider
API keys, network calls, or REPL scripts; the in-process harness stays disabled
unless a focused test or developer run explicitly opts into in-process .NET
module hosting.

The module registers deterministic provider plugins under provider keys such
as `sharpclaw-test`, `sharpclaw-test-stream`, `sharpclaw-test-tools`, and
`sharpclaw-test-cost`. A test can create a provider row with one of those
provider keys, attach a model named `test-harness-model`, and then send a
normal chat request through `ChatService`. SharpClaw still resolves the model,
builds headers, chooses the provider, validates completion parameters, sends
tool definitions, records token usage, and stores messages exactly as it would
for a real provider. The only difference is that the provider response comes
from an in-memory scenario configured through the harness module's control
tool. Tests should call that control tool through the normal module runtime
surface, not by compiling against the harness implementation classes.

To add a new provider case, configure the harness through
`test_harness_control` before the chat call. For example, a test can set the
`sharpclaw-test-tools` scenario to return a first turn with a `ChatToolCall`
for `test_harness_inline_permissioned`, then a second turn with final assistant
text. That tests the complete native tool loop: SharpClaw sends tool schemas
to the provider, receives the tool call, evaluates the module permission
descriptor, executes or denies the tool through the host pipeline, sends a
tool-result message back to the provider, and persists the final assistant
response. Because the scenario is a list of turns, multi-round tool
conversations stay deterministic and readable.

Streaming tests use the same shape. A turn can specify streaming chunks, a
first-token delay, a per-chunk delay, and a completion delay. If the turn is
configured with three chunks, a 40 ms first-token delay, 30 ms per chunk after
the first chunk, and a 20 ms completion delay, the provider's configured time
is 120 ms. A test that allows 500 ms of SharpClaw overhead must finish within
620 ms. A measured 621 ms is a failure. The provider records its own elapsed
time separately from the total chat or SSE time, so a failed budget points at
provider delay or SharpClaw overhead instead of mixing both together.

The module also registers deterministic tools. `test_harness_inline_open` is
available without a permission descriptor, while
`test_harness_inline_permissioned`, `test_harness_job_permissioned`, and
`test_harness_job_streaming` require the module-owned global flag
`CanUseTestHarnessTools`. A test can leave that flag off the agent role and
prove that the host denies the tool without calling the module implementation.
The same test can grant the flag with `Independent` clearance and prove that
the tool executes, records a trace, returns a configured payload, fails on
request, or simulates latency. This is how tests verify that a mistaken or
malicious module tool cannot bypass host permission enforcement.

The header tag `{{testharness}}` is intentionally simple. It returns a
configured string, records the full `ModuleHeaderTagContext`, and can simulate
latency or failure. A concrete chat regression test can set an agent custom
header to `prefix {{testharness}}`, send a normal chat message, and assert that
the provider captured `prefix tag-value` in the user message. The same test can
turn on `Chat:DisableHeaderTagExpansion` and assert that the literal
`{{testharness}}` is sent, or turn on `Chat:DisableModuleHeaderTags` and assert
that the module resolver was not called.

Cost tests use the provider plugin's `CostFeed`. The configured cost behavior
can return a daily provider-cost result, return `null` to simulate a permission
denial, or sleep for a known number of milliseconds. This lets tests keep
third-party billing behavior in module-owned code while still proving that the
core provider-cost service calls the plugin surface instead of hardcoding
provider-specific cost logic.

The in-memory capture is sanitized before tests read it. API keys, bearer
tokens, obvious secret fields, and provider parameters with names like
`api_key`, `secret`, `token`, or `password` are redacted. Prompt text, system
prompt text, tool names, tool schemas, provider parameters, model names, and
completion parameters remain assertable. That balance lets regression tests
verify exactly what SharpClaw sent without leaking credentials into test output
or logs.

New regression scenarios should be added by extending the data that feeds the
existing fixtures, not by copying whole tests. For example, a new caching case
should add another cache budget or lookup sequence to the fixture. A new
provider failure case should add a provider turn with `ThrowMalformedPayload`,
`FailuresBeforeSuccess`, or `StreamFailureAfterChunks`. A new permission case
should choose the harness tool, the role flag, and the clearance level, then
assert the host's decision and the harness trace. The module is deliberately
small so that the suite can grow to hundreds or thousands of cases while each
failure still points at one provider turn, one tool behavior, or one latency
budget.
