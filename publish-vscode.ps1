<#
.SYNOPSIS
    Repo-root shortcut for the VS Code extension publish script.

.DESCRIPTION
    Forwards all arguments to sharpclaw-vscode\publish.ps1
    so you can run it from the repo root without remembering the path.

.EXAMPLE
    .\publish-vscode.ps1                           # Full: clean → build → package → install → launch
    .\publish-vscode.ps1 -DevHost                  # Extension Development Host (like F5)
    .\publish-vscode.ps1 -SkipLaunch               # Build + install only, don't launch
    .\publish-vscode.ps1 -CleanModules             # Fresh node_modules + full pipeline
    .\publish-vscode.ps1 -DevHost -WorkspacePath C:\myproject
#>

$script = Join-Path $PSScriptRoot "sharpclaw-vscode\publish.ps1"

if (-not (Test-Path $script)) {
    Write-Host "ERROR: publish.ps1 not found at: $script" -ForegroundColor Red
    exit 1
}

& $script @args
exit $LASTEXITCODE
