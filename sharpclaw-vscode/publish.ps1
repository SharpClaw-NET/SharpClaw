<#
.SYNOPSIS
    Builds, packages, and installs the SharpClaw VS Code extension for testing.

.DESCRIPTION
    Unified publish / test script for the SharpClaw VS Code extension.

      +-----------+-------------------------------------------------------------------+
      | Step      | Action                                                            |
      +-----------+-------------------------------------------------------------------+
      | Clean     | Removes out/, *.vsix, node_modules (optional).                   |
      | Install   | Runs npm install (or npm ci with -CleanModules).                 |
      | Compile   | TypeScript → JavaScript via tsc.                                 |
      | Package   | Creates .vsix via @vscode/vsce.                                  |
      | Deploy    | Uninstalls previous version, installs new .vsix into VS Code.    |
      | Launch    | Opens VS Code (optionally in Extension Development Host mode).   |
      +-----------+-------------------------------------------------------------------+

    By default, performs all steps. Use switches to control behavior.

.PARAMETER DevHost
    Launch VS Code in Extension Development Host mode (like F5 in VS 2026).
    The extension is loaded from source without installing a .vsix.
    Mutually exclusive with -SkipLaunch.

.PARAMETER WorkspacePath
    Path to open in the launched VS Code instance. Default: repo root.

.PARAMETER CleanModules
    Remove node_modules before install (forces npm ci for a fresh install).

.PARAMETER SkipPackage
    Skip .vsix packaging (useful with -DevHost where no .vsix is needed).

.PARAMETER SkipInstall
    Skip uninstalling old / installing new .vsix (useful with -DevHost).

.PARAMETER SkipLaunch
    Skip launching VS Code after build.

