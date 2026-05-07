param(
    [string]$Configuration = "Release",
    [string]$RootSuffix = "Exp",
    [switch]$Launch,
    [switch]$Restart,
    [switch]$SkipUninstall,
    [string]$DevEnvPath,
    [string]$VsixInstallerPath
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "SharpClaw.VS2026Extension.csproj"
$extensionId = "SharpClaw.VS2026Extension.d5e3a8f1-4c2b-4e9d-8f1a-2b3c4d5e6f7a"

function Resolve-VsInstallPath {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path -LiteralPath $vswhere) {
        $path = & $vswhere -latest -products * -version "[18.0,19.0)" -property installationPath
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($path)) {
            return $path.Trim()
        }
    }

    $fallback = "D:\Program Files\Microsoft Visual Studio\18\Community"
    if (Test-Path -LiteralPath $fallback) {
        return $fallback
    }

    throw "Could not locate Visual Studio 2026. Pass -DevEnvPath and -VsixInstallerPath explicitly."
}

$vsInstallPath = Resolve-VsInstallPath

if ([string]::IsNullOrWhiteSpace($DevEnvPath)) {
    $DevEnvPath = Join-Path $vsInstallPath "Common7\IDE\devenv.exe"
}

if ([string]::IsNullOrWhiteSpace($VsixInstallerPath)) {
    $VsixInstallerPath = Join-Path $vsInstallPath "Common7\IDE\VSIXInstaller.exe"
}

if (-not (Test-Path -LiteralPath $DevEnvPath)) {
    throw "devenv.exe was not found: $DevEnvPath"
}

if (-not (Test-Path -LiteralPath $VsixInstallerPath)) {
    throw "VSIXInstaller.exe was not found: $VsixInstallerPath"
}

# Keep marketplace/package identity stable, but make every experimental build
# visibly newer to VSIXInstaller and the VisualStudio.Extensibility service host.
$now = Get-Date
$devVersion = "1.1.$($now.ToString('MMdd')).$($now.ToString('HHmm'))"

if ($Restart) {
    $rootSuffixPattern = [regex]::Escape($RootSuffix)
    $expInstances = Get-CimInstance Win32_Process -Filter "name = 'devenv.exe'" |
        Where-Object { $_.CommandLine -match "(?i)/rootSuffix(:|\s+)$rootSuffixPattern(\s|$)" }

    foreach ($instance in $expInstances) {
        Write-Host "Stopping existing Visual Studio $RootSuffix instance (pid $($instance.ProcessId))..."
        Stop-Process -Id $instance.ProcessId -Force -ErrorAction SilentlyContinue
    }

    if ($expInstances) {
        Start-Sleep -Seconds 2
    }
}

Write-Host "Building SharpClaw VS2026 experimental VSIX version $devVersion..."
dotnet build $projectPath -c $Configuration `
    "/p:Version=$devVersion" `
    "/p:AssemblyVersion=$devVersion" `
    "/p:FileVersion=$devVersion"

if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}

$vsixPath = Join-Path $repoRoot "bin\$Configuration\net8.0-windows\SharpClaw.VS2026Extension.vsix"
if (-not (Test-Path -LiteralPath $vsixPath)) {
    throw "VSIX was not produced: $vsixPath"
}

if (-not $SkipUninstall) {
    Write-Host "Removing any existing SharpClaw package from the $RootSuffix hive..."
    & $VsixInstallerPath "/quiet" "/rootSuffix:$RootSuffix" "/uninstall:$extensionId"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "VSIX uninstall returned exit code $LASTEXITCODE; continuing with install."
    }
}

Write-Host "Installing $vsixPath into the $RootSuffix hive..."
& $VsixInstallerPath "/quiet" "/rootSuffix:$RootSuffix" $vsixPath
if ($LASTEXITCODE -ne 0) {
    throw "VSIX install failed with exit code $LASTEXITCODE."
}

if ($Launch) {
    Write-Host "Launching Visual Studio with /rootsuffix $RootSuffix /log..."
    Start-Process -FilePath $DevEnvPath -ArgumentList "/rootsuffix $RootSuffix /log"
}

Write-Host "Installed SharpClaw experimental VSIX version $devVersion."
