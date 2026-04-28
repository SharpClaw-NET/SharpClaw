<#
.SYNOPSIS
    Runs the public Computer Report demo end-to-end.

.DESCRIPTION
    This is a beginner-friendly wrapper around the SharpClaw REPL. It will:

      1. Build the SharpClaw API project (skip with -SkipBuild if already built).
      2. Log in to the local REPL using the admin account.
      3. Create or reuse a context called "Computer Report Demo".
      4. Register or update the two task definitions in this folder
         (DesktopReporterSetupTask.cs and DesktopReportTask.cs).
      5. Run the setup task — which creates an agent, role, and channel
         dedicated to the demo.
      6. Run the report task — which uses the Computer Use module to
         enumerate windows, read the clipboard, and capture the primary
         display, then asks the model to produce a Markdown report.
      7. Save the resulting report as a .md file you can open in any
         editor.

    You do NOT need to know the SharpClaw CLI to use this script. Just
    follow the prerequisites in README.md and run it.

.PARAMETER AdminUser
    Local REPL admin username. Default: admin.
    Change only if you customised the local admin account in your .env.

.PARAMETER AdminPass
    Local REPL admin password. Default: 123456.
    Change only if you customised the local admin password in your .env.

.PARAMETER OutputPath
    Where to save the final Markdown report. Default:
    %USERPROFILE%\Desktop\computer-report.md.

.PARAMETER ModelName
    Name of the model to use. Default: claude-sonnet-4.6.
    The model must already be synced under one of your providers — see
    README.md "Prerequisites" section.

.PARAMETER SkipBuild
    Do not rebuild the API project. Use this on repeat runs after the
    first successful build to save ~15 seconds.

.PARAMETER StartupSeconds
    Maximum seconds to allow each REPL session to run. Default: 600.
    The report run typically finishes in 30-90 seconds; the default is
    generous to absorb cold-start latency on slow machines.

.EXAMPLE
    # Simplest invocation. Builds, then runs the demo.
    .\Run-ComputerReport.ps1

.EXAMPLE
    # Skip the rebuild and write the report to a custom path.
    .\Run-ComputerReport.ps1 -SkipBuild -OutputPath C:\reports\my-pc.md

.EXAMPLE
    # Use a different model (must already be synced).
    .\Run-ComputerReport.ps1 -ModelName gpt-5
#>

param(
    [string] $AdminUser      = "admin",
    [string] $AdminPass      = "123456",
    [string] $OutputPath     = (Join-Path $env:USERPROFILE "Desktop\computer-report.md"),
    [string] $ModelName      = "claude-sonnet-4.6",
    [switch] $SkipBuild,
    [int]    $StartupSeconds = 600
)

$ErrorActionPreference = "Stop"

# ────────────────────────────────────────────────────────────────────
# Locate the repository root from this script's location.
# ────────────────────────────────────────────────────────────────────
$scriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot    = Resolve-Path (Join-Path $scriptDir "..\..\..")
$apiProject  = Join-Path $repoRoot "SharpClaw.Application.API"
$setupSource = Join-Path $scriptDir "DesktopReporterSetupTask.cs"
$reportSrc   = Join-Path $scriptDir "DesktopReportTask.cs"
$tempCmds    = Join-Path $env:TEMP "sc_computer_report_cmds.txt"
$tempOut     = Join-Path $env:TEMP "sc_computer_report_out.txt"
$guidPattern = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}'

Write-Host "Repository root : $repoRoot"
Write-Host "Setup task file : $setupSource"
Write-Host "Report task file: $reportSrc"
Write-Host "Output report   : $OutputPath"
Write-Host ""

if (-not (Test-Path $setupSource)) { Write-Error "Setup task file not found: $setupSource" }
if (-not (Test-Path $reportSrc))   { Write-Error "Report task file not found: $reportSrc" }