.PARAMETER CodeBinary
    Path to the VS Code CLI binary. Default: 'code' (assumes it's on PATH).

.PARAMETER Verbose
    Enables detailed diagnostic output during each step.

.EXAMPLE
    .\publish.ps1                              # Full: clean → build → package → install → launch
    .\publish.ps1 -DevHost                     # Extension Development Host (like F5 in VS 2026)
    .\publish.ps1 -SkipLaunch                  # Build + install only, don't launch
    .\publish.ps1 -CleanModules                # Fresh node_modules + full pipeline
    .\publish.ps1 -DevHost -WorkspacePath C:\myproject
#>
param(
    [switch]$DevHost,
    [string]$WorkspacePath = "",
    [switch]$CleanModules,
    [switch]$SkipPackage,
    [switch]$SkipInstall,
    [switch]$SkipLaunch,
    [string]$CodeBinary = "code",
    [switch]$Verbose
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# ═══════════════════════════════════════════════════════════════════════
#  Logging
# ═══════════════════════════════════════════════════════════════════════

$script:stepIndex = 0
$script:warnings = [System.Collections.Generic.List[string]]::new()
$script:startTime = Get-Date

function Write-Step {
    param([string]$Message)
    $script:stepIndex++
    Write-Host ""
    Write-Host "[$script:stepIndex] $Message" -ForegroundColor Cyan
    Write-Host ("─" * 60) -ForegroundColor DarkGray
}

function Write-Detail {
    param([string]$Message)
    if ($Verbose) {
        Write-Host "    $Message" -ForegroundColor DarkGray
    }
}

function Write-Ok {
    param([string]$Message)
    Write-Host "  ✔ $Message" -ForegroundColor Green
}

function Write-Warn {
    param([string]$Message)
    $script:warnings.Add($Message)
    Write-Host "  ⚠ $Message" -ForegroundColor Yellow
}

function Write-Fail {
    param([string]$Message)
    Write-Host "  ✘ $Message" -ForegroundColor Red
}

function Get-Elapsed {
    return ((Get-Date) - $script:startTime).TotalSeconds
}

# ═══════════════════════════════════════════════════════════════════════
#  Paths
# ═══════════════════════════════════════════════════════════════════════

$repoRoot     = Split-Path $PSScriptRoot -Parent
$extensionDir = $PSScriptRoot
$outDir       = Join-Path $extensionDir "out"
$srcDir       = Join-Path $extensionDir "src"
$packageJson  = Join-Path $extensionDir "package.json"

if (-not $WorkspacePath) {
    $WorkspacePath = $repoRoot
}

# ═══════════════════════════════════════════════════════════════════════
#  Read extension metadata
# ═══════════════════════════════════════════════════════════════════════

if (-not (Test-Path $packageJson)) {
    Write-Fail "package.json not found at: $packageJson"
    exit 1
}

$pkgData = Get-Content $packageJson -Raw | ConvertFrom-Json
$extName = $pkgData.name
$extVersion = $pkgData.version
$extPublisher = $pkgData.publisher
$extId = "$extPublisher.$extName"
$vsixName = "$extName-$extVersion.vsix"

# ═══════════════════════════════════════════════════════════════════════
#  Banner
# ═══════════════════════════════════════════════════════════════════════

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════════╗" -ForegroundColor Magenta
Write-Host "║         SharpClaw VS Code Extension — Publish           ║" -ForegroundColor Magenta
Write-Host "╚══════════════════════════════════════════════════════════╝" -ForegroundColor Magenta
Write-Host ""
Write-Host "  Extension     : $extId v$extVersion" -ForegroundColor White
Write-Host "  Extension Dir : $extensionDir" -ForegroundColor DarkGray
Write-Host "  Repo Root     : $repoRoot" -ForegroundColor DarkGray
Write-Host "  Mode          : $(if ($DevHost) { 'Extension Development Host' } else { 'VSIX Install' })" -ForegroundColor White
Write-Host "  Workspace     : $WorkspacePath" -ForegroundColor DarkGray
Write-Host "  Timestamp     : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor DarkGray

# ═══════════════════════════════════════════════════════════════════════
#  Prerequisites check
# ═══════════════════════════════════════════════════════════════════════

Write-Step "Checking prerequisites"

# Node.js
$nodeVersion = $null
try {
    $nodeVersion = (& node --version 2>&1).ToString().Trim()
    Write-Ok "Node.js: $nodeVersion"
    Write-Detail "Path: $(Get-Command node | Select-Object -ExpandProperty Source)"
} catch {
    Write-Fail "Node.js not found. Install from https://nodejs.org/"
    exit 1
}

# npm
$npmVersion = $null
try {
    $npmVersion = (& npm --version 2>&1).ToString().Trim()
    Write-Ok "npm: v$npmVersion"
} catch {
    Write-Fail "npm not found."
    exit 1
}

# VS Code CLI
$codeVersion = $null
try {
    $codeVersion = (& $CodeBinary --version 2>&1 | Select-Object -First 1).ToString().Trim()
    Write-Ok "VS Code: $codeVersion"
    Write-Detail "Binary: $CodeBinary"
} catch {
    Write-Fail "VS Code CLI ('$CodeBinary') not found. Ensure 'code' is on PATH or use -CodeBinary."
    exit 1
}

# TypeScript compiler
try {
    Push-Location $extensionDir
    $tscVersion = (& npx tsc --version 2>&1).ToString().Trim()
    Write-Ok "TypeScript: $tscVersion"
} catch {
    Write-Warn "TypeScript not yet available (will be installed with npm install)."
} finally {
    Pop-Location
}

Write-Detail "Elapsed: $([math]::Round((Get-Elapsed), 1))s"

# ═══════════════════════════════════════════════════════════════════════
#  Clean
# ═══════════════════════════════════════════════════════════════════════

Write-Step "Cleaning build artifacts"

# Remove compiled output
if (Test-Path $outDir) {
    Remove-Item $outDir -Recurse -Force
    Write-Ok "Removed: out/"
} else {
    Write-Detail "out/ already clean."
}

# Remove old .vsix files
$oldVsix = Get-ChildItem $extensionDir -Filter "*.vsix" -File
if ($oldVsix.Count -gt 0) {
    foreach ($f in $oldVsix) {
        Remove-Item $f.FullName -Force
        Write-Ok "Removed: $($f.Name)"
    }
} else {
    Write-Detail "No old .vsix files found."
}

# Optionally remove node_modules
if ($CleanModules) {
    $nodeModules = Join-Path $extensionDir "node_modules"
    if (Test-Path $nodeModules) {
        Remove-Item $nodeModules -Recurse -Force
        Write-Ok "Removed: node_modules/ (clean install requested)"
    }
}

Write-Detail "Elapsed: $([math]::Round((Get-Elapsed), 1))s"

# ═══════════════════════════════════════════════════════════════════════
#  npm install
# ═══════════════════════════════════════════════════════════════════════

Write-Step "Installing dependencies"

Push-Location $extensionDir
try {
    $npmCmd = if ($CleanModules) { "ci" } else { "install" }
    Write-Detail "Running: npm $npmCmd"

    $npmOutput = & npm $npmCmd 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "npm $npmCmd failed (exit code $LASTEXITCODE)."
        Write-Host $npmOutput -ForegroundColor Red
        exit 1
    }

    # Count installed packages
    $nodeModulesPath = Join-Path $extensionDir "node_modules"
    if (Test-Path $nodeModulesPath) {
        $pkgCount = (Get-ChildItem $nodeModulesPath -Directory | Where-Object { $_.Name -notlike ".*" }).Count
        Write-Ok "npm $npmCmd completed ($pkgCount packages)"
    } else {
        Write-Ok "npm $npmCmd completed"
    }

    if ($Verbose) {
        $npmOutput.Split("`n") | Where-Object { $_.Trim() } | ForEach-Object {
            Write-Detail $_.Trim()
        }
    }
} finally {
    Pop-Location
}

Write-Detail "Elapsed: $([math]::Round((Get-Elapsed), 1))s"

# ═══════════════════════════════════════════════════════════════════════
#  Compile TypeScript
# ═══════════════════════════════════════════════════════════════════════

Write-Step "Compiling TypeScript"

Push-Location $extensionDir
try {
    Write-Detail "Running: npx tsc -p ./"

    $tscOutput = & npx tsc -p ./ 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "TypeScript compilation failed."
        Write-Host $tscOutput -ForegroundColor Red
        exit 1
    }

    # Count output files
    if (Test-Path $outDir) {
        $jsFiles = (Get-ChildItem $outDir -Filter "*.js" -Recurse).Count
        $mapFiles = (Get-ChildItem $outDir -Filter "*.map" -Recurse).Count
        Write-Ok "Compilation succeeded ($jsFiles .js files, $mapFiles .map files)"
    } else {
        Write-Fail "Compilation produced no output directory."
        exit 1
    }

    if ($Verbose -and $tscOutput.Trim()) {
        $tscOutput.Split("`n") | Where-Object { $_.Trim() } | ForEach-Object {
            Write-Detail $_.Trim()
        }
    }
} finally {
    Pop-Location
}

