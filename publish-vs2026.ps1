<#
.SYNOPSIS
    Repo-root shortcut for the VS 2026 extension install-test script.

.DESCRIPTION
    Forwards all arguments to sharpclaw-vs2026\.vsextension\install-test.ps1
    so you can run it from the repo root without remembering the path.

.EXAMPLE
    .\publish-vs2026.ps1                        # Build Release, deploy to Exp, launch
    .\publish-vs2026.ps1 -Configuration Debug   # Build Debug
    .\publish-vs2026.ps1 -NoLaunch              # Deploy without launching VS
    .\publish-vs2026.ps1 -MainInstance           # Deploy to the main VS instance
    .\publish-vs2026.ps1 -Reset                  # Full clean + redeploy
    .\publish-vs2026.ps1 -Uninstall              # Remove the extension
#>

$script = Join-Path $PSScriptRoot "sharpclaw-vs2026\.vsextension\install-test.ps1"

if (-not (Test-Path $script)) {
    Write-Host "ERROR: install-test.ps1 not found at: $script" -ForegroundColor Red
    exit 1
}

& $script @args
exit $LASTEXITCODE