# ────────────────────────────────────────────────────────────────────
# Helper: run a list of REPL commands inside a single `dotnet run`
# session, capturing stdout+stderr. We pipe commands via stdin and
# wait up to $StartupSeconds for the session to finish on its own
# (every command sequence ends with `exit`).
# ────────────────────────────────────────────────────────────────────
function Invoke-Repl([string[]] $commands, [int] $seconds = $StartupSeconds) {
    Set-Content -Path $tempCmds -Value $commands -Encoding utf8

    $job = Start-Job {
        param($proj, $cmds, $noBuild)
        Set-Location $proj
        $env:ASPNETCORE_ENVIRONMENT = "Development"
        $env:SHARPCLAW_FORCE_REPL   = "1"
        if ($noBuild) {
            Get-Content $cmds | dotnet run -c Debug --no-build 2>&1
        } else {
            Get-Content $cmds | dotnet run -c Debug 2>&1
        }
    } -ArgumentList $apiProject, $tempCmds, $SkipBuild.IsPresent

    Wait-Job $job -Timeout $seconds | Out-Null
    $output = Receive-Job $job
    Get-Job | Stop-Job
    Get-Job | Remove-Job
    if ($null -eq $output) { return "" }
    $text = $output -join "`n"
    $text | Out-File $tempOut -Encoding utf8
    return $text
}

function Get-FirstGuidAfter([string] $text, [string] $anchorRegex) {
    # Find the "Id" that lives in the SAME object scope as the anchor (Name,
    # CustomId, etc.). Walking back from the anchor, accept the first "Id"
    # whose intervening text has balanced {} and []. This avoids picking up
    # an Id from a nested object (e.g. AllowedAgents[0].Id) or from an
    # unrelated preceding object earlier in the session output.
    $marker = [regex]::Match($text, $anchorRegex)
    if (-not $marker.Success) { return $null }

    $head = $text.Substring(0, $marker.Index)
    $idMatches = [regex]::Matches($head, '"Id":\s*"(' + $guidPattern + ')"')
    for ($i = $idMatches.Count - 1; $i -ge 0; $i--) {
        $m = $idMatches[$i]
        $between = $text.Substring($m.Index + $m.Length, $marker.Index - ($m.Index + $m.Length))
        $brace = 0; $bracket = 0
        foreach ($c in $between.ToCharArray()) {
            switch ($c) {
                '{' { $brace++ }
                '}' { $brace-- }
                '[' { $bracket++ }
                ']' { $bracket-- }
            }
            if ($brace -lt 0 -or $bracket -lt 0) { break }
        }
        if ($brace -eq 0 -and $bracket -eq 0) { return $m.Groups[1].Value }
    }
    return $null
}

function Get-TaskId([string] $output, [string] $taskName) {
    return Get-FirstGuidAfter $output ('"Name":\s*"' + [regex]::Escape($taskName) + '"')
}

function Get-ChannelIdByCustomId([string] $output, [string] $customId) {
    # The channel JSON often has nested AllowedAgents with their own Id+CustomId.
    # Walk back from the channel's CustomId, but only accept an Id whose
    # surrounding {}/[] are balanced, i.e. it lives in the same object scope.
    $marker = [regex]::Match($output, '"CustomId":\s*"' + [regex]::Escape($customId) + '"')
    if (-not $marker.Success) { return $null }

    $head = $output.Substring(0, $marker.Index)
    $idMatches = [regex]::Matches($head, '"Id":\s*"(' + $guidPattern + ')"')
    for ($i = $idMatches.Count - 1; $i -ge 0; $i--) {
        $m = $idMatches[$i]
        $between = $output.Substring($m.Index + $m.Length, $marker.Index - ($m.Index + $m.Length))
        $brace = 0; $bracket = 0
        foreach ($c in $between.ToCharArray()) {
            switch ($c) {
                '{' { $brace++ }
                '}' { $brace-- }
                '[' { $bracket++ }
                ']' { $bracket-- }
            }
            if ($brace -lt 0 -or $bracket -lt 0) { break }
        }
        if ($brace -eq 0 -and $bracket -eq 0) { return $m.Groups[1].Value }
    }
    return $null
}

# ════════════════════════════════════════════════════════════════════
# Step 1 — Build (unless -SkipBuild)
# ════════════════════════════════════════════════════════════════════
if (-not $SkipBuild) {
    Write-Host "==> Building SharpClaw.Application.API (Debug)..." -ForegroundColor Cyan
    dotnet build -c Debug (Join-Path $apiProject "SharpClaw.Application.API.csproj") | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Error "Build failed. Fix compile errors and re-run, or pass -SkipBuild if you have an older working build." }
} else {
    Write-Host "==> Skipping build (per -SkipBuild)." -ForegroundColor Yellow
}