Write-Detail "Elapsed: $([math]::Round((Get-Elapsed), 1))s"

# ═══════════════════════════════════════════════════════════════════════
#  Package VSIX
# ═══════════════════════════════════════════════════════════════════════

if (-not $SkipPackage -and -not ($DevHost -and $SkipInstall)) {
    Write-Step "Packaging VSIX"

    Push-Location $extensionDir
    try {
        Write-Detail "Running: npx @vscode/vsce package"

        $vsceOutput = & npx @vscode/vsce package --no-update-package-json 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) {
            Write-Fail "VSIX packaging failed."
            Write-Host $vsceOutput -ForegroundColor Red
            exit 1
        }

        $vsixFile = Get-ChildItem $extensionDir -Filter "*.vsix" -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($vsixFile) {
            $vsixSizeKB = [math]::Round($vsixFile.Length / 1KB, 1)
            Write-Ok "Packaged: $($vsixFile.Name) ($vsixSizeKB KB)"
            Write-Detail "Path: $($vsixFile.FullName)"
        } else {
            Write-Fail "No .vsix file produced."
            exit 1
        }

        if ($Verbose) {
            $vsceOutput.Split("`n") | Where-Object { $_.Trim() } | ForEach-Object {
                Write-Detail $_.Trim()
            }
        }
    } finally {
        Pop-Location
    }

    Write-Detail "Elapsed: $([math]::Round((Get-Elapsed), 1))s"
}

# ═══════════════════════════════════════════════════════════════════════
#  Uninstall old + install new
# ═══════════════════════════════════════════════════════════════════════

