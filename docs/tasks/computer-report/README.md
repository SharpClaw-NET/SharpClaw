# Computer Report Demo

This folder contains a public, beginner-friendly demo for SharpClaw. It
shows what a SharpClaw agent can actually *do* on your computer when you
give it the Computer Use module: enumerate your open windows, peek at
your clipboard, take a screenshot, and write a tidy Markdown report
about your machine.

You do not need to know anything about SharpClaw's internals to run it.
Follow the steps below in order. Every prerequisite is listed
explicitly, even the obvious ones.

---

## What you get

When the demo finishes you will have a Markdown file on your Desktop
called `computer-report.md` that looks roughly like this:

```markdown
# Desktop Report

## Summary
- Computer identity: Windows 11 desktop, hostname unknown
- OS: Windows 11 (build inferred from process paths)
- Timezone and local time: UTC offset visible in window timestamps
- Primary user/session clues: signed in as `marko`, three messaging
  apps active

## Displays
| Display          | Resolution | Notes                       |
|------------------|-----------:|------------------------------|
| Primary Display  | 1920x1080  | Captured via cu_capture_display |

## Open Windows
| Process       | Title                       | PID  | Evidence            |
|---------------|-----------------------------|-----:|---------------------|
| Code.exe      | Run-ComputerReport.ps1 - …  | 1234 | enumerate_windows   |
| Discord.exe   | #general - SharpClaw         | 5678 | enumerate_windows   |
…
```

The demo also creates an agent (`Desktop Reporter`), a role
(`Desktop Reporter Demo Role`) with the right Computer Use permissions,
and a channel where the agent lives. If you re-run the demo those
resources are reused, not duplicated.

---

## Prerequisites

You must have all of the following before you start. Skipping any of
these will cause the script to fail with a clear error message — but
it's faster to set them up correctly the first time.

### 1. Operating system

  * Windows 10 or Windows 11. The Computer Use module is Windows-only
    today; macOS and Linux are not supported by this demo.

### 2. .NET 10 SDK

  * Install the .NET 10 SDK from
    <https://dotnet.microsoft.com/download/dotnet/10.0>.
  * After installing, open a fresh PowerShell window and run
    `dotnet --version`. You should see `10.x.y` in the output.

### 3. PowerShell

  * Use Windows PowerShell 5.1 (the default `powershell.exe`) or
    PowerShell 7+ (`pwsh`). Either works.

### 4. The SharpClaw repository, cloned locally

  * Clone with Git:
    ```powershell
    git clone https://github.com/mkn8rn/SharpClaw.git
    cd SharpClaw
    ```

### 5. SharpClaw must have been started at least once

  Before the script can use SharpClaw you must run the desktop client
  (`SharpClaw.Uno`) once, so the first-time setup wizard creates an
  admin user, an initial agent, and the on-disk database.

  1. Open `SharpClaw.sln` in Visual Studio 2026, or run from the
     command line:
     ```powershell
     dotnet run --project SharpClaw.Uno -c Debug
     ```
  2. Walk through the **First-Time Setup** screen. Set the admin
     username and password (the defaults `admin` / `123456` are
     fine for a private machine — the script uses those by default).
  3. After setup, log in once. This guarantees the seeded data is
     written to disk.
  4. Close SharpClaw.

### 6. At least one AI provider authenticated

  The report agent needs a model. The default model is
  `claude-sonnet-4.6`, which is available through GitHub Copilot.

  * In SharpClaw.Uno, go to **Settings → Providers**, pick a
    provider (GitHub Copilot is easiest — it uses your existing
    GitHub account), and complete the authentication flow.
  * Then click **Sync Models** so SharpClaw learns which models that
    provider exposes.
  * Confirm that the model name you intend to use shows up in the
    model list. If you don't see `claude-sonnet-4.6`, pass a
    different one with `-ModelName` (see Examples below).

### 7. The Computer Use module must be enabled

  * In SharpClaw.Uno, go to **Settings → Modules**. Find
    **sharpclaw_computer_use** and make sure it is **Enabled**.
  * The module is enabled by default in fresh installs, but a careful
    user may have turned it off.

You only need to do steps 1–7 **once**. After that, every demo run is
just one command.

---

## Running the demo

From the repository root, in PowerShell:

```powershell
cd docs\tasks\computer-report
.\Run-ComputerReport.ps1
```

That's it. The script will:

  1. Build the SharpClaw API project (15–60 seconds the first time).
  2. Log in to the local REPL with the admin account.
  3. Find or create a context called *Computer Report Demo*.
  4. Register the two task definitions in this folder
     (`DesktopReporterSetupTask.cs` and `DesktopReportTask.cs`).
  5. Run the setup task, which creates the demo agent, role, and
     channel (or reuses them if they already exist).
  6. Run the report task, which uses the Computer Use module to
     gather evidence and asks the model to write a Markdown report.
  7. Save the report to `%USERPROFILE%\Desktop\computer-report.md`.

