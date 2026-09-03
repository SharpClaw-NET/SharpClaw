# SharpClaw Runtime CLI Skill

## Use The CLI

Run the Runtime Host with `--cli` and one command. The current base commands are `help` and `chat`.

```text
SharpClaw.Runtime.Host.exe --cli help
SharpClaw.Runtime.Host.exe --cli chat "Return one short answer."
```

The process loads the Runtime environment, validates the database, loads enabled modules, and starts the kernel before command execution.

CLI mode does not start an HTTP listener or publish Runtime discovery.

## Send Chat

Use `--cli chat <message>`. The CLI joins all message arguments with one space.

```text
SharpClaw.Runtime.Host.exe --cli chat "Summarize the current build status."
```

The command runs `DirectChatKernel` through the configured provider graph. The default installation uses stateless chat without history.

The completion content goes to standard output. Missing text returns exit code `1` and writes an error to standard error.

## Action Boundary

The singleton action dispatcher runs `parse`, `command-select`, `execute`, `output-write`, and `complete`.

Failure uses `fail`. Cancellation uses `cancel`. The execute action passes its cancellation token into direct chat.

Exit code `0` means success. Exit code `1` means failure. Exit code `130` means cancellation.

## Configuration

Use the deployed Runtime `Environment/.env` file with canonical dotenv keys:

```dotenv
Encryption__EncryptDatabase=true
Encryption__EncryptProviderKeys=true
Database__Provider=JsonFile
Modules__sharpclaw_providers_openai_compat=true
```

The active environment document uses the Supprocom.Secrets installation-key boundary. `SHARPCLAW_ENCRYPTION_KEY` is a valid installation-key source when configured.

## Limits

The base CLI does not provide authentication, user sessions, feature-domain commands, database migration commands, or environment editing commands.

Use a published module contract for optional behavior. Do not add a local command fallback or a second dispatcher.