if (-not $SkipInstall -and -not $DevHost) {
    Write-Step "Installing extension into VS Code"

    # List installed extensions to check for existing install
    Write-Detail "Checking for existing installation..."
    $installed = & $CodeBinary --list-extensions 2>&1 | Out-String
    if ($installed -match [regex]::Escape($extId)) {
        Write-Detail "Found existing: $extId — uninstalling..."
        $uninstallOutput = & $CodeBinary --uninstall-extension $extId 2>&1 | Out-String
        if ($LASTEXITCODE -eq 0) {
            Write-Ok "Uninstalled previous: $extId"
        } else {
            Write-Warn "Uninstall returned exit code $LASTEXITCODE (may not have been installed)."
            if ($Verbose) { Write-Detail $uninstallOutput.Trim() }
        }
    } else {
        Write-Detail "No existing installation found."
    }

    # Install the new .vsix
    $vsixFile = Get-ChildItem $extensionDir -Filter "*.vsix" -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $vsixFile) {
        Write-Fail "No .vsix file found to install. Run without -SkipPackage."
        exit 1
    }

    Write-Detail "Installing: $($vsixFile.FullName)"
    $installOutput = & $CodeBinary --install-extension $vsixFile.FullName --force 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "Extension install failed (exit code $LASTEXITCODE)."
        Write-Host $installOutput -ForegroundColor Red
        exit 1
    }

    Write-Ok "Installed: $($vsixFile.Name)"
    if ($Verbose) { Write-Detail $installOutput.Trim() }

    Write-Detail "Elapsed: $([math]::Round((Get-Elapsed), 1))s"
}

# ═══════════════════════════════════════════════════════════════════════
#  Launch VS Code
# ═══════════════════════════════════════════════════════════════════════

if (-not $SkipLaunch) {
    Write-Step "Launching VS Code"

    if ($DevHost) {
        # Extension Development Host — loads extension from source (like F5 in VS 2026)
        $absExtDir = (Resolve-Path $extensionDir).Path
        Write-Detail "Mode: Extension Development Host"
        Write-Detail "Extension path: $absExtDir"
        Write-Detail "Workspace: $WorkspacePath"
        Write-Detail "Running: $CodeBinary --extensionDevelopmentPath=`"$absExtDir`" `"$WorkspacePath`""

        & $CodeBinary --extensionDevelopmentPath="$absExtDir" "$WorkspacePath"

        if ($LASTEXITCODE -ne 0) {
            Write-Warn "VS Code exited with code $LASTEXITCODE."
        } else {
            Write-Ok "Extension Development Host launched."
        }
    } else {
        # Normal launch — extension is already installed via .vsix
        Write-Detail "Mode: Normal (extension installed)"
        Write-Detail "Workspace: $WorkspacePath"
        Write-Detail "Running: $CodeBinary `"$WorkspacePath`""

        & $CodeBinary "$WorkspacePath"

        if ($LASTEXITCODE -ne 0) {
            Write-Warn "VS Code exited with code $LASTEXITCODE."
        } else {
            Write-Ok "VS Code launched."
        }
    }

    Write-Detail "Elapsed: $([math]::Round((Get-Elapsed), 1))s"
}

# ═══════════════════════════════════════════════════════════════════════
#  Summary
# ═══════════════════════════════════════════════════════════════════════

$totalSeconds = [math]::Round((Get-Elapsed), 1)

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════════╗" -ForegroundColor Magenta
Write-Host "║                       Summary                           ║" -ForegroundColor Magenta
Write-Host "╚══════════════════════════════════════════════════════════╝" -ForegroundColor Magenta
Write-Host ""
Write-Host "  Extension  : $extId v$extVersion" -ForegroundColor White
Write-Host "  Mode       : $(if ($DevHost) { 'DevHost' } else { 'VSIX Install' })" -ForegroundColor White
Write-Host "  Duration   : ${totalSeconds}s" -ForegroundColor White
Write-Host "  Steps      : $script:stepIndex completed" -ForegroundColor White

if ($script:warnings.Count -gt 0) {
    Write-Host "  Warnings   : $($script:warnings.Count)" -ForegroundColor Yellow
    foreach ($w in $script:warnings) {
        Write-Host "    ⚠ $w" -ForegroundColor Yellow
    }
} else {
    Write-Host "  Warnings   : 0" -ForegroundColor Green
}

if (-not $SkipLaunch) {
    Write-Host ""
    if ($DevHost) {
        Write-Host "  The Extension Development Host window should now be open." -ForegroundColor DarkGray
        Write-Host "  Open the Output panel → 'SharpClaw' channel to see bridge logs." -ForegroundColor DarkGray
    } else {
        Write-Host "  VS Code should now be open with the extension installed." -ForegroundColor DarkGray
        Write-Host "  Run 'SharpClaw: Connect' from the Command Palette (Ctrl+Shift+P)." -ForegroundColor DarkGray
        Write-Host "  Check the Output panel → 'SharpClaw' channel for bridge logs." -ForegroundColor DarkGray
    }
}

Write-Host ""