A typical first run takes 90–180 seconds. Repeat runs (with
`-SkipBuild`) take 60–120 seconds.

---

## Examples

### Default run

```powershell
.\Run-ComputerReport.ps1
```

### Skip the build (after a successful first run)

```powershell
.\Run-ComputerReport.ps1 -SkipBuild
```

### Save the report somewhere else

```powershell
.\Run-ComputerReport.ps1 -OutputPath C:\reports\my-pc.md
```

### Use a different model

The model name must already be visible in **Settings → Models** in
SharpClaw.Uno. For example, to use OpenAI's GPT-5 (also reachable
through GitHub Copilot):

```powershell
.\Run-ComputerReport.ps1 -ModelName gpt-5
```

### Use a non-default admin account

If you changed the admin credentials during first-time setup:

```powershell
.\Run-ComputerReport.ps1 -AdminUser alice -AdminPass "S3cret!"
```

### Combine flags

```powershell
.\Run-ComputerReport.ps1 -SkipBuild -ModelName gpt-5 -OutputPath C:\reports\my-pc.md
```

---

## What each file in this folder does

  * **DesktopReporterSetupTask.cs**
    Defines the *one-time* setup task. It looks up the configured
    model, creates a role with safe Computer Use permissions, creates
    the **Desktop Reporter** agent, attaches the role, and creates a
    dedicated channel. Re-running it is idempotent: it reuses
    resources whose custom IDs (`demo.desktop-reporter.agent`,
    `demo.desktop-reporter.channel`) already exist.

  * **DesktopReportTask.cs**
    The actual report task. It opens a thread on the Desktop Reporter
    channel and asks the agent to:
      * call `cu_enumerate_windows` once,
      * call `cu_read_clipboard` once,
      * call `cu_capture_display` on exactly one display, and
      * optionally call `cu_capture_window` on at most one window.
    It then asks the model to render a strict Markdown report using
    those tool results as evidence, and emits the result so the
    wrapper script can save it to disk.

  * **Run-ComputerReport.ps1**
    The PowerShell wrapper described in this README. Handles build,
    login, registration, execution, and Markdown extraction so you
    don't have to drive the SharpClaw CLI by hand.

  * **README.md**
    This file.

---

## Troubleshooting

**"Build failed"** — Open `SharpClaw.sln` in Visual Studio 2026 and
run **Build → Rebuild Solution**. Fix any compile errors before
re-running. If the solution builds in Visual Studio but
`Run-ComputerReport.ps1` still fails, run with `-SkipBuild` and
check that the API project's `bin\Debug\net10.0\` folder contains a
recent `SharpClaw.Application.API.dll`.

**"No agents are available"** — You skipped step 5 of Prerequisites.
Open SharpClaw.Uno, complete first-time setup, then re-run the
script.

**"Setup task did not complete"** — Almost always a model issue.
Check that:
  * The model name (default `claude-sonnet-4.6`) appears in the
    SharpClaw.Uno **Models** list. If not, pass a different name
    with `-ModelName`.
  * The provider for that model is authenticated (Settings →
    Providers shows a green/active state).
  * Your network can reach the provider — try a simple chat in
    SharpClaw.Uno first.

**"The report task did not complete"** — Confirm the
**sharpclaw_computer_use** module is enabled (Prerequisite 7) and
that the model you chose supports tool calling. `claude-sonnet-4.6`,
`gpt-5`, and `gpt-5-mini` are all known to work via GitHub Copilot.

**"prompt token count of … exceeds the limit of 128000"** — Some
model+provider combinations have small context windows. The default
prompt is tuned to keep below 128k tokens by capturing only one
display. If you still hit this limit, try a model with a larger
context window (e.g. `gpt-5`).

**Where are the logs?** — The full REPL session output for the most
recent run is saved at `%TEMP%\sc_computer_report_out.txt`. Open it
in any text editor; the failure (if any) is at the bottom.

---

## Privacy and safety

The Computer Use module reads real data from your machine: window
titles, the clipboard contents, and screen pixels of the primary
display. The model receives that data and the resulting Markdown
report contains a redacted summary.

  * **Run the demo only on a machine where you are comfortable with
    the configured AI provider receiving that data.**
  * The prompt explicitly tells the model not to transcribe secrets
    verbatim, but treat the output as if a thoughtful coworker had
    looked over your shoulder for two minutes.
  * No mouse clicks, keystrokes, window resizes, or app launches are
    performed by this demo. It is observation-only.

---

## Going further

  * Edit `DesktopReportTask.cs` to change the prompt — for example to
    add a section about installed VS Code extensions, or to drop the
    screenshot entirely if you only want a text inventory.
  * Add the Desktop Reporter channel to the SharpClaw.Uno sidebar to
    chat with the agent interactively after the demo seeds it.
  * Use the same setup task as a template for your own observation-
    style agents: copy `DesktopReporterSetupTask.cs`, change the
    custom IDs and permission set, and you have a freshly-scoped
    agent in seconds.