# ════════════════════════════════════════════════════════════════════
# Step 2 — Find or create the demo context
# ════════════════════════════════════════════════════════════════════
Write-Host "==> Locating or creating the 'Computer Report Demo' context..." -ForegroundColor Cyan

$ctxOut = Invoke-Repl @(
    "login $AdminUser $AdminPass",
    "context list",
    "exit"
) 90

$ctxId = Get-FirstGuidAfter $ctxOut '"Name":\s*"Computer Report Demo"'

if ([string]::IsNullOrEmpty($ctxId)) {
    Write-Host "  No existing context found — creating a fresh one." -ForegroundColor Yellow

    # `context add` expects an agent ID for new contexts. We pick the first
    # agent that already exists in the system (the seeded admin agent or
    # whatever the user has). If nothing exists, we explain the failure.
    $agentListOut = Invoke-Repl @(
        "login $AdminUser $AdminPass",
        "agent list",
        "exit"
    ) 90
    $agentMatches = [regex]::Matches($agentListOut, '"Id":\s*"(' + $guidPattern + ')"')
    if ($agentMatches.Count -eq 0) {
        Write-Error "No agents are available. Open SharpClaw.Uno once and run through the first-time setup so a default agent gets created, then re-run this script."
    }
    $seedAgentId = $agentMatches[0].Groups[1].Value

    $ctxAddOut = Invoke-Repl @(
        "login $AdminUser $AdminPass",
        "context add $seedAgentId Computer Report Demo",
        "exit"
    ) 90
    $ctxId = Get-FirstGuidAfter $ctxAddOut '"Name":\s*"Computer Report Demo"'
    if ([string]::IsNullOrEmpty($ctxId)) {
        Write-Host $ctxAddOut
        Write-Error "Failed to create the demo context. See output above."
    }
}
Write-Host "  Context ID: $ctxId"

# ════════════════════════════════════════════════════════════════════
# Step 3 — Register or update the two task definitions
# ════════════════════════════════════════════════════════════════════
Write-Host "==> Registering or updating task definitions..." -ForegroundColor Cyan

$listOut      = Invoke-Repl @("login $AdminUser $AdminPass", "task list", "exit") 90
$setupTaskId  = Get-TaskId $listOut "desktop-reporter-setup"
$reportTaskId = Get-TaskId $listOut "desktop-report"

$cmds = [System.Collections.Generic.List[string]]::new()
$cmds.Add("login $AdminUser $AdminPass")
if ([string]::IsNullOrEmpty($setupTaskId))  { $cmds.Add("task create $setupSource") }
else                                         { $cmds.Add("task update $setupTaskId $setupSource") }
if ([string]::IsNullOrEmpty($reportTaskId)) { $cmds.Add("task create $reportSrc") }
else                                         { $cmds.Add("task update $reportTaskId $reportSrc") }
$cmds.Add("task list")
$cmds.Add("exit")

$regOut       = Invoke-Repl $cmds.ToArray() 120
$setupTaskId  = Get-TaskId $regOut "desktop-reporter-setup"
$reportTaskId = Get-TaskId $regOut "desktop-report"

if ([string]::IsNullOrEmpty($setupTaskId) -or [string]::IsNullOrEmpty($reportTaskId)) {
    Write-Host $regOut
    Write-Error "One or both task definitions failed to register. See output above."
}
Write-Host "  desktop-reporter-setup : $setupTaskId"
Write-Host "  desktop-report         : $reportTaskId"

# ════════════════════════════════════════════════════════════════════
# Step 4 — Run the setup task (creates agent, role, channel)
# ════════════════════════════════════════════════════════════════════
Write-Host "==> Running setup task to create the Desktop Reporter agent/channel..." -ForegroundColor Cyan

# create-queued + start-instance + listen pattern keeps the host alive
# until the orchestrator emits Done. Using `task start` and exiting
# immediately would shut the host down mid-run.
$queueOut = Invoke-Repl @(
    "login $AdminUser $AdminPass",
    "task create-queued $setupTaskId --context $ctxId",
    "exit"
) 90

