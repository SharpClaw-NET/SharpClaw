# SharpClaw Runtime CLI Reference

## Scope

The Runtime CLI is a process mode for one local command. It uses the same compiled kernel graph as direct Runtime chat.

The current base CLI provides `help` and `chat`. It does not provide user, agent, channel, thread, role, database migration, or environment editing commands.

Optional modules own their command surfaces. The base Runtime CLI does not create a second command path for module domains.

## Start The CLI

Pass `--cli` and one command to the Runtime Host executable:

```text
SharpClaw.Runtime.Host.exe --cli help
SharpClaw.Runtime.Host.exe --cli chat "Return one short status message."
```

The command name is case-insensitive. Arguments after the command become the command argument list.

The Runtime loads configuration and modules, validates database readiness, and starts the kernel before it runs the CLI command.

CLI mode does not start the HTTP listener. It does not publish a Runtime discovery entry.

## Help

Run `--cli help` to print the current command names:

```text
SharpClaw Runtime CLI
  --cli help
  --cli chat <message>
```

The help output is a Runtime result. It does not list routes or feature-module commands.

## Direct Chat

Run `--cli chat` with one or more message arguments:

```text
SharpClaw.Runtime.Host.exe --cli chat "Write a release status in one sentence."
```

The CLI joins the message arguments with one space and submits one `ChatTurnInput` to `DirectChatKernel`.

The kernel selects the configured provider and model through enabled provider modules. The default installation does not load or save conversation history.

The CLI writes the completion content to standard output. A missing message returns a failure result and writes the error to standard error.

## Action Flow

Every CLI command uses the singleton Runtime action dispatcher. The flow is `parse`, `command-select`, `execute`, `output-write`, and `complete`.

A command failure uses `fail` before it writes the stable failure message. An action cancellation uses `cancel` before it writes the cancellation message.

The execute terminal receives the dispatcher cancellation token. The CLI does not continue chat work after the action boundary cancels.

## Exit Codes

Exit code `0` means that the command completed. Exit code `1` means that parsing, execution, output, or another command operation failed.

Exit code `130` means that the action or process was cancelled. The process cancellation token comes from console cancellation or host unload.

General exception details are not printed as command output. The CLI writes `The Runtime CLI command failed.` for an execution failure.

## Configuration

The CLI uses the same deployed Runtime `Environment/.env` file as the local host. Use canonical dotenv keys with `__` separators.

```dotenv
Encryption__EncryptDatabase=true
Encryption__EncryptProviderKeys=true
Database__Provider=JsonFile
Modules__sharpclaw_providers_openai_compat=true
```

The Runtime protects the active environment document through the Supprocom.Secrets installation-key boundary. `SHARPCLAW_ENCRYPTION_KEY` is an installation-key source when it is configured and valid.

Database readiness runs before command execution. The CLI does not apply migrations or switch to another provider when readiness fails.

## Boundaries

The base CLI has no HTTP API-key exchange because it runs inside the local Runtime process. It also has no user session or administrator authorization flow.

Use the owning module contract for optional feature behavior. Do not infer a removed feature command from an older guide.