$setupInstance = Get-FirstGuidAfter $queueOut '"Status":\s*"Queued"'
if ([string]::IsNullOrEmpty($setupInstance)) {
    Write-Host $queueOut
    Write-Error "Could not queue a setup instance. See output above."
}

$setupOut = Invoke-Repl @(
    "login $AdminUser $AdminPass",
    "task start-instance $setupInstance",
    "task listen $setupInstance",
    "task instance $setupInstance",
    "channel list",
    "exit"
) 240

if ($setupOut -notmatch '"Status":\s*"Completed"') {
    Write-Host $setupOut
    Write-Error "Setup task did not complete. See output above and check that the model '$ModelName' is synced and reachable."
}

$desktopChannel = Get-ChannelIdByCustomId $setupOut 'demo.desktop-reporter.channel'
if ([string]::IsNullOrEmpty($desktopChannel)) {
    Write-Host $setupOut
    Write-Error "Could not resolve the Desktop Reporter channel ID after setup."
}
Write-Host "  Desktop Reporter channel: $desktopChannel"

# ════════════════════════════════════════════════════════════════════
# Step 5 — Run the report task and capture the Markdown output
# ════════════════════════════════════════════════════════════════════
Write-Host "==> Running the desktop-report task. This can take 30-120 seconds..." -ForegroundColor Cyan

$rptQueue = Invoke-Repl @(
    "login $AdminUser $AdminPass",
    "task create-queued $reportTaskId $desktopChannel",
    "exit"
) 90

$rptInstance = Get-FirstGuidAfter $rptQueue '"Status":\s*"Queued"'
if ([string]::IsNullOrEmpty($rptInstance)) {
    Write-Host $rptQueue
    Write-Error "Could not queue a report instance."
}

$rptOut = Invoke-Repl @(
    "login $AdminUser $AdminPass",
    "task start-instance $rptInstance",
    "task listen $rptInstance",
    "task outputs $rptInstance",
    "task instance $rptInstance",
    "exit"
) $StartupSeconds

if ($rptOut -notmatch '"Status":\s*"Completed"') {
    Write-Host $rptOut
    Write-Error "The report task did not complete. See output above. Tip: enable Computer Use module, ensure model '$ModelName' is synced, and confirm at least one provider is authenticated."
}

# ────────────────────────────────────────────────────────────────────
# Extract the emitted Markdown report.
#
# `task listen` streams the Output entries straight to stdout in plain
# text, so the Markdown appears verbatim between the first
# "# Desktop Report" line and the "Task stream ended." footer. The
# subsequent "task outputs"/"task instance" JSON dumps repeat the same
# content but JSON-escaped — we use the streamed copy for simplicity.
# ────────────────────────────────────────────────────────────────────
$report = $null

$startMatch = [regex]::Match($rptOut, '(?m)^# Desktop Report\s*$')
if ($startMatch.Success) {
    $tail = $rptOut.Substring($startMatch.Index)
    $endMatch = [regex]::Match($tail, 'Task stream ended\.|sharpclaw\s*\(admin\)>')
    if ($endMatch.Success) {
        $report = $tail.Substring(0, $endMatch.Index)
    } else {
        $report = $tail
    }

    # Trim the streaming footer that the orchestrator appends after Emit:
    # the trailing "demo run completed." Log line and the "[status] X" tag.
    $report = [regex]::Replace($report, '\s*Desktop report demo run completed\.\s*$', '', 'Multiline')
    $report = [regex]::Replace($report, '(?m)^\s*\[status\][^\r\n]*\r?\n?', '')
    $report = [regex]::Replace($report, '\s*demo run completed\.\s*\Z', '')
    $report = $report.TrimEnd()
}

if ([string]::IsNullOrEmpty($report)) {
    Write-Host $rptOut
    Write-Error "The report finished but no Markdown content could be extracted from the session output. The full session log is at: $tempOut"
}

$outDir = Split-Path -Parent $OutputPath
if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
Set-Content -Path $OutputPath -Value $report -Encoding utf8

Write-Host ""
Write-Host "==> Done. Report written to:" -ForegroundColor Green
Write-Host "    $OutputPath"
Write-Host ""
Write-Host "Open it in any Markdown-aware editor (VS Code, Typora, Obsidian) or"
Write-Host "preview it on GitHub. The full session log is at:"
Write-Host "    $tempOut"
